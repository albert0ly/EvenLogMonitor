using EventLogMonitor.WebhookReceiver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SmsProviderOptions>(builder.Configuration.GetSection("SmsProvider"));
builder.Services.AddHttpClient("sms-provider", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<SmsProviderSender>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok("Webhook receiver is running."));

app.MapPost("/api/sms/webhook", async (SmsWebhookRequest request, SmsProviderSender sender, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest("User cannot send SMS. Message is required.");

    if (request.PhoneNumbers is null || request.PhoneNumbers.Count == 0)
        return Results.BadRequest("User cannot send SMS. Phone numbers are required.");

    var result = await sender.SendAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.Run();
