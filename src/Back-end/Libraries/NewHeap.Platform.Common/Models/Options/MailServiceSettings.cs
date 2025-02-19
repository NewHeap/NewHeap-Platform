namespace NewHeap.Platform.Common.Models.Options;

public partial class MailServiceSettings
{
    public string Transport { get; set; }  = null!;
    public string Host { get; set; } = null!;
    public int Port { get; set; }
    public bool EnableSsl { get; set; }
    public string User { get; set; } = null!;
    public string Password { get; set; } = null!;
    public string FromEmail { get; set; } = null!;
    public string FromDisplayName { get; set; } = null!;
    public bool IsSendRescritedActive { get; set; }
    public List<string> RestrictedEmailWhitelist { get; set; } = [];
}