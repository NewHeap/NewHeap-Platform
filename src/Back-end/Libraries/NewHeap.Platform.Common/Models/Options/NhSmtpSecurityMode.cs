namespace NewHeap.Platform.Common.Models.Options;

/// <summary>
/// How <see cref="Services.NhMailService"/> secures its connection to the SMTP server, set with
/// <see cref="MailServiceSettings.SecurityMode"/>. Every mode validates the server's certificate; no
/// mode falls back to plain text when TLS fails.
/// </summary>
public enum NhSmtpSecurityMode
{
    /// <summary>
    /// Connects in plain text and upgrades the connection with STARTTLS before anything else is sent,
    /// usually on port 587. A server that does not offer STARTTLS fails the delivery instead of
    /// receiving the message, the user name and the password in plain text.
    /// </summary>
    StartTlsRequired = 0,

    /// <summary>
    /// Speaks TLS from the first byte (SMTPS), usually on port 465.
    /// </summary>
    ImplicitTls = 1,

    /// <summary>
    /// Sends in plain text without TLS. Use it only for a development mail server on the local
    /// machine or a relay on a network that protects the traffic itself.
    /// </summary>
    None = 2,
}
