using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using Xunit;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace NewHeap.Platform.Common.Tests;

/// <summary>
/// <see cref="NhMailService"/> secures its SMTP connection as <see cref="MailServiceSettings.SecurityMode"/>
/// says, against real Mailpit servers (<see cref="MailpitTestServer"/>): required STARTTLS, implicit TLS
/// on its own port and plain text, authentication only with a user, an untrusted certificate that
/// delivers nothing without a plain-text retry, cancellation, and the startup refusal of a mode that
/// does not exist.
/// </summary>
public sealed class NhMailServiceSmtpSecurityTests
{
    private const string MailUser = "mail-sender";

    [Fact]
    public async Task RequiredStartTlsUpgradesTheConnectionAndAuthenticatesWithTheUser()
    {
        var server = await MailpitTestServer.GetAsync(MailpitTransport.StartTlsOffered);
        var subjectToken = NewSubjectToken();
        var mailService = new TestAuthorityMailService(Settings(server, NhSmtpSecurityMode.StartTlsRequired, user: MailUser));

        await mailService.SendAsync(Message(subjectToken));

        var delivered = Assert.Single(await server.FindBySubjectAsync(subjectToken));
        Assert.Equal(MailUser, delivered.Username);
        Assert.Equal("recipient@sample.test", Assert.Single(delivered.To).Address);
        Assert.True(mailService.ValidatedCertificates > 0, "The delivery did not negotiate TLS.");
        Assert.Equal(1, mailService.Authentications);
    }

    [Fact]
    public async Task ImplicitTlsDeliversOnTheServersSmtpsPort()
    {
        var server = await MailpitTestServer.GetAsync(MailpitTransport.ImplicitTls);
        var subjectToken = NewSubjectToken();
        var mailService = new TestAuthorityMailService(Settings(server, NhSmtpSecurityMode.ImplicitTls, user: MailUser));

        await mailService.SendAsync(Message(subjectToken));

        var delivered = Assert.Single(await server.FindBySubjectAsync(subjectToken));
        Assert.Equal(MailUser, delivered.Username);
        Assert.True(mailService.ValidatedCertificates > 0, "The delivery did not negotiate TLS.");
        Assert.Equal(1, mailService.Authentications);
    }

    [Fact]
    public async Task WithoutAUserTheServiceDoesNotAuthenticate()
    {
        var server = await MailpitTestServer.GetAsync(MailpitTransport.StartTlsOffered);
        var subjectToken = NewSubjectToken();
        var mailService = new TestAuthorityMailService(Settings(server, NhSmtpSecurityMode.StartTlsRequired, user: ""));

        await mailService.SendAsync(Message(subjectToken));

        var delivered = Assert.Single(await server.FindBySubjectAsync(subjectToken));
        Assert.True(string.IsNullOrEmpty(delivered.Username), $"The delivery authenticated as '{delivered.Username}'.");
        Assert.Equal(0, mailService.Authentications);
        Assert.True(mailService.ValidatedCertificates > 0, "The delivery did not negotiate TLS.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequiredStartTlsRefusesAServerWithoutStartTls(bool legacyEnableSsl)
    {
        var server = await MailpitTestServer.GetAsync(MailpitTransport.PlainText);
        var subjectToken = NewSubjectToken();
        // Settings without a mode keep EnableSsl's meaning: true requires STARTTLS, as before.
        var settings = legacyEnableSsl
            ? Settings(server, securityMode: null, enableSsl: true)
            : Settings(server, NhSmtpSecurityMode.StartTlsRequired);
        var mailService = new TestAuthorityMailService(settings);

        await Assert.ThrowsAsync<NotSupportedException>(() => mailService.SendAsync(Message(subjectToken)));

        Assert.Empty(await server.FindBySubjectAsync(subjectToken));
    }

    [Theory]
    [InlineData(MailpitTransport.StartTlsOffered, NhSmtpSecurityMode.StartTlsRequired, false)]
    [InlineData(MailpitTransport.StartTlsOffered, null, true)]
    [InlineData(MailpitTransport.ImplicitTls, NhSmtpSecurityMode.ImplicitTls, false)]
    public async Task AnUntrustedCertificateDeliversNothingAndIsNotRetriedInPlainText(
        MailpitTransport transport,
        NhSmtpSecurityMode? securityMode,
        bool legacyEnableSsl)
    {
        var server = await MailpitTestServer.GetAsync(transport);
        var subjectToken = NewSubjectToken();
        // The default validation: the host does not trust the test authority.
        var mailService = new NhMailService(
            Options.Create(Settings(server, securityMode, legacyEnableSsl, MailUser)),
            NullLogger<NhMailService>.Instance);

        await Assert.ThrowsAsync<SslHandshakeException>(() => mailService.SendAsync(Message(subjectToken)));

        // The STARTTLS server also takes mail in plain text, so a retry without TLS would be found.
        Assert.Empty(await server.FindBySubjectAsync(subjectToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoneSendsInPlainTextEvenWhenTheServerOffersStartTls(bool namedMode)
    {
        var server = await MailpitTestServer.GetAsync(MailpitTransport.StartTlsOffered);
        var subjectToken = NewSubjectToken();
        // A named mode wins over EnableSsl; settings without a mode and EnableSsl false send in
        // plain text, as before.
        var settings = namedMode
            ? Settings(server, NhSmtpSecurityMode.None, enableSsl: true)
            : Settings(server, securityMode: null, enableSsl: false);
        var mailService = new TestAuthorityMailService(settings);

        await mailService.SendAsync(Message(subjectToken));

        Assert.Single(await server.FindBySubjectAsync(subjectToken));
        Assert.Equal(0, mailService.ValidatedCertificates);
    }

    [Fact]
    public async Task ACancelledDeliveryThrowsAndDeliversNothing()
    {
        var server = await MailpitTestServer.GetAsync(MailpitTransport.StartTlsOffered);
        var subjectToken = NewSubjectToken();
        var mailService = new TestAuthorityMailService(Settings(server, NhSmtpSecurityMode.StartTlsRequired, user: MailUser));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mailService.SendAsync(Message(subjectToken), cancellationToken: cancellation.Token));

        Assert.Empty(await server.FindBySubjectAsync(subjectToken));
    }

    [Fact]
    public async Task CancellationStopsADeliveryThatWaitsForTheServer()
    {
        // A server that accepts the connection and never greets: without cancellation the client
        // would wait for its two-minute timeout.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var settings = new MailServiceSettings
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            SecurityMode = NhSmtpSecurityMode.StartTlsRequired,
            User = "",
            Password = "",
            FromEmail = "sender@sample.test",
            FromDisplayName = "Mail security tests",
        };
        var mailService = new NhMailService(Options.Create(settings), NullLogger<NhMailService>.Instance);
        using var cancellation = new CancellationTokenSource();
        using var acceptBound = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        var delivery = mailService.SendAsync(Message(NewSubjectToken()), cancellationToken: cancellation.Token);
        using var connection = await listener.AcceptTcpClientAsync(acceptBound.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
    }

    [Theory]
    [InlineData(null, true, NhSmtpSecurityMode.StartTlsRequired)]
    [InlineData(null, false, NhSmtpSecurityMode.None)]
    [InlineData(NhSmtpSecurityMode.ImplicitTls, false, NhSmtpSecurityMode.ImplicitTls)]
    [InlineData(NhSmtpSecurityMode.None, true, NhSmtpSecurityMode.None)]
    [InlineData(NhSmtpSecurityMode.StartTlsRequired, false, NhSmtpSecurityMode.StartTlsRequired)]
    public void AMissingModeFollowsEnableSslAndANamedModeWins(
        NhSmtpSecurityMode? securityMode,
        bool enableSsl,
        NhSmtpSecurityMode expected)
    {
        var settings = new MailServiceSettings { SecurityMode = securityMode, EnableSsl = enableSsl };

        Assert.Equal(expected, settings.ResolveSecurityMode());
    }

    [Fact]
    public void AnUndefinedModeCannotBeResolved()
    {
        var settings = new MailServiceSettings { SecurityMode = (NhSmtpSecurityMode)42 };

        var exception = Assert.Throws<InvalidOperationException>(() => settings.ResolveSecurityMode());
        Assert.Contains("SecurityMode 42", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("StartTlsRequired")]
    [InlineData("ImplicitTls")]
    [InlineData("None")]
    public async Task TheHostStartsWithANamedMode(string securityMode)
    {
        using var host = BuildHost(securityMode);

        await host.StartAsync();

        var settings = host.Services.GetRequiredService<IOptions<MailServiceSettings>>().Value;
        Assert.Equal(Enum.Parse<NhSmtpSecurityMode>(securityMode), settings.SecurityMode);
        await host.StopAsync();
    }

    [Fact]
    public async Task TheHostRefusesToStartWithAnUndefinedMode()
    {
        // Configuration binds a number that no mode has.
        using var host = BuildHost("7");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("SecurityMode 7 is not an NhSmtpSecurityMode", exception.Message, StringComparison.Ordinal);
    }

    private static IHost BuildHost(string securityMode)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mail:Host"] = "smtp.sample.test",
            ["Mail:Port"] = "587",
            ["Mail:SecurityMode"] = securityMode,
            ["Mail:FromEmail"] = "sender@sample.test",
        });
        builder.Services
            .AddNewHeapPlatformCommon(NewHeapCommonOptions.Builder(builder.Configuration).Build())
            .WithMail(mail => builder.Configuration.GetSection("Mail").Bind(mail));

        return builder.Build();
    }

    private static string NewSubjectToken()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static MailMessage Message(string subjectToken)
    {
        var message = new MailMessage
        {
            Subject = $"Mail security {subjectToken}",
            Body = "A message of the mail security tests.",
        };
        message.To.Add(new MailAddress("recipient@sample.test"));

        return message;
    }

    private static MailServiceSettings Settings(
        MailpitEndpoint server,
        NhSmtpSecurityMode? securityMode,
        bool enableSsl = false,
        string user = "")
    {
        return new MailServiceSettings
        {
            Host = server.Host,
            Port = server.SmtpPort,
            SecurityMode = securityMode,
            EnableSsl = enableSsl,
            User = user,
            Password = user.Length == 0 ? "" : "mail-test-password",
            FromEmail = "sender@sample.test",
            FromDisplayName = "Mail security tests",
        };
    }

    /// <summary>
    /// A mail service that trusts the test authority as a host trusts its root certificates: the
    /// server's certificate must chain to it and name the host. It counts the certificates it saw and
    /// the authentications of its client, so a test knows whether TLS was negotiated and whether the
    /// client authenticated.
    /// </summary>
    private sealed class TestAuthorityMailService(MailServiceSettings settings)
        : NhMailService(Options.Create(settings), NullLogger<NhMailService>.Instance)
    {
        private int _validatedCertificates;
        private int _authentications;

        public int ValidatedCertificates => Volatile.Read(ref _validatedCertificates);

        public int Authentications => Volatile.Read(ref _authentications);

        internal override SmtpClient CreateSmtpClient()
        {
            var client = base.CreateSmtpClient();
            client.ServerCertificateValidationCallback = ValidateWithTestAuthority;
            client.Authenticated += (_, _) => Interlocked.Increment(ref _authentications);

            return client;
        }

        private bool ValidateWithTestAuthority(
            object sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            Interlocked.Increment(ref _validatedCertificates);

            // A missing certificate or one for another name fails as it would for a trusted root.
            if (certificate is null
                || (sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            {
                return false;
            }

            using var testChain = new X509Chain();
            testChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            testChain.ChainPolicy.CustomTrustStore.Add(MailpitTestServer.Authority);
            // The test authority publishes no revocation information.
            testChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var serverCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

            return testChain.Build(serverCertificate);
        }
    }
}
