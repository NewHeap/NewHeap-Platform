namespace NewHeap.Platform.Common.Models.Options;

public partial class EmailServiceSettings
{
    public string Transport { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }
    public bool EnableSsl { get; set; }
    public string User { get; set; }
    public string Password { get; set; }
    public string FromEmail { get; set; }
    public string FromDisplayName { get; set; }
    public bool IsSendRescritedActive { get; set; }
    public List<string> RestrictedEmailWhitelist { get; set; }
}
