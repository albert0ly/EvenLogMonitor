namespace EventLogMonitor.WebhookReceiver;

public sealed class SmsProviderOptions
{
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = "https://api.inforu.co.il/SendMessageXml.ashx";
    public string ProxyHost { get; set; } = string.Empty;
    public int ProxyPort { get; set; }
    public InforuOptions Inforu { get; set; } = new();
}

public sealed class InforuOptions
{
    public InforuUserOptions User { get; set; } = new();
    public InforuContentOptions Content { get; set; } = new();
    public InforuSettingsOptions Settings { get; set; } = new();
}

public sealed class InforuUserOptions
{
    public string Username { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
}

public sealed class InforuContentOptions
{
    public string Type { get; set; } = "sms";
    public string Message { get; set; } = "{0}";
}

public sealed class InforuSettingsOptions
{
    public string Sender { get; set; } = string.Empty;
}
