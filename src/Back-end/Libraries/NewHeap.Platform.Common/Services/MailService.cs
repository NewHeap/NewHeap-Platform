using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using WebAPI.Models.Options;

namespace NewHeap.Platform.Common.Services;
public class MailService
{
    protected readonly EmailSettings _emailSettings;

    public MailService(IOptions<EmailSettings> emailSettings)
    {
        _emailSettings = emailSettings.Value;
    }

    public async Task SendAsync(MailMessage mailMessage, MailAddress fromMailAddress = null, string formDisplayName = null)
    {
        if (mailMessage == null)
        {
            throw new ArgumentNullException();
        }

        formDisplayName ??= _emailSettings.FromDisplayName;
        mailMessage.From = fromMailAddress ?? new MailAddress(_emailSettings.FromEmail, formDisplayName);
        mailMessage.BodyEncoding = Encoding.UTF8;

        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if ((!string.IsNullOrWhiteSpace(environment) && environment.ToLower() != "production"))
        {
            mailMessage.Subject = (string.IsNullOrWhiteSpace(mailMessage.Subject))
                ? $"[{environment}]"
                : $"[{environment}] - " + mailMessage.Subject;
        }

        if (_emailSettings.IsSendRescritedActive)
        {
            foreach (var restrictedAllowedEntry in _emailSettings.RestrictedEmailWhitelist)
            {
                var toInvalidEntries = mailMessage.To.Where(x => !x.Address.EndsWith(restrictedAllowedEntry)).ToList();
                foreach (var invalidEntry in toInvalidEntries)
                {
                    mailMessage.To.Remove(invalidEntry);
                }

                var ccInvalidEntries = mailMessage.CC.Where(x => !x.Address.EndsWith(restrictedAllowedEntry)).ToList();
                foreach (var invalidEntry in ccInvalidEntries)
                {
                    mailMessage.CC.Remove(invalidEntry);
                }

                var bccInvalidEntries = mailMessage.Bcc.Where(x => !x.Address.EndsWith(restrictedAllowedEntry)).ToList();
                foreach (var invalidEntry in bccInvalidEntries)
                {
                    mailMessage.Bcc.Remove(invalidEntry);
                }
            }

            if (!mailMessage.To.Any())
            {
                return;
            }
        }

        using (SmtpClient smtp = new SmtpClient(_emailSettings.Host, _emailSettings.Port))
        {
            smtp.Credentials = new NetworkCredential(_emailSettings.User, _emailSettings.Password);
            smtp.EnableSsl = _emailSettings.EnableSsl;
            await smtp.SendMailAsync(mailMessage);
        }
    }
}
