using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using NewHeap.Platform.Tests.Relational;
using Npgsql;
using System.Text;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// A schema search may omit the schema, the search term or both. Every test gets a database of its
/// own with selectable objects in two schemas and one object the principal cannot select, so the
/// omitted filters prove that visibility and limits stay as they are.
/// </summary>
public sealed class DatabaseReadSchemaSearchTests
{
    private const string Password = "Test-only-searcher-Password-42";

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ProviderSearchAcceptsAnOmittedSchemaAndTerm(RelationalTestProvider provider)
    {
        var database = await PrepareAsync(provider);
        var readProvider = DatabaseReadProviderFactory.Create(database.Kind);
        var limits = new DatabaseReadLimits(10, 10, 2_000, 65_536, 4_096, 8_192);
        var cancellationToken = CancellationToken.None;
        await using var connection = readProvider.CreateConnection(
            database.ReaderConnectionString,
            Guid.NewGuid().ToString("N"),
            limits);
        await connection.OpenAsync(cancellationToken);
        (await readProvider.VerifyReadOnlyPrincipalAsync(connection, limits, cancellationToken))
            .IsReadOnly.Should().BeTrue();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await readProvider.ConfigureReadOnlyTransactionAsync(connection, transaction, limits, cancellationToken);

        async Task<string[]> SearchAsync(string? schemaName, string? searchTerm)
        {
            var result = await readProvider.ReadSchemaAsync(
                connection,
                transaction,
                new ResolvedDatabaseSchemaRequest(DatabaseSchemaOperation.Search, schemaName, null, searchTerm),
                limits,
                cancellationToken);

            result.Truncated.Should().BeFalse();
            return result.Objects!.Select(item => $"{item.Schema}.{item.Name}").ToArray();
        }

        (await SearchAsync(null, "project")).Should().Equal(
            $"{database.DefaultSchema}.search_projects",
            "reporting.search_project_totals");
        (await SearchAsync("reporting", null)).Should().Equal("reporting.search_project_totals");
        (await SearchAsync(null, null)).Should().Equal(
            $"{database.DefaultSchema}.search_projects",
            $"{database.DefaultSchema}.search_tasks",
            "reporting.search_project_totals");
        (await SearchAsync(database.DefaultSchema, "project")).Should().Equal(
            $"{database.DefaultSchema}.search_projects");

        await transaction.RollbackAsync(cancellationToken);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task CliSearchAcceptsAnOmittedSchemaAndTerm(RelationalTestProvider provider)
    {
        var database = await PrepareAsync(provider);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);

        using (var schemaOmitted = await RunJsonAsync(workspace, """{ "operation": "search", "searchTerm": "project" }"""))
        {
            ObjectNames(schemaOmitted).Should().Equal(
                $"{database.DefaultSchema}.search_projects",
                "reporting.search_project_totals");
        }

        using (var termOmitted = await RunJsonAsync(workspace, """{ "operation": "search", "schemaName": "reporting" }"""))
        {
            ObjectNames(termOmitted).Should().Equal("reporting.search_project_totals");
        }

        using (var bothOmitted = await RunJsonAsync(workspace, """{ "operation": "search" }"""))
        {
            ObjectNames(bothOmitted).Should().Equal(
                $"{database.DefaultSchema}.search_projects",
                $"{database.DefaultSchema}.search_tasks",
                "reporting.search_project_totals");
            bothOmitted.RootElement.GetProperty("schema").GetProperty("truncated").GetBoolean().Should().BeFalse();
        }

        using (var explicitSchema = await RunAsync(
                   workspace,
                   DatabaseReadTestWorkspace.SchemaRequest("search", database.DefaultSchema, searchTerm: "project"),
                   "schema"))
        {
            ObjectNames(explicitSchema).Should().Equal($"{database.DefaultSchema}.search_projects");
        }

        using (var bothNullAndLimited = await RunAsync(
                   workspace,
                   DatabaseReadTestWorkspace.SchemaRequest("search", maximumRows: 2),
                   "schema"))
        {
            ObjectNames(bothNullAndLimited).Should().Equal(
                $"{database.DefaultSchema}.search_projects",
                $"{database.DefaultSchema}.search_tasks");
            bothNullAndLimited.RootElement.GetProperty("schema").GetProperty("truncated").GetBoolean().Should().BeTrue();
        }

        using (var directSchemaOmitted = await RunAsync(workspace, new MemoryStream(), "schema", "--search", "project"))
        {
            directSchemaOmitted.RootElement.GetProperty("schema").GetProperty("operation").GetString()
                .Should().Be("search");
            ObjectNames(directSchemaOmitted).Should().Equal(
                $"{database.DefaultSchema}.search_projects",
                "reporting.search_project_totals");
        }
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task SearchAndDescribeAcceptsAnOmittedSchemaAndTerm(RelationalTestProvider provider)
    {
        var database = await PrepareAsync(provider);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        var expectedIdentifier = database.Kind == DatabaseProviderKind.SqlServer
            ? "[dbo].[search_tasks]"
            : "\"public\".\"search_tasks\"";

        using (var schemaOmitted = await RunJsonAsync(
                   workspace,
                   """{ "operation": "search-and-describe", "searchTerm": "task" }"""))
        {
            var schema = schemaOmitted.RootElement.GetProperty("schema");
            schema.GetProperty("operation").GetString().Should().Be("search-and-describe");
            ObjectNames(schemaOmitted).Should().Equal($"{database.DefaultSchema}.search_tasks");
            schema.GetProperty("object").GetProperty("sqlIdentifier").GetString().Should().Be(expectedIdentifier);
            schema.GetProperty("object").GetProperty("columns").GetArrayLength().Should().Be(2);
        }

        using (var bothOmitted = await RunJsonAsync(workspace, """{ "operation": "search-and-describe" }"""))
        {
            var schema = bothOmitted.RootElement.GetProperty("schema");
            schema.GetProperty("operation").GetString().Should().Be("search-and-describe");
            ObjectNames(bothOmitted).Should().HaveCount(3);
            schema.TryGetProperty("object", out _).Should().BeFalse();
        }

        using (var direct = await RunAsync(
                   workspace,
                   new MemoryStream(),
                   "schema",
                   "--search",
                   "task",
                   "--describe-if-single"))
        {
            var schema = direct.RootElement.GetProperty("schema");
            schema.GetProperty("operation").GetString().Should().Be("search-and-describe");
            schema.GetProperty("object").GetProperty("sqlIdentifier").GetString().Should().Be(expectedIdentifier);
        }

        using (var directExplicitSchema = await RunAsync(
                   workspace,
                   new MemoryStream(),
                   "schema",
                   "--search",
                   "project",
                   "--schema-name",
                   "reporting",
                   "--describe-if-single"))
        {
            var schema = directExplicitSchema.RootElement.GetProperty("schema");
            ObjectNames(directExplicitSchema).Should().Equal("reporting.search_project_totals");
            schema.GetProperty("object").GetProperty("name").GetString().Should().Be("search_project_totals");
        }
    }

    private static string[] ObjectNames(JsonDocument response)
    {
        return response.RootElement
            .GetProperty("schema")
            .GetProperty("objects")
            .EnumerateArray()
            .Select(item => $"{item.GetProperty("schema").GetString()}.{item.GetProperty("name").GetString()}")
            .ToArray();
    }

    /// <summary>
    /// Sends a schema request whose omitted properties are absent from the JSON, not <c>null</c>.
    /// </summary>
    private static Task<JsonDocument> RunJsonAsync(DatabaseReadTestWorkspace workspace, string schemaJson)
    {
        var json =
            $$"""
            {
              "schemaVersion": 1,
              "profile": "test",
              "schema": {{schemaJson}},
              "limits": { "maximumRows": 10, "timeoutSeconds": 10 },
              "reason": "Automated schema search test"
            }
            """;

        return RunAsync(workspace, new MemoryStream(Encoding.UTF8.GetBytes(json)), "schema");
    }

    private static async Task<JsonDocument> RunAsync(
        DatabaseReadTestWorkspace workspace,
        MemoryStream input,
        string command,
        params string[] arguments)
    {
        using var output = new MemoryStream();
        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            [command, "--profiles", workspace.ProfileCatalogPath, .. arguments],
            input,
            output,
            CancellationToken.None);
        await input.DisposeAsync();

        exitCode.Should().Be(0, "the tool returned {0}", Encoding.UTF8.GetString(output.ToArray()));
        return DatabaseReadTestWorkspace.ParseOutput(output);
    }

    private static async Task<SearchDatabase> PrepareAsync(RelationalTestProvider provider)
    {
        var adminConnectionString = await RelationalTestServer.For(provider).CreateDatabaseAsync("schemasearch");

        return provider == RelationalTestProvider.SqlServer
            ? await PrepareSqlServerAsync(adminConnectionString)
            : await PreparePostgreSqlAsync(adminConnectionString);
    }

    private static async Task<SearchDatabase> PrepareSqlServerAsync(string adminConnectionString)
    {
        var admin = new SqlConnectionStringBuilder(adminConnectionString);
        var user = admin.InitialCatalog + "_searcher";
        var passwordKeyword = "PASS" + "WORD";
        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync();
        foreach (var statement in new[]
                 {
                     $"CREATE LOGIN [{user}] WITH {passwordKeyword} = '{Password}';",
                     "CREATE SCHEMA reporting;",
                     $"""
                     CREATE USER [{user}] FOR LOGIN [{user}];
                     CREATE TABLE dbo.search_projects (id int NOT NULL PRIMARY KEY, name nvarchar(100) NOT NULL);
                     CREATE TABLE dbo.search_tasks (id int NOT NULL PRIMARY KEY, project_id int NOT NULL);
                     CREATE TABLE reporting.search_project_totals (project_id int NOT NULL, total int NOT NULL);
                     CREATE TABLE dbo.search_restricted_projects (id int NOT NULL);
                     GRANT SELECT ON dbo.search_projects TO [{user}];
                     GRANT SELECT ON dbo.search_tasks TO [{user}];
                     GRANT SELECT ON reporting.search_project_totals TO [{user}];
                     """
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync();
        }

        var reader = new SqlConnectionStringBuilder(adminConnectionString)
        {
            UserID = user,
            Password = Password,
            IntegratedSecurity = false
        };

        return new SearchDatabase(DatabaseProviderKind.SqlServer, "sql-server", "dbo", reader.ConnectionString);
    }

    private static async Task<SearchDatabase> PreparePostgreSqlAsync(string adminConnectionString)
    {
        var admin = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var user = admin.Database + "_searcher";
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            CREATE SCHEMA reporting;
            CREATE TABLE public.search_projects (id int NOT NULL PRIMARY KEY, name varchar(100) NOT NULL);
            CREATE TABLE public.search_tasks (id int NOT NULL PRIMARY KEY, project_id int NOT NULL);
            CREATE TABLE reporting.search_project_totals (project_id int NOT NULL, total int NOT NULL);
            CREATE TABLE public.search_restricted_projects (id int NOT NULL);
            CREATE ROLE {user} LOGIN PASSWORD '{Password}';
            GRANT CONNECT ON DATABASE "{admin.Database}" TO {user};
            GRANT USAGE ON SCHEMA public, reporting TO {user};
            GRANT SELECT ON public.search_projects, public.search_tasks, reporting.search_project_totals TO {user};
            """;
        await command.ExecuteNonQueryAsync();

        var reader = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = user,
            Password = Password
        };

        return new SearchDatabase(DatabaseProviderKind.PostgreSql, "postgresql", "public", reader.ConnectionString);
    }

    private sealed record SearchDatabase(
        DatabaseProviderKind Kind,
        string ProviderName,
        string DefaultSchema,
        string ReaderConnectionString);
}
