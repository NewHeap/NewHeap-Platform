using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models.Options;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace NewHeap.Platform.Common.Services;

public partial class MailService
{
    protected readonly MailServiceSettings _emailSettings;

    public MailService(IOptions<MailServiceSettings> emailSettings)
    {
        _emailSettings = emailSettings.Value;
    }

    public virtual async Task SendAsync(MailMessage mailMessage, MailAddress? fromMailAddress = null,
        string? formDisplayName = null)
    {
        if (mailMessage == null)
        {
            throw new ArgumentNullException();
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
                validTo = mailMessage.To.Where(x => x.Address.EndsWith(restrictedAllowedEntry)).ToList();
                validCC = mailMessage.CC.Where(x => x.Address.EndsWith(restrictedAllowedEntry)).ToList();
                validBcc = mailMessage.Bcc.Where(x => x.Address.EndsWith(restrictedAllowedEntry)).ToList();
            }

            mailMessage.To.Clear();
            mailMessage.CC.Clear();
            mailMessage.Bcc.Clear();

            validTo.GroupBy(x => x.Address).Select(x => x.FirstOrDefault()).ToList().ForEach(x => mailMessage.To.Add(x));
            validCC.GroupBy(x => x.Address).Select(x => x.FirstOrDefault()).ToList().ForEach(x => mailMessage.CC.Add(x));
            validBcc.GroupBy(x => x.Address).Select(x => x.FirstOrDefault()).ToList().ForEach(x => mailMessage.Bcc.Add(x));

            if (!mailMessage.To.Any())
            {
                return;
            }
        }

        using (SmtpClient smtp = new(_emailSettings.Host, _emailSettings.Port))
        {
            smtp.Credentials = new NetworkCredential(_emailSettings.User, _emailSettings.Password);
            smtp.EnableSsl = _emailSettings.EnableSsl;
            await smtp.SendMailAsync(mailMessage);
        }
    }
}