using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using NewHeap.Platform.Tests.Relational;
using Npgsql;
using System.Text;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// A real TLS handshake with a server certificate that no certificate authority on the machine
/// trusts. SQL Server uses the shared test server, whose certificate is self-signed, with
/// certificate validation turned on. The shared PostgreSQL server runs without TLS, so the
/// PostgreSQL case lets Npgsql validate the certificate of a local TLS endpoint instead.
/// </summary>
public sealed class DatabaseReadTlsTests
{
    private const string Canary = "NEVER-RETURN-THIS-PASSWORD";

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task UntrustedServerCertificateIsClassifiedWithoutTheProviderMessage(
        RelationalTestProvider provider)
    {
        await using var endpoint = provider == RelationalTestProvider.PostgreSql
            ? PostgreSqlTlsTestEndpoint.Start()
            : null;
        var (providerName, connectionString, host) = provider == RelationalTestProvider.SqlServer
            ? await SqlServerConnectionAsync()
            : PostgreSqlConnection(endpoint!);
        using var workspace = new DatabaseReadTestWorkspace(providerName, connectionString);
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["schema", "--profiles", workspace.ProfileCatalogPath, "--search", "project"],
            input,
            output);

        var outputText = Encoding.UTF8.GetString(output.ToArray());
        exitCode.Should().Be(5, outputText);
        outputText.Should().NotContain(Canary);
        outputText.Should().NotContain(host);
        outputText.Should().NotContain("certificate chain", "the raw provider message must not be returned");
        outputText.Should().NotContain("RemoteCertificateValidationCallback");
        using var response = JsonDocument.Parse(outputText);
        response.RootElement.TryGetProperty("schema", out _).Should().BeFalse();
        var error = response.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("database-query-failed");
        error.GetProperty("classification").GetString().Should().Be("tls-certificate-untrusted");
        error.GetProperty("provider").GetString().Should().Be(providerName);
        error.GetProperty("stage").GetString().Should().Be("connection-open");
        error.GetProperty("transient").GetBoolean().Should().BeFalse();
        error.GetProperty("retryHint").GetString().Should().Be("certificate-authority-trust-required");
        if (provider == RelationalTestProvider.SqlServer)
        {
            error.GetProperty("providerCode").GetString().Should().Be("-2146893019");
        }
        else
        {
            error.TryGetProperty("providerCode", out _).Should().BeFalse();
        }
    }

    private static async Task<(string Provider, string ConnectionString, string Host)> SqlServerConnectionAsync()
    {
        var server = new SqlConnectionStringBuilder(
            await RelationalTestServer.For(RelationalTestProvider.SqlServer).GetConnectionStringAsync());
        var builder = new SqlConnectionStringBuilder(server.ConnectionString)
        {
            InitialCatalog = "master",
            UserID = "diagnostics_reader",
            Password = Canary,
            IntegratedSecurity = false,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = false
        };

        return ("sql-server", builder.ConnectionString, builder.DataSource);
    }

    private static (string Provider, string ConnectionString, string Host) PostgreSqlConnection(
        PostgreSqlTlsTestEndpoint endpoint)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = endpoint.Port,
            Database = "diagnostics",
            Username = "diagnostics_reader",
            Password = Canary,
            SslMode = SslMode.VerifyFull
        };

        return ("postgresql", builder.ConnectionString, $"127.0.0.1:{endpoint.Port}");
    }
}
