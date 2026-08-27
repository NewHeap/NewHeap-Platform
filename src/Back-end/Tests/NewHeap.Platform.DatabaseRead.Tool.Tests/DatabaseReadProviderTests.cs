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
                Name nvarchar(100) NOT NULL,
                Status nvarchar(32) NOT NULL
            );
            CREATE TABLE dbo.RestrictedProjects (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                Secret nvarchar(100) NOT NULL
            );
            CREATE INDEX IX_DiagnosticProjects_Name_Id
                ON dbo.DiagnosticProjects (Name ASC, Id DESC)
                INCLUDE (Status);
            INSERT INTO dbo.DiagnosticProjects (Id, Name, Status)
            VALUES ('{ProjectId}', N'Diagnostic project', N'active');
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
            "SELECT TOP (10) Id, Name FROM dbo.DiagnosticProjects WHERE Id = @id",
            [new { name = "id", type = "uuid", value = ProjectId.ToString() }]);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        AssertSuccessfulProjectResult(response, "sql-server");
        await AssertSchemaInspectionAsync(
            "sql-server",
            readerBuilder.ConnectionString,
            "dbo",
            "DiagnosticProjects",
            "[dbo].[DiagnosticProjects]",
            "IX_DiagnosticProjects_Name_Id");
        await AssertIndexesRejectedForUnselectableObjectAsync(
            "sql-server",
            readerBuilder.ConnectionString,
            "dbo",
            "RestrictedProjects");
        await AssertClassifiedFailureAsync(
            "sql-server",
            readerBuilder.ConnectionString,
            "SELECT TOP (1) MissingColumn FROM dbo.DiagnosticProjects",
            "column-not-found",
            "207");
        await AssertClassifiedFailureAsync(
            "sql-server",
            readerBuilder.ConnectionString,
            "SELECT TOP (1) Id FROM dbo.MissingProjects",
            "object-not-found",
            "208");
        await AssertClassifiedFailureAsync(
            "sql-server",
            readerBuilder.ConnectionString,
            "SELECT TOP (1) Id FROM dbo.RestrictedProjects",
            "permission-denied",
            "229");
        await AssertRowLimitOverflowFailsWithoutReturningPartialDataAsync(
            "sql-server",
            readerBuilder.ConnectionString);
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
                    name varchar(100) NOT NULL,
                    status varchar(32) NOT NULL
                );
                CREATE TABLE restricted_projects (
                    id uuid NOT NULL PRIMARY KEY,
                    secret varchar(100) NOT NULL
                );
                CREATE INDEX ix_diagnostic_projects_name_id
                    ON diagnostic_projects (name ASC, id DESC)
                    INCLUDE (status);
                INSERT INTO diagnostic_projects (id, name, status)
                VALUES ('{ProjectId}', 'Diagnostic project', 'active');
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
            "SELECT id, name FROM diagnostic_projects WHERE id = @id LIMIT 10",
            [new { name = "id", type = "uuid", value = ProjectId.ToString() }]);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        AssertSuccessfulProjectResult(response, "postgresql");
        await AssertSchemaInspectionAsync(
            "postgresql",
            readerBuilder.ConnectionString,
            "public",
            "diagnostic_projects",
            "\"public\".\"diagnostic_projects\"",
            "ix_diagnostic_projects_name_id");
        await AssertIndexesRejectedForUnselectableObjectAsync(
            "postgresql",
            readerBuilder.ConnectionString,
            "public",
            "restricted_projects");
        await AssertClassifiedFailureAsync(
            "postgresql",
            readerBuilder.ConnectionString,
            "SELECT missing_column FROM diagnostic_projects LIMIT 1",
            "column-not-found",
            "42703");
        await AssertClassifiedFailureAsync(
            "postgresql",
            readerBuilder.ConnectionString,
            "SELECT id FROM missing_projects LIMIT 1",
            "object-not-found",
            "42P01");
        await AssertClassifiedFailureAsync(
            "postgresql",
            readerBuilder.ConnectionString,
            "SELECT id FROM restricted_projects LIMIT 1",
            "permission-denied",
            "42501");
        await AssertRowLimitOverflowFailsWithoutReturningPartialDataAsync(
            "postgresql",
            readerBuilder.ConnectionString);
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

    private static async Task AssertSchemaInspectionAsync(
        string provider,
        string connectionString,
        string schemaName,
        string objectName,
        string expectedSqlIdentifier,
        string expectedIndexName)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, connectionString);
        using (var searchInput = DatabaseReadTestWorkspace.SchemaRequest(
                   "search",
                   schemaName,
                   searchTerm: "projects"))
        using (var searchOutput = new MemoryStream())
        {
            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                ["schema", "--profiles", workspace.ProfileCatalogPath],
                searchInput,
                searchOutput);

            exitCode.Should().Be(0);
            using var response = DatabaseReadTestWorkspace.ParseOutput(searchOutput);
            var schema = response.RootElement.GetProperty("schema");
            schema.GetProperty("operation").GetString().Should().Be("search");
            schema.GetProperty("evidenceHash").GetString().Should().HaveLength(64);
            var objects = schema.GetProperty("objects").EnumerateArray().ToArray();
            objects.Should().ContainSingle(item => item.GetProperty("name").GetString() == objectName);
            objects.Should().NotContain(item =>
                item.GetProperty("name").GetString()!.Contains("Restricted", StringComparison.OrdinalIgnoreCase)
                || item.GetProperty("name").GetString()!.Contains("restricted", StringComparison.Ordinal));
        }

        using var describeInput = DatabaseReadTestWorkspace.SchemaRequest(
            "describe",
            schemaName,
            objectName);
        using var describeOutput = new MemoryStream();
        var describeExitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["schema", "--profiles", workspace.ProfileCatalogPath],
            describeInput,
            describeOutput);

        describeExitCode.Should().Be(
            0,
            "schema describe returned {0}",
            System.Text.Encoding.UTF8.GetString(describeOutput.ToArray()));
        using var describeResponse = DatabaseReadTestWorkspace.ParseOutput(describeOutput);
        var describedObject = describeResponse.RootElement
            .GetProperty("schema")
            .GetProperty("object");
        describedObject.GetProperty("sqlIdentifier").GetString().Should().Be(expectedSqlIdentifier);
        describedObject.GetProperty("columns").GetArrayLength().Should().Be(3);
        var describedIndexes = describedObject.GetProperty("indexes").EnumerateArray().ToArray();
        describedIndexes.Should().Contain(index => index.GetProperty("isPrimaryKey").GetBoolean());
        AssertIndexMetadata(describedIndexes, expectedIndexName);

        using var indexesInput = DatabaseReadTestWorkspace.SchemaRequest(
            "indexes",
            schemaName,
            objectName);
        using var indexesOutput = new MemoryStream();
        var indexesExitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["schema", "--profiles", workspace.ProfileCatalogPath],
            indexesInput,
            indexesOutput);

        indexesExitCode.Should().Be(
            0,
            "schema indexes returned {0}",
            System.Text.Encoding.UTF8.GetString(indexesOutput.ToArray()));
        using var indexesResponse = DatabaseReadTestWorkspace.ParseOutput(indexesOutput);
        var schemaIndexes = indexesResponse.RootElement.GetProperty("schema");
        schemaIndexes.GetProperty("operation").GetString().Should().Be("indexes");
        schemaIndexes.GetProperty("evidenceHash").GetString().Should().HaveLength(64);
        var indexSet = schemaIndexes.GetProperty("indexes");
        indexSet.GetProperty("sqlIdentifier").GetString().Should().Be(expectedSqlIdentifier);
        AssertIndexMetadata(
            indexSet.GetProperty("items").EnumerateArray().ToArray(),
            expectedIndexName);
    }

    private static void AssertIndexMetadata(
        IReadOnlyList<JsonElement> indexes,
        string expectedIndexName)
    {
        var index = indexes.Single(item => item.GetProperty("name").GetString() == expectedIndexName);
        index.GetProperty("isPartial").GetBoolean().Should().BeFalse();
        index.GetProperty("columns").EnumerateArray()
            .Select(column => column.GetString()!.ToLowerInvariant())
            .Should().Equal("name", "id");
        index.GetProperty("keyColumns").EnumerateArray()
            .Select(column => column.GetProperty("direction").GetString())
            .Should().Equal("ascending", "descending");
        index.GetProperty("includedColumns").EnumerateArray()
            .Select(column => column.GetString())
            .Should().ContainSingle(column => string.Equals(
                column,
                "Status",
                StringComparison.OrdinalIgnoreCase),
                "the provider response was {0}",
                index.GetRawText());
    }

    private static async Task AssertIndexesRejectedForUnselectableObjectAsync(
        string provider,
        string connectionString,
        string schemaName,
        string objectName)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, connectionString);
        using var input = DatabaseReadTestWorkspace.SchemaRequest(
            "indexes",
            schemaName,
            objectName);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["schema", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(5);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.TryGetProperty("schema", out _).Should().BeFalse();
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("schema-object-not-found");
    }

    private static async Task AssertClassifiedFailureAsync(
        string provider,
        string connectionString,
        string sql,
        string classification,
        string providerCode)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, connectionString);
        using var input = DatabaseReadTestWorkspace.Request(sql);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(5);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        var error = response.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("database-query-failed");
        error.GetProperty("classification").GetString().Should().Be(classification);
        error.GetProperty("provider").GetString().Should().Be(provider);
        error.GetProperty("providerCode").GetString().Should().Be(providerCode);
    }

    private static async Task AssertRowLimitOverflowFailsWithoutReturningPartialDataAsync(
        string provider,
        string connectionString)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, connectionString);
        using var input = DatabaseReadTestWorkspace.Request(
            "SELECT 1 AS value_number UNION ALL SELECT 2 AS value_number",
            maximumRows: 1);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(4);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.RootElement.TryGetProperty("result", out _).Should().BeFalse();
        var error = response.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("result-row-limit-exceeded");
        error.GetProperty("message").GetString().Should().Contain("No partial result was returned");
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
