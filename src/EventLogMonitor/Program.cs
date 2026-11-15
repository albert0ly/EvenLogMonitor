using EventLogMonitor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Logging.AddEventLog(settings =>
{
    settings.LogName = "Application";
    settings.SourceName = "EventLogMonitor";
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Event Log Monitor";
});

builder.Services.Configure<EventLogMonitorOptions>(builder.Configuration.GetSection("EventLogMonitor"));
builder.Services.Configure<EmailNotificationOptions>(builder.Configuration.GetSection("Notifications:Email"));
builder.Services.Configure<SmsWebhookOptions>(builder.Configuration.GetSection("Notifications:SmsWebhook"));
builder.Services.AddHttpClient("sms-webhook", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddSingleton<IisWorkerProcessMap>();
builder.Services.AddHostedService<IisWorkerProcessMap>(sp => sp.GetRequiredService<IisWorkerProcessMap>());
builder.Services.AddHostedService<EventLogMonitorWorker>();

await builder.Build().RunAsync();
