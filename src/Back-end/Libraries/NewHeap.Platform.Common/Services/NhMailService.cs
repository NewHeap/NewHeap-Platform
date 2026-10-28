using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using NewHeap.Platform.Common.Models.Options;
using Microsoft.Extensions.Logging;
using System.Net.Mail;
using System.Text;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace NewHeap.Platform.Common.Services;

/// <summary>
/// Sends a <see cref="MailMessage"/> over SMTP with MailKit, secured as
/// <see cref="MailServiceSettings.SecurityMode"/> says (see <see cref="NhSmtpSecurityMode"/>).
/// </summary>
/// <remarks>
/// A delivery that fails throws the exception of MailKit or of the connection, for example
/// <see cref="SslHandshakeException"/> for a certificate the host does not trust,
/// <see cref="NotSupportedException"/> for a server without STARTTLS, <see cref="AuthenticationException"/>
/// for refused credentials and <see cref="MailKit.Net.Smtp.SmtpCommandException"/> for a refused
/// command. A cancelled delivery throws <see cref="OperationCanceledException"/>.
/// </remarks>
public partial class NhMailService
{
    protected readonly MailServiceSettings _emailSettings;
    protected readonly ILogger<NhMailService> _logger;

    public NhMailService(
        IOptions<MailServiceSettings> emailSettings,
        ILogger<NhMailService> logger)
    {
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public virtual async Task SendAsync(
        MailMessage mailMessage, 
        MailAddress? fromMailAddress = null,
        string? formDisplayName = null, 
        CancellationToken cancellationToken = default
        )
    {
        if (mailMessage == null)
        {
            throw new ArgumentNullException(nameof(mailMessage));
        }

        formDisplayName ??= _emailSettings.FromDisplayName;
        mailMessage.From = fromMailAddress ?? new MailAddress(_emailSettings.FromEmail, formDisplayName);
        mailMessage.BodyEncoding = Encoding.UTF8;

        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(environment) && environment.ToLower() != "production")
        {
            mailMessage.Subject = string.IsNullOrWhiteSpace(mailMessage.Subject)
                ? $"[{environment}]"
                : $"[{environment}] - " + mailMessage.Subject;
        }

        if (_emailSettings.IsSendRescritedActive)
        {
            var validTo = new List<MailAddress>();
            var validCC = new List<MailAddress>();
            var validBcc = new List<MailAddress>();

            foreach (var restrictedAllowedEntry in _emailSettings.RestrictedEmailWhitelist)
            {
                validTo.AddRange(mailMessage.To.Where(x => x.Address.EndsWith(restrictedAllowedEntry)).ToList());
                validCC.AddRange(mailMessage.CC.Where(x => x.Address.EndsWith(restrictedAllowedEntry)).ToList());
                validBcc.AddRange(mailMessage.Bcc.Where(x => x.Address.EndsWith(restrictedAllowedEntry)).ToList());
            }

            mailMessage.To.Clear();
            mailMessage.CC.Clear();
            mailMessage.Bcc.Clear();

            validTo.GroupBy(x => x.Address).Select(x => x.FirstOrDefault()).ToList().ForEach(x => mailMessage.To.Add(x!));
            validCC.GroupBy(x => x.Address).Select(x => x.FirstOrDefault()).ToList().ForEach(x => mailMessage.CC.Add(x!));
            validBcc.GroupBy(x => x.Address).Select(x => x.FirstOrDefault()).ToList().ForEach(x => mailMessage.Bcc.Add(x!));

            if (!mailMessage.To.Any())
            {
                _logger.LogDebug("Skipped mail delivery because recipient restrictions removed every recipient.");
                return;
            }
        }

        var secureSocketOptions = ToSecureSocketOptions(_emailSettings.ResolveSecurityMode());

        // The MIME parts read the streams of the message's attachments, which stay the caller's to
        // dispose, as they did with the framework's SMTP client.
        var mimeMessage = MimeMessage.CreateFromMailMessage(mailMessage);

        using var smtp = CreateSmtpClient();

        // One attempt in the configured mode: a failed TLS handshake or a server without STARTTLS
        // ends the delivery with its exception, never with a second attempt in plain text.
        await smtp.ConnectAsync(_emailSettings.Host, _emailSettings.Port, secureSocketOptions, cancellationToken);

        if (!string.IsNullOrEmpty(_emailSettings.User))
        {
            await smtp.AuthenticateAsync(_emailSettings.User, _emailSettings.Password ?? string.Empty, cancellationToken);
        }

        await smtp.SendAsync(mimeMessage, cancellationToken);

        _logger.LogDebug(
            "Mail delivery completed for {RecipientCount} recipient(s).",
            mailMessage.To.Count + mailMessage.CC.Count + mailMessage.Bcc.Count);

        await CloseAfterDeliveryAsync(smtp, cancellationToken);
    }

    /// <summary>
    /// The SMTP client of one delivery. It validates the server's certificate with MailKit's default
    /// validation, which checks revocation, and writes no protocol log, so neither the message nor the
    /// credentials reach a log.
    /// </summary>
    internal virtual SmtpClient CreateSmtpClient()
    {
        return new SmtpClient();
    }

    /// <summary>
    /// Ends the session with QUIT. The server accepted the message already, so a QUIT that fails or
    /// is cancelled does not report the delivery as failed; disposing the client closes the socket.
    /// </summary>
    private async Task CloseAfterDeliveryAsync(SmtpClient smtp, CancellationToken cancellationToken)
    {
        try
        {
            await smtp.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                "The SMTP session did not close cleanly after the delivery: {ExceptionType}.",
                exception.GetType().FullName);
        }
    }

    private static SecureSocketOptions ToSecureSocketOptions(NhSmtpSecurityMode securityMode)
    {
        return securityMode switch
        {
            // StartTls requires the upgrade; StartTlsWhenAvailable and Auto would send in plain text
            // to a server that does not offer it.
            NhSmtpSecurityMode.StartTlsRequired => SecureSocketOptions.StartTls,
            NhSmtpSecurityMode.ImplicitTls => SecureSocketOptions.SslOnConnect,
            NhSmtpSecurityMode.None => SecureSocketOptions.None,
            _ => throw new ArgumentOutOfRangeException(nameof(securityMode), securityMode, null),
        };
    }
}
