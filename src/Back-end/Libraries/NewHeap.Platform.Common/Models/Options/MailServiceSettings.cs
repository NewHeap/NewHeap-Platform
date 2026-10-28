namespace NewHeap.Platform.Common.Models.Options;

public partial class MailServiceSettings
{
    public string Transport { get; set; }  = null!;
    public string Host { get; set; } = null!;
    public int Port { get; set; }

    /// <summary>
    /// The security of settings that do not name a <see cref="SecurityMode"/>: <see langword="true"/>
    /// requires STARTTLS and <see langword="false"/> sends in plain text. Prefer
    /// <see cref="SecurityMode"/>, which wins when both are set.
    /// </summary>
    public bool EnableSsl { get; set; }

    /// <summary>
    /// How the connection to the SMTP server is secured. When it is <see langword="null"/>,
    /// <see cref="EnableSsl"/> decides as before; name the mode explicitly in new settings, for example
    /// <c>"SecurityMode": "StartTlsRequired"</c> with port 587 or <c>"ImplicitTls"</c> with port 465.
    /// A value outside <see cref="NhSmtpSecurityMode"/> refuses host start.
    /// </summary>
    public NhSmtpSecurityMode? SecurityMode { get; set; }

    /// <summary>
    /// The user name the mail service authenticates with. Leave it empty for a server that accepts
    /// mail without authentication; the service then does not authenticate.
    /// </summary>
    public string User { get; set; } = null!;
    public string Password { get; set; } = null!;
    public string FromEmail { get; set; } = null!;
    public string FromDisplayName { get; set; } = null!;
    public bool IsSendRescritedActive { get; set; }
    public List<string> RestrictedEmailWhitelist { get; set; } = [];

    /// <summary>
    /// The security mode in effect: <see cref="SecurityMode"/> when it is set, otherwise
    /// <see cref="NhSmtpSecurityMode.StartTlsRequired"/> when <see cref="EnableSsl"/> is
    /// <see langword="true"/> and <see cref="NhSmtpSecurityMode.None"/> when it is not.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="SecurityMode"/> holds a value that <see cref="NhSmtpSecurityMode"/> does not define.
    /// </exception>
    public NhSmtpSecurityMode ResolveSecurityMode()
    {
        if (SecurityMode is not { } securityMode)
        {
            return EnableSsl ? NhSmtpSecurityMode.StartTlsRequired : NhSmtpSecurityMode.None;
        }

        if (!Enum.IsDefined(securityMode))
        {
            throw new InvalidOperationException(NhMailServiceSettingsValidator.UndefinedSecurityModeMessage(securityMode));
        }

        return securityMode;
    }
}
