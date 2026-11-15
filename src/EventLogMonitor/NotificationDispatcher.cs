using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventLogMonitor;

public sealed class NotificationDispatcher
{
    private readonly EmailNotificationOptions _email;
    private readonly SmsWebhookOptions _sms;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        IOptions<EmailNotificationOptions> email,
        IOptions<SmsWebhookOptions> sms,
        IHttpClientFactory httpClientFactory,
        ILogger<NotificationDispatcher> logger)
    {
        _email = email.Value;
        _sms = sms.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> SendAsync(EventRule rule, EventSnapshot item, CancellationToken cancellationToken)
    {
        var subject = $"[{item.Machine}] {rule.Name}: {item.LogName} Event {item.EventId}";
        var body = BuildMessage(rule, item);
        var sent = false;

        if (rule.NotifyEmail && _email.Enabled)
        {
            try
            {
                await SendEmailAsync(subject, body, cancellationToken);
                sent = true;
                _logger.LogInformation("Email notification sent for rule '{RuleName}'.", rule.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Email notification failed for rule '{RuleName}'.", rule.Name);
            }
        }

        if (rule.NotifySms && _sms.Enabled)
        {
            try
            {
                await SendSmsWebhookAsync(subject, body, rule, item, cancellationToken);
                sent = true;
                _logger.LogInformation("SMS webhook accepted notification for rule '{RuleName}'.", rule.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SMS webhook notification failed for rule '{RuleName}'.", rule.Name);
            }
        }

        if (!sent)
            _logger.LogWarning("Rule '{RuleName}' matched, but no notification was sent. Check rule channel flags and Notifications settings.", rule.Name);

        return sent;
    }

    private async Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_email.SmtpHost) || string.IsNullOrWhiteSpace(_email.From) || _email.To.Count == 0)
            throw new InvalidOperationException("Email is enabled, but SmtpHost, From, or To is not configured.");

        using var mail = new MailMessage
        {
            From = new MailAddress(_email.From),
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        foreach (var address in _email.To.Where(x => !string.IsNullOrWhiteSpace(x)))
            mail.To.Add(address);

        using var client = new SmtpClient(_email.SmtpHost, _email.SmtpPort)
        {
            EnableSsl = _email.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };

        if (!string.IsNullOrWhiteSpace(_email.UserName))
            client.Credentials = new NetworkCredential(_email.UserName, _email.Password);

        await client.SendMailAsync(mail, cancellationToken);
    }

    private async Task SendSmsWebhookAsync(
        string subject,
        string message,
        EventRule rule,
        EventSnapshot item,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_sms.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("SMS webhook URL must be an absolute HTTP or HTTPS URL.");

        var method = new HttpMethod(string.IsNullOrWhiteSpace(_sms.Method) ? "POST" : _sms.Method.ToUpperInvariant());
        var phoneNumbers = _sms.PhoneNumbers
            .Where(number => !string.IsNullOrWhiteSpace(number))
            .Select(number => number.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var body = _sms.BodyTemplate
            .Replace("{{message}}", JsonSerializer.Serialize(message), StringComparison.Ordinal)
            .Replace("{{subject}}", JsonSerializer.Serialize(subject), StringComparison.Ordinal)
            .Replace("{{machine}}", JsonSerializer.Serialize(item.Machine), StringComparison.Ordinal)
            .Replace("{{log}}", JsonSerializer.Serialize(item.LogName), StringComparison.Ordinal)
            .Replace("{{eventId}}", item.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{provider}}", JsonSerializer.Serialize(item.Provider), StringComparison.Ordinal)
            .Replace("{{time}}", JsonSerializer.Serialize(item.TimeCreated.ToString("O")), StringComparison.Ordinal)
            .Replace("{{rule}}", JsonSerializer.Serialize(rule.Name), StringComparison.Ordinal)
            .Replace("{{phoneNumbersJson}}", JsonSerializer.Serialize(phoneNumbers), StringComparison.Ordinal);

        using var request = new HttpRequestMessage(method, uri);
        request.Content = new StringContent(body, Encoding.UTF8, _sms.ContentType);

        foreach (var header in _sms.Headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
            else
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await _httpClientFactory.CreateClient("sms-webhook").SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"SMS webhook returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}");
    }

    private static string BuildMessage(EventRule rule, EventSnapshot item)
    {
        var message = item.Message;
        if (message.Length > 6000)
            message = message[..6000] + "\r\n[Message truncated]";

        return $"Rule: {rule.Name}\r\n" +
               $"Computer: {item.Machine}\r\n" +
               $"Log: {item.LogName}\r\n" +
               $"Provider: {item.Provider}\r\n" +
               $"Event ID: {item.EventId}\r\n" +
               $"Level: {item.LevelName}\r\n" +
               $"Time: {item.TimeCreated:O}\r\n" +
               $"Record ID: {item.RecordId?.ToString() ?? "n/a"}\r\n" +
               $"Process ID: {item.ProcessId?.ToString() ?? "n/a"}\r\n" +
               $"Application pool: {(item.ApplicationPoolNames.Count == 0 ? "unknown / not present" : string.Join(", ", item.ApplicationPoolNames))}\r\n\r\n" +
               message;
    }
}
