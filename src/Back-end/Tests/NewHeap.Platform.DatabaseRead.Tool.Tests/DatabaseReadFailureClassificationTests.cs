using AwesomeAssertions;
using Npgsql;
using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

public sealed class DatabaseReadFailureClassificationTests
{
    private const string UnsafeCanary = "PRIVATE-CERTIFICATE-SUBJECT-MUST-NOT-LEAK";
    private const string UnsafeDetail = "Unsafe provider detail " + UnsafeCanary + ".";

    [Theory]
    [InlineData(0)]
    [InlineData(50_000)]
    public void SqlServerConnectionOpenFailureKeepsTheFirstSafeProviderCode(
        int providerCode)
    {
        var exception = new TestDatabaseException("Unsafe provider detail.");

        var failure = SqlServerDatabaseReadProvider.ClassifySqlErrorNumbers(
            [providerCode],
            exception,
            DatabaseReadExecutionStage.ConnectionOpen);

        failure.Classification.Should().Be("connection-failed");
        failure.Provider.Should().Be("sql-server");
        failure.ProviderCode.Should().Be(providerCode.ToString());
        failure.Transient.Should().BeTrue();
        failure.RetryHint.Should().Be("network-access-required");
        failure.Message.Should().NotContain(exception.Message);
    }

    [Fact]
    public void NestedSocketFailureIsRecognizedOutsideTheConnectionOpenStage()
    {
        var exception = new TestDatabaseException(
            "Unsafe provider detail.",
            new SocketException((int)SocketError.HostNotFound));

        var failure = SqlServerDatabaseReadProvider.ClassifySqlErrorNumbers(
            [50_001],
            exception,
            DatabaseReadExecutionStage.QueryExecution);

        failure.Classification.Should().Be("connection-failed");
        failure.ProviderCode.Should().Be("50001");
        failure.RetryHint.Should().Be("network-access-required");
    }

    [Theory]
    [InlineData(18456, "authentication-failed")]
    [InlineData(4060, "database-not-found")]
    public void SqlServerCredentialAndDatabaseFailuresRemainSpecific(
        int providerCode,
        string classification)
    {
        var failure = SqlServerDatabaseReadProvider.ClassifySqlErrorNumbers(
            [providerCode],
            new TestDatabaseException("Unsafe provider detail."),
            DatabaseReadExecutionStage.ConnectionOpen);

        failure.Classification.Should().Be(classification);
        failure.ProviderCode.Should().Be(providerCode.ToString());
        failure.Transient.Should().BeFalse();
        failure.RetryHint.Should().BeNull();
    }

    [Fact]
    public void SqlServerClassificationDoesNotReplaceTheFirstSafeProviderCode()
    {
        var failure = SqlServerDatabaseReadProvider.ClassifySqlErrorNumbers(
            [50_000, 18_456],
            new TestDatabaseException("Unsafe provider detail."),
            DatabaseReadExecutionStage.ConnectionOpen);

        failure.Classification.Should().Be("authentication-failed");
        failure.ProviderCode.Should().Be("50000");
    }

    [Theory]
    [InlineData("28P01", "authentication-failed", false, null)]
    [InlineData("3D000", "database-not-found", false, null)]
    [InlineData("ZZ999", "connection-failed", true, "network-access-required")]
    public void PostgreSqlConnectionFailuresKeepSafeSqlStateMetadata(
        string sqlState,
        string classification,
        bool transient,
        string? retryHint)
    {
        var failure = PostgreSqlDatabaseReadProvider.ClassifySqlState(
            sqlState,
            new TestDatabaseException("Unsafe provider detail."),
            DatabaseReadExecutionStage.ConnectionOpen);

        failure.Classification.Should().Be(classification);
        failure.Provider.Should().Be("postgresql");
        failure.ProviderCode.Should().Be(sqlState);
        failure.Transient.Should().Be(transient);
        failure.RetryHint.Should().Be(retryHint);
    }

    [Theory]
    [InlineData((int)DatabaseReadExecutionStage.ReadOnlyVerification)]
    [InlineData((int)DatabaseReadExecutionStage.QueryExecution)]
    [InlineData((int)DatabaseReadExecutionStage.SchemaExecution)]
    public void PostgreSqlDriverCommandTimeoutIsAStatementTimeout(int stageValue)
    {
        var exception = new NpgsqlException(UnsafeDetail, new TimeoutException(UnsafeDetail));

        var failure = new PostgreSqlDatabaseReadProvider().ClassifyException(
            exception,
            (DatabaseReadExecutionStage)stageValue);

        failure.Classification.Should().Be("statement-timeout");
        failure.Provider.Should().Be("postgresql");
        failure.ProviderCode.Should().BeNull();
        failure.Transient.Should().BeFalse();
        failure.RetryHint.Should().BeNull();
        failure.Message.Should().NotContain(UnsafeCanary);
    }

    [Fact]
    public void PostgreSqlDriverConnectTimeoutKeepsItsNetworkRetryHint()
    {
        var exception = new NpgsqlException(UnsafeDetail, new TimeoutException(UnsafeDetail));

        var failure = new PostgreSqlDatabaseReadProvider().ClassifyException(
            exception,
            DatabaseReadExecutionStage.ConnectionOpen);

        failure.Classification.Should().Be("connection-failed");
        failure.Transient.Should().BeTrue();
        failure.RetryHint.Should().Be("network-access-required");
        failure.Message.Should().NotContain(UnsafeCanary);
    }

    [Theory]
    [InlineData((int)DatabaseReadExecutionStage.QueryExecution)]
    [InlineData((int)DatabaseReadExecutionStage.SchemaExecution)]
    public void PostgreSqlSocketFailuresRemainConnectionFailures(int stageValue)
    {
        var exception = new NpgsqlException(
            UnsafeDetail,
            new TimeoutException(UnsafeDetail, new SocketException((int)SocketError.TimedOut)));

        var failure = new PostgreSqlDatabaseReadProvider().ClassifyException(
            exception,
            (DatabaseReadExecutionStage)stageValue);

        failure.Classification.Should().Be("connection-failed");
        failure.Transient.Should().BeTrue();
        failure.RetryHint.Should().Be("network-access-required");
    }

    [Theory]
    [InlineData(-2146893019, "tls-certificate-untrusted", false, "certificate-authority-trust-required")]
    [InlineData(-2146762487, "tls-certificate-untrusted", false, "certificate-authority-trust-required")]
    [InlineData(-2146762486, "tls-certificate-untrusted", false, "certificate-authority-trust-required")]
    [InlineData(18456, "authentication-failed", false, null)]
    [InlineData(10060, "connection-failed", true, "network-access-required")]
    public void SqlServerSeparatesAnUntrustedChainFromRejectedCredentialsAndNetworkFailures(
        int providerCode,
        string classification,
        bool transient,
        string? retryHint)
    {
        var exception = new TestDatabaseException(UnsafeDetail);

        var failure = SqlServerDatabaseReadProvider.ClassifySqlErrorNumbers(
            [providerCode],
            exception,
            DatabaseReadExecutionStage.ConnectionOpen);

        failure.Classification.Should().Be(classification);
        failure.Provider.Should().Be("sql-server");
        failure.ProviderCode.Should().Be(providerCode.ToString(CultureInfo.InvariantCulture));
        failure.Transient.Should().Be(transient);
        failure.RetryHint.Should().Be(retryHint);
        failure.Message.Should().NotContain(UnsafeCanary);
    }

    [Fact]
    public void SqlServerCertificateRejectedByTheManagedNetworkLayerIsUntrusted()
    {
        var exception = new TestDatabaseException(
            UnsafeDetail,
            new AuthenticationException(UnsafeDetail));

        var failure = SqlServerDatabaseReadProvider.ClassifySqlErrorNumbers(
            [35],
            exception,
            DatabaseReadExecutionStage.ConnectionOpen);

        failure.Classification.Should().Be("tls-certificate-untrusted");
        failure.ProviderCode.Should().Be("35");
        failure.Transient.Should().BeFalse();
        failure.RetryHint.Should().Be("certificate-authority-trust-required");
        failure.Message.Should().NotContain(UnsafeCanary);
    }

    /// <summary>
    /// Only an untrusted chain is mapped where SqlClient names the failed check: a host name the
    /// certificate does not name, an expired certificate and a protocol failure keep their
    /// connection classification.
    /// </summary>
    [Theory]
    [InlineData(-2146893022)]
    [InlineData(-2146893016)]
    [InlineData(-2146893018)]
    public void SqlServerOtherTlsFailuresRemainConnectionFailures(int providerCode)
    {
        var failure = SqlServerDatabaseReadProvider.ClassifySqlErrorNumbers(
            [providerCode],
            new TestDatabaseException(UnsafeDetail),
            DatabaseReadExecutionStage.ConnectionOpen);

        failure.Classification.Should().Be("connection-failed");
        failure.ProviderCode.Should().Be(providerCode.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("untrusted-chain", "tls-certificate-untrusted", null, false, "certificate-authority-trust-required")]
    [InlineData("rejected-credentials", "authentication-failed", "28P01", false, null)]
    [InlineData("network-failure", "connection-failed", null, true, "network-access-required")]
    [InlineData("handshake-failure", "connection-failed", null, true, "network-access-required")]
    public void PostgreSqlSeparatesAnUntrustedChainFromRejectedCredentialsAndNetworkFailures(
        string scenario,
        string classification,
        string? providerCode,
        bool transient,
        string? retryHint)
    {
        DbException exception = scenario switch
        {
            "untrusted-chain" => new NpgsqlException(
                UnsafeDetail,
                new AuthenticationException(UnsafeDetail)),
            "rejected-credentials" => new PostgresException(UnsafeDetail, "FATAL", "FATAL", "28P01"),
            "network-failure" => new NpgsqlException(
                UnsafeDetail,
                new SocketException((int)SocketError.TimedOut)),
            "handshake-failure" => new NpgsqlException(
                UnsafeDetail,
                new AuthenticationException(UnsafeDetail, new Win32Exception(unchecked((int)0x80090331)))),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

        var failure = new PostgreSqlDatabaseReadProvider().ClassifyException(
            exception,
            DatabaseReadExecutionStage.ConnectionOpen);

        failure.Classification.Should().Be(classification);
        failure.Provider.Should().Be("postgresql");
        failure.ProviderCode.Should().Be(providerCode);
        failure.Transient.Should().Be(transient);
        failure.RetryHint.Should().Be(retryHint);
        failure.Message.Should().NotContain(UnsafeCanary);
    }

    [Theory]
    [InlineData((int)DatabaseReadExecutionStage.ConnectionOpen, "connection-open")]
    [InlineData((int)DatabaseReadExecutionStage.ReadOnlyVerification, "readonly-verification")]
    [InlineData((int)DatabaseReadExecutionStage.QueryExecution, "query-execution")]
    [InlineData((int)DatabaseReadExecutionStage.SchemaExecution, "schema-execution")]
    public void ExecutionStagesHaveStableResponseValues(
        int stageValue,
        string responseValue)
    {
        var context = new DatabaseReadExecutionContext();

        context.Enter((DatabaseReadExecutionStage)stageValue);

        context.GetResponseStage().Should().Be(responseValue);
    }

    private sealed class TestDatabaseException : DbException
    {
        public TestDatabaseException(string message)
            : base(message)
        {
        }

        public TestDatabaseException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
