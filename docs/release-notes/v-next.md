# v-next

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: an explicit SMTP security mode

`MailServiceSettings.SecurityMode` names how `NhMailService` secures its SMTP connection
(`NhSmtpSecurityMode`): `StartTlsRequired` upgrades with STARTTLS and fails against a server that does
not offer it, `ImplicitTls` speaks TLS from the first byte (SMTPS), and `None` sends in plain text.
Settings without a mode keep the meaning of `EnableSsl`: `true` requires STARTTLS and `false` sends in
plain text, so existing settings connect as before. The service now sends with MailKit (MIT) and
converts the `MailMessage` with `MimeMessage.CreateFromMailMessage`, so `SendAsync(MailMessage, ...)`
and its callers stay as they are. It validates the server's certificate with no switch to skip it,
authenticates only when `User` is set, writes no protocol log, never retries in plain text after a
TLS failure and honours cancellation. `WithMail` and `AddNewHeapNotifications<TContext>` validate the
settings when the host starts.

Common now depends on MailKit 4.18.2 and MimeKit, and the release unit's packages require
`System.Security.Cryptography.Pkcs` 10.0.13 or later instead of 9.0.21: MimeKit needs the 10.0 line,
and 10.0.13 is its fix for CVE-2026-84008 (GHSA-846f-f764-76mx).

| Breaking change | Required action |
|---|---|
| `NhMailService.SendAsync` throws MailKit's exceptions instead of `System.Net.Mail.SmtpException`, such as `SslHandshakeException`, `NotSupportedException` for a server without STARTTLS, `AuthenticationException`, `SmtpCommandException` and `SmtpProtocolException`. | Catch those exceptions, or `Exception`, where code caught `SmtpException`. |
| A TLS connection now also checks the revocation of the server's certificate; a host that cannot reach the certificate authority's revocation service fails the delivery. | Allow the host outbound access to the authority's OCSP and CRL endpoints; trust a private authority in the host's certificate store. |
| Host start reads `MailServiceSettings` and fails when `SecurityMode` is not an `NhSmtpSecurityMode` or a value cannot be bound, instead of failing the first delivery. | Use `StartTlsRequired`, `ImplicitTls` or `None`, or leave `SecurityMode` out to keep `EnableSsl`, and fix values that do not bind. |
| `NhEmailNotificationDispatcher` rethrows the `OperationCanceledException` of a dispatch whose token was cancelled instead of returning a failed `TaskResult` with `DispatchError`. | Let the cancellation travel; catch `OperationCanceledException` where code calls `DispatchAsync` with a token it cancels. |

## NewHeap.Platform.Media.FileStructureStorage.SqlServer: cryptography dependency on the 10.0 line

The SQL Server media provider requires `System.Security.Cryptography.Pkcs` 10.0.13 or later instead
of 9.0.21, in line with the mail dependencies of `NewHeap.Platform.Common`; 10.0.13 is the 10.0
line's fix for CVE-2026-84008 (GHSA-846f-f764-76mx). No API or schema changes are required.
