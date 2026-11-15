using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace EventLogMonitor.WebhookReceiver;

public sealed class SmsProviderSender
{
    private readonly SmsProviderOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SmsProviderSender> _logger;

    public SmsProviderSender(
        IOptions<SmsProviderOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<SmsProviderSender> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<object> SendAsync(SmsWebhookRequest request, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return new { accepted = false, message = "User cannot send SMS. Provider is disabled." };

        if (!Uri.TryCreate(_options.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("SMS provider URL must be absolute HTTP or HTTPS.");
        }

        var numbers = request.PhoneNumbers
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (numbers.Length == 0)
            return new { accepted = false, message = "User cannot send SMS. No valid phone numbers." };

        var sent = 0;
        var failed = 0;
        var details = new List<object>();

        foreach (var phone in numbers)
        {
            try
            {
                var payload = BuildInforuXml(phone, string.Format(_options.Inforu.Content.Message, request.Message));
                var encoded = Uri.EscapeDataString(payload).Replace("%20", "+", StringComparison.Ordinal);
                var body = $"InforuXML={encoded}";

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
                httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                if (!string.IsNullOrWhiteSpace(_options.ProxyHost) && _options.ProxyPort > 0)
                {
                    var handler = new HttpClientHandler
                    {
                        Proxy = new WebProxy(_options.ProxyHost.Trim(), _options.ProxyPort),
                        UseProxy = true
                    };

                    using var proxyClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                    using var proxyResponse = await proxyClient.SendAsync(httpRequest, cancellationToken);
                    var proxyResponseBody = await proxyResponse.Content.ReadAsStringAsync(cancellationToken);

                    if (!proxyResponse.IsSuccessStatusCode)
                    {
                        failed++;
                        details.Add(new { phone, status = (int)proxyResponse.StatusCode, response = proxyResponseBody });
                        continue;
                    }

                    sent++;
                    details.Add(new { phone, status = (int)proxyResponse.StatusCode, response = proxyResponseBody });
                    continue;
                }

                using var response = await _httpClientFactory.CreateClient("sms-provider").SendAsync(httpRequest, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    failed++;
                    details.Add(new { phone, status = (int)response.StatusCode, response = responseBody });
                    continue;
                }

                sent++;
                details.Add(new { phone, status = (int)response.StatusCode, response = responseBody });
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "SMS send failed for {Phone}", phone);
                details.Add(new { phone, error = ex.Message });
            }
        }

        return new { accepted = sent > 0, sent, failed, details };
    }

    private string BuildInforuXml(string phone, string message)
    {
        var xml = new XElement("Inforu",
            new XElement("User",
                new XElement("Username", _options.Inforu.User.Username ?? string.Empty),
                new XElement("ApiToken", _options.Inforu.User.ApiToken ?? string.Empty)),
            new XElement("Content",
                new XAttribute("Type", string.IsNullOrWhiteSpace(_options.Inforu.Content.Type) ? "sms" : _options.Inforu.Content.Type),
                new XElement("Message", message ?? string.Empty)),
            new XElement("Recipients",
                new XElement("PhoneNumber", phone)),
            new XElement("Settings",
                new XElement("Sender", _options.Inforu.Settings.Sender ?? string.Empty)));

        return xml.ToString(SaveOptions.DisableFormatting);
    }
}
