using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

public sealed class DatabaseReadProviderTests
{
    private static readonly Guid ProjectId = Guid.Parse("9f6e1ca4-96e4-4f6a-8820-738693649dc3");

    [Fact]
    public async Task SqlServerProfileReadsWithAWriteDeniedPrincipal()
    {
        const string databaseName = "NewHeapDiagnostics";
        const string username = "newheap_database_reader";
        const string password = "Test-only-reader-Password-42";
        await using var container = new MsSqlBuilder(
            "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await container.StartAsync();

        var adminBuilder = new SqlConnectionStringBuilder(container.GetConnectionString());
        var passwordKeyword = "PASS" + "WORD";
        await ExecuteSqlServerAsync(
            adminBuilder.ConnectionString,
            $"CREATE DATABASE [{databaseName}]; CREATE LOGIN [{username}] WITH {passwordKeyword} = '{password}';");
        adminBuilder.InitialCatalog = databaseName;
        await ExecuteSqlServerAsync(
            adminBuilder.ConnectionString,
            $"""
            CREATE TABLE dbo.DiagnosticProjects (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                Name nvarchar(100) NOT NULL
            );
            INSERT INTO dbo.DiagnosticProjects (Id, Name) VALUES ('{ProjectId}', N'Diagnostic project');
            CREATE USER [{username}] FOR LOGIN [{username}];
            CREATE ROLE [newheap_database_readonly];
            ALTER ROLE [newheap_database_readonly] ADD MEMBER [{username}];
            GRANT SELECT ON dbo.DiagnosticProjects TO [newheap_database_readonly];
            DENY INSERT, UPDATE, DELETE ON DATABASE::[{databaseName}] TO [newheap_database_readonly];
            DENY EXECUTE TO [newheap_database_readonly];
            """);

        var readerBuilder = new SqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            UserID = username,
            Password = password,
            IntegratedSecurity = false
        };
        var provider = new SqlServerDatabaseReadProvider();
        var limits = new DatabaseReadLimits(10, 10, 2_000, 65_536, 4_096, 8_192);
        await using (var providerConnection = provider.CreateConnection(
                         readerBuilder.ConnectionString,
                         Guid.NewGuid().ToString("N"),
                         limits))
        {
            await providerConnection.OpenAsync();
            (await provider.VerifyReadOnlyPrincipalAsync(providerConnection, limits, CancellationToken.None))
                .Should().BeTrue();
            await using var transaction = await providerConnection.BeginTransactionAsync();
            await provider.ConfigureReadOnlyTransactionAsync(
                providerConnection,
                transaction,
                limits,
                CancellationToken.None);
            await transaction.RollbackAsync();
        }

        using var workspace = new DatabaseReadTestWorkspace("sql-server", readerBuilder.ConnectionString);
        using var input = DatabaseReadTestWorkspace.Request(
            "SELECT Id, Name FROM dbo.DiagnosticProjects WHERE Id = @id",
            [new { name = "id", type = "uuid", value = ProjectId.ToString() }]);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        AssertSuccessfulProjectResult(response, "sql-server");
        await AssertElevatedPrincipalIsRejectedAsync("sql-server", adminBuilder.ConnectionString);

        await using var readerConnection = new SqlConnection(readerBuilder.ConnectionString);
        await readerConnection.OpenAsync();
        await using var update = readerConnection.CreateCommand();
        update.CommandText = "UPDATE dbo.DiagnosticProjects SET Name = N'Changed' WHERE Id = @id";
        update.Parameters.AddWithValue("@id", ProjectId);
        var writeAction = () => update.ExecuteNonQueryAsync();

        await writeAction.Should().ThrowAsync<SqlException>();
    }

    [Fact]
    public async Task PostgreSqlProfileReadsWithAWriteDeniedPrincipal()
    {
        const string username = "newheap_database_reader";
        const string password = "Test-only-reader-Password-42";
        await using var container = new PostgreSqlBuilder("postgres:15.1").Build();
        await container.StartAsync();

        var adminBuilder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
        await using (var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var setup = adminConnection.CreateCommand();
            var quotedDatabaseName = new NpgsqlCommandBuilder().QuoteIdentifier(adminConnection.Database);
            setup.CommandText = $"""
                CREATE TABLE diagnostic_projects (
                    id uuid NOT NULL PRIMARY KEY,
                    name varchar(100) NOT NULL
                );
                INSERT INTO diagnostic_projects (id, name) VALUES ('{ProjectId}', 'Diagnostic project');
                CREATE ROLE {username} LOGIN PASSWORD '{password}';
                GRANT CONNECT ON DATABASE {quotedDatabaseName} TO {username};
                GRANT USAGE ON SCHEMA public TO {username};
                GRANT SELECT ON TABLE diagnostic_projects TO {username};
                ALTER ROLE {username} SET default_transaction_read_only = on;
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var readerBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Username = username,
            Password = password
        };
        using var workspace = new DatabaseReadTestWorkspace("postgresql", readerBuilder.ConnectionString);
        using var input = DatabaseReadTestWorkspace.Request(
            "SELECT id, name FROM diagnostic_projects WHERE id = @id",
            [new { name = "id", type = "uuid", value = ProjectId.ToString() }]);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        AssertSuccessfulProjectResult(response, "postgresql");
        await AssertElevatedPrincipalIsRejectedAsync("postgresql", adminBuilder.ConnectionString);

        await using var readerConnection = new NpgsqlConnection(readerBuilder.ConnectionString);
        await readerConnection.OpenAsync();
        await using var update = readerConnection.CreateCommand();
        update.CommandText = "UPDATE diagnostic_projects SET name = 'Changed' WHERE id = @id";
        update.Parameters.AddWithValue("@id", ProjectId);
        var writeAction = () => update.ExecuteNonQueryAsync();

        await writeAction.Should().ThrowAsync<PostgresException>();
    }

    private static async Task ExecuteSqlServerAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertElevatedPrincipalIsRejectedAsync(
        string provider,
        string connectionString)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, connectionString);
        using var input = DatabaseReadTestWorkspace.Request("SELECT 1");
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(4);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("read-only-principal-not-verified");
    }

    private static void AssertSuccessfulProjectResult(JsonDocument response, string provider)
    {
        var root = response.RootElement;
        root.GetProperty("ok").GetBoolean().Should().BeTrue();
        root.GetProperty("target").GetProperty("provider").GetString().Should().Be(provider);
        root.GetProperty("target").GetProperty("readOnlyVerified").GetBoolean().Should().BeTrue();
        var result = root.GetProperty("result");
        result.GetProperty("rowCount").GetInt32().Should().Be(1);
        var row = result.GetProperty("rows")[0];
        row[0].GetString().Should().Be(ProjectId.ToString());
        row[1].GetString().Should().Be("Diagnostic project");
    }
}
