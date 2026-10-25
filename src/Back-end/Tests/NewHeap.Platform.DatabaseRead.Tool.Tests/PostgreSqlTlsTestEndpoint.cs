using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// A local endpoint that answers the PostgreSQL protocol's TLS request and then presents a
/// self-signed certificate that no certificate authority on the machine trusts.
/// </summary>
/// <remarks>
/// The shared PostgreSQL test container runs without TLS, and turning TLS on there would change
/// every PostgreSQL test of the process. This endpoint lets Npgsql perform its real TLS
/// handshake and certificate validation, so the tool sees the exception chain an untrusted
/// server certificate produces. No PostgreSQL message is ever exchanged.
/// </remarks>
internal sealed class PostgreSqlTlsTestEndpoint : IAsyncDisposable
{
    private const int SslRequestCode = 80877103;
    private const int GssEncryptionRequestCode = 80877104;

    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;

    private PostgreSqlTlsTestEndpoint(TcpListener listener, X509Certificate2 certificate)
    {
        _listener = listener;
        _certificate = certificate;
        _acceptLoop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static PostgreSqlTlsTestEndpoint Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return new PostgreSqlTlsTestEndpoint(listener, CreateUntrustedCertificate());
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        _certificate.Dispose();
        _stopping.Dispose();
    }

    private static X509Certificate2 CreateUntrustedCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")],
            critical: false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        // SChannel cannot serve TLS with an ephemeral key, so the key is loaded from a PFX export.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private async Task AcceptAsync()
    {
        var connections = new List<Task>();
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            connections.Add(ServeAsync(client));
        }

        await Task.WhenAll(connections);
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = new byte[8];
                while (true)
                {
                    await stream.ReadExactlyAsync(request, _stopping.Token);
                    var code = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4));
                    if (code == GssEncryptionRequestCode)
                    {
                        await stream.WriteAsync("N"u8.ToArray(), _stopping.Token);
                        continue;
                    }

                    if (code != SslRequestCode)
                    {
                        return;
                    }

                    await stream.WriteAsync("S"u8.ToArray(), _stopping.Token);
                    await using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                    await tls.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = _certificate },
                        _stopping.Token);

                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or AuthenticationException or OperationCanceledException
                    or ObjectDisposedException or SocketException or EndOfStreamException)
            {
                // The client rejects the certificate and closes the connection, as intended.
            }
        }
    }
}
