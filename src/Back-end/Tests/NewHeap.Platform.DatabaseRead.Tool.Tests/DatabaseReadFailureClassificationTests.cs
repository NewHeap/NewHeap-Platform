using AwesomeAssertions;
using System.Data.Common;
using System.Net.Sockets;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

public sealed class DatabaseReadFailureClassificationTests
{
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
