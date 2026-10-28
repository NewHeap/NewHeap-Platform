using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace NewHeap.Platform.Common.Tests;

/// <summary>
/// The SMTP security a <see cref="MailpitTestServer"/> offers. Public, as theories take it.
/// </summary>
public enum MailpitTransport
{
    /// <summary>STARTTLS is offered and optional: the server also takes mail in plain text.</summary>
    StartTlsOffered,

    /// <summary>TLS from the first byte (SMTPS) on the server's SMTP port.</summary>
    ImplicitTls,

    /// <summary>No TLS at all: the server offers no STARTTLS.</summary>
    PlainText,
}

/// <summary>
/// One Mailpit server per <see cref="MailpitTransport"/> for the whole test process, started when a
/// test first uses it. The TLS servers present a certificate of <see cref="Authority"/>, a certificate
/// authority made for this process that the host does not trust, and accept any user name and
/// password, so a test can see whether and as whom the client authenticated.
/// </summary>
/// <remarks>
/// Tests share the servers and tell their messages apart by a subject of their own. The servers are
/// never stopped explicitly: the Testcontainers resource reaper removes them when the process exits.
/// </remarks>
internal sealed class MailpitTestServer
{
    public const string Image = "axllent/mailpit:v1.31.6";

    private const int SmtpPort = 1025;
    private const int HttpPort = 8025;
    private const string CertificatePath = "/certs/server.pem";
    private const string KeyPath = "/certs/server-key.pem";

    private static readonly Lazy<TestCertificateAuthority> CertificateAuthority = new(TestCertificateAuthority.Create);

    private static readonly MailpitTestServer StartTlsOfferedServer = new(MailpitTransport.StartTlsOffered);
    private static readonly MailpitTestServer ImplicitTlsServer = new(MailpitTransport.ImplicitTls);
    private static readonly MailpitTestServer PlainTextServer = new(MailpitTransport.PlainText);

    private readonly MailpitTransport _transport;
    private readonly Lazy<Task<MailpitEndpoint>> _endpoint;

    /// <summary>
    /// The started container, referenced for the life of the process; the resource reaper stops it.
    /// </summary>
    private IContainer? _container;

    private MailpitTestServer(MailpitTransport transport)
    {
        _transport = transport;
        _endpoint = new Lazy<Task<MailpitEndpoint>>(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The certificate authority that issued the TLS servers' certificates.</summary>
    public static X509Certificate2 Authority => CertificateAuthority.Value.Certificate;

    public static Task<MailpitEndpoint> GetAsync(MailpitTransport transport)
    {
        var server = transport switch
        {
            MailpitTransport.StartTlsOffered => StartTlsOfferedServer,
            MailpitTransport.ImplicitTls => ImplicitTlsServer,
            MailpitTransport.PlainText => PlainTextServer,
            _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null),
        };

        return server._endpoint.Value;
    }

    private async Task<MailpitEndpoint> StartAsync()
    {
        var builder = new ContainerBuilder(Image)
            .WithPortBinding(SmtpPort, true)
            .WithPortBinding(HttpPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged(@"\[smtpd\] starting on")
                .UntilHttpRequestIsSucceeded(request => request.ForPort(HttpPort).ForPath("/readyz")));

        if (_transport != MailpitTransport.PlainText)
        {
            var hostname = builder.Build().Hostname;
            var serverCertificate = CertificateAuthority.Value.IssueServerCertificate(hostname);
            // The TLS servers accept any credentials and record the user name with the message;
            // Mailpit offers authentication only with TLS.
            builder = builder
                .WithResourceMapping(Encoding.ASCII.GetBytes(serverCertificate.CertificatePem), CertificatePath)
                .WithResourceMapping(Encoding.ASCII.GetBytes(serverCertificate.KeyPem), KeyPath)
                .WithEnvironment("MP_SMTP_TLS_CERT", CertificatePath)
                .WithEnvironment("MP_SMTP_TLS_KEY", KeyPath)
                .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "true");
        }

        if (_transport == MailpitTransport.ImplicitTls)
        {
            builder = builder.WithEnvironment("MP_SMTP_REQUIRE_TLS", "true");
        }

        var container = builder.Build();
        _container = container;
        await container.StartAsync();

        var api = new UriBuilder(Uri.UriSchemeHttp, container.Hostname, container.GetMappedPublicPort(HttpPort)).Uri;
        return new MailpitEndpoint(container.Hostname, container.GetMappedPublicPort(SmtpPort), api);
    }
}

/// <summary>
/// Where a started <see cref="MailpitTestServer"/> listens, and its REST API.
/// </summary>
internal sealed class MailpitEndpoint(string host, int smtpPort, Uri api)
{
    private static readonly HttpClient Http = new();

    public string Host { get; } = host;

    public int SmtpPort { get; } = smtpPort;

    /// <summary>
    /// The messages whose subject contains <paramref name="subjectToken"/>, as Mailpit's search finds
    /// them. Mailpit stores a message before it answers the SMTP client, so a delivery that returned is
    /// found at once.
    /// </summary>
    public async Task<IReadOnlyList<MailpitMessage>> FindBySubjectAsync(string subjectToken)
    {
        var query = Uri.EscapeDataString($"subject:\"{subjectToken}\"");
        var result = await Http.GetFromJsonAsync<MailpitSearchResult>(new Uri(api, $"api/v1/search?query={query}"));
        return result?.Messages ?? [];
    }
}

internal sealed record MailpitSearchResult(
    [property: JsonPropertyName("messages")] IReadOnlyList<MailpitMessage> Messages);

internal sealed record MailpitMessage(
    [property: JsonPropertyName("Subject")] string Subject,
    [property: JsonPropertyName("Username")] string? Username,
    [property: JsonPropertyName("To")] IReadOnlyList<MailpitAddress> To);

internal sealed record MailpitAddress(
    [property: JsonPropertyName("Address")] string Address);

/// <summary>
/// A certificate authority made for one test process, and server certificates that it issues for
/// the Docker host and the loopback names.
/// </summary>
internal sealed class TestCertificateAuthority
{
    private readonly X509Certificate2 _authorityWithKey;

    private TestCertificateAuthority(X509Certificate2 authorityWithKey)
    {
        _authorityWithKey = authorityWithKey;
        Certificate = X509CertificateLoader.LoadCertificate(authorityWithKey.RawData);
    }

    /// <summary>The authority's certificate without its key, for a trust store of a test.</summary>
    public X509Certificate2 Certificate { get; }

    public static TestCertificateAuthority Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=NewHeap mail test authority", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        return new TestCertificateAuthority(request.CreateSelfSigned(now.AddDays(-1), now.AddDays(2)));
    }

    public (string CertificatePem, string KeyPem) IssueServerCertificate(string hostname)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")],
            false));
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_authorityWithKey, true, false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        if (IPAddress.TryParse(hostname, out var address))
        {
            names.AddIpAddress(address);
        }
        else
        {
            names.AddDnsName(hostname);
        }

        request.CertificateExtensions.Add(names.Build());

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.Create(
            _authorityWithKey,
            now.AddDays(-1),
            now.AddDays(1),
            RandomNumberGenerator.GetBytes(16));

        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }
}
