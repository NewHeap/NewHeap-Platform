using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// The SQL Server profile opt-in <c>sqlServerFunctionPolicy: inline-table-valued-select</c> on a
/// real server: SELECT on an inline table-valued function passes only when the function is
/// schema-bound and depends only on tables and views of the same database that the principal may
/// select directly, and the principal can see the function's definition to prove that. Every other
/// function permission stays rejected, each with its own verification check, and the strict policy
/// is unchanged.
/// </summary>
/// <remarks>
/// The login belongs to the server that the test process shares, so its name starts with the name
/// of the test's own database, and the test drops it when it ends.
/// </remarks>
public sealed class DatabaseReadSqlServerFunctionPolicyTests
{
    private const string ReaderPassword = "Test-only-reader-Password-42";

    private static readonly DatabaseReadLimits Limits = new(10, 10, 2_000, 65_536, 4_096, 8_192);

    [Fact]
    public async Task SqlServerInlineTableValuedSelectAcceptsOnlyVerifiedFunctions()
    {
        var admin = new SqlConnectionStringBuilder(
            await RelationalTestServer.For(RelationalTestProvider.SqlServer).CreateDatabaseAsync("functions"));
        var master = new SqlConnectionStringBuilder(admin.ConnectionString) { InitialCatalog = "master" };
        var reader = admin.InitialCatalog + "_reader";
        var passwordKeyword = "PASS" + "WORD";
        await ExecuteAsync(master.ConnectionString, $"CREATE LOGIN [{reader}] WITH {passwordKeyword} = '{ReaderPassword}';");
        await ExecuteAsync(
            admin.ConnectionString,
            $"""
            CREATE TABLE dbo.Items (Id int NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL, Secret nvarchar(100) NULL);
            CREATE TABLE dbo.Restricted (Id int NOT NULL PRIMARY KEY);
            INSERT INTO dbo.Items (Id, Name, Secret) VALUES (1, N'First item', N'Hidden');
            CREATE USER [{reader}] FOR LOGIN [{reader}];
            GRANT SELECT (Id, Name) ON dbo.Items TO [{reader}];
            """);
        foreach (var definition in new[]
                 {
                     "CREATE VIEW dbo.ItemNames WITH SCHEMABINDING AS SELECT Id, Name FROM dbo.Items;",
                     "CREATE FUNCTION dbo.ReadItem(@id int) RETURNS TABLE WITH SCHEMABINDING AS " +
                     "RETURN (SELECT Id, Name FROM dbo.Items WHERE Id = @id);",
                     "CREATE FUNCTION dbo.ReadItemNames() RETURNS TABLE WITH SCHEMABINDING AS " +
                     "RETURN (SELECT Name FROM dbo.ItemNames);",
                     "CREATE FUNCTION dbo.ReadJsonKeys(@json nvarchar(max)) RETURNS TABLE WITH SCHEMABINDING AS " +
                     "RETURN (SELECT [key] AS JsonKey FROM OPENJSON(@json));",
                     "CREATE FUNCTION dbo.ReadItemsLoosely() RETURNS TABLE AS RETURN (SELECT Id FROM dbo.Items);",
                     "CREATE FUNCTION dbo.ReadItemsInSteps() RETURNS @result TABLE (Id int NOT NULL) WITH SCHEMABINDING AS " +
                     "BEGIN INSERT INTO @result (Id) SELECT Id FROM dbo.Items; RETURN; END;",
                     "CREATE FUNCTION dbo.Doubled(@value int) RETURNS int WITH SCHEMABINDING AS BEGIN RETURN @value * 2; END;",
                     "CREATE FUNCTION dbo.ReadDoubled() RETURNS TABLE WITH SCHEMABINDING AS " +
                     "RETURN (SELECT dbo.Doubled(Id) AS Doubled FROM dbo.Items);",
                     "CREATE FUNCTION dbo.ReadNested() RETURNS TABLE WITH SCHEMABINDING AS " +
                     "RETURN (SELECT Id FROM dbo.ReadItem(1));",
                     "CREATE FUNCTION dbo.ReadSecrets() RETURNS TABLE WITH SCHEMABINDING AS " +
                     "RETURN (SELECT Id, Secret FROM dbo.Items);",
                     "CREATE FUNCTION dbo.CountItems() RETURNS TABLE WITH SCHEMABINDING AS " +
                     "RETURN (SELECT COUNT_BIG(*) AS Total FROM dbo.Items);",
                     "CREATE FUNCTION dbo.ReadRestricted() RETURNS TABLE WITH SCHEMABINDING AS " +
                     "RETURN (SELECT Id FROM dbo.Restricted);"
                 })
        {
            await ExecuteAsync(admin.ConnectionString, definition);
        }

        await ExecuteAsync(admin.ConnectionString, $"GRANT SELECT ON dbo.ItemNames TO [{reader}];");
        var readerConnectionString = new SqlConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = reader,
            Password = ReaderPassword,
            IntegratedSecurity = false,
            Pooling = false
        }.ConnectionString;
        var scenarios = new FunctionScenario[]
        {
            new("a verified function", "dbo.ReadItem", null),
            new("a verified function over a view", "dbo.ReadItemNames", null),
            new("a verified function without dependencies", "dbo.ReadJsonKeys", null),
            new("SELECT without VIEW DEFINITION", "dbo.ReadItem", "inline-function-definition-not-visible",
                ViewDefinition: false),
            new("a function that is not schema-bound", "dbo.ReadItemsLoosely", "inline-function-not-schema-bound"),
            new("a multi-statement function", "dbo.ReadItemsInSteps", "function-select"),
            new("a function that calls a scalar function", "dbo.ReadDoubled", "inline-function-dependency-not-allowed",
                Visible: "dbo.Doubled"),
            new("a function that reads another function", "dbo.ReadNested", "inline-function-dependency-not-allowed",
                Visible: "dbo.ReadItem"),
            new("a function that calls a function the principal cannot see", "dbo.ReadDoubled",
                "inline-function-dependency-not-selectable"),
            new("a function that reads a column the principal may not select", "dbo.ReadSecrets",
                "inline-function-dependency-not-selectable"),
            new("a function that counts a table the principal may select only by column", "dbo.CountItems",
                "inline-function-dependency-not-selectable"),
            new("a function that reads a table the principal may not select", "dbo.ReadRestricted",
                "inline-function-dependency-not-selectable")
        };
        var inlinePolicy = new SqlServerDatabaseReadProvider(SqlServerFunctionPolicy.InlineTableValuedSelect);
        var strictPolicy = new SqlServerDatabaseReadProvider();

        try
        {
            (await VerifyAsync(inlinePolicy, readerConnectionString))
                .Should().Be(new DatabaseReadPrincipalVerification(true));

            var mismatches = new List<string>();
            foreach (var scenario in scenarios)
            {
                var permissions = scenario.ViewDefinition ? "SELECT, VIEW DEFINITION" : "SELECT";
                var visible = scenario.Visible is null
                    ? string.Empty
                    : $" GRANT VIEW DEFINITION ON {scenario.Visible} TO [{reader}];";
                var invisible = scenario.Visible is null
                    ? string.Empty
                    : $" REVOKE VIEW DEFINITION ON {scenario.Visible} FROM [{reader}];";
                await ExecuteAsync(
                    admin.ConnectionString,
                    $"GRANT {permissions} ON {scenario.Function} TO [{reader}];{visible}");
                var inline = await VerifyAsync(inlinePolicy, readerConnectionString);
                var strict = await VerifyAsync(strictPolicy, readerConnectionString);
                await ExecuteAsync(
                    admin.ConnectionString,
                    $"REVOKE {permissions} ON {scenario.Function} FROM [{reader}];{invisible}");

                var expected = scenario.ExpectedCheck is null
                    ? new DatabaseReadPrincipalVerification(true)
                    : new DatabaseReadPrincipalVerification(false, scenario.ExpectedCheck);
                if (inline != expected)
                {
                    mismatches.Add($"{scenario.Name}: expected {Describe(expected)}, verified {Describe(inline)}");
                }

                if (strict != new DatabaseReadPrincipalVerification(false, "function-select"))
                {
                    mismatches.Add($"{scenario.Name}: the strict policy verified {Describe(strict)}");
                }
            }

            string.Join(Environment.NewLine, mismatches).Should().BeEmpty();

            await ExecuteAsync(admin.ConnectionString, $"ALTER ROLE db_datareader ADD MEMBER [{reader}];");
            (await VerifyAsync(inlinePolicy, readerConnectionString))
                .Should().Be(
                    new DatabaseReadPrincipalVerification(false, "function-select"),
                    "a database-wide read grant includes the multi-statement function");
            await ExecuteAsync(admin.ConnectionString, $"ALTER ROLE db_datareader DROP MEMBER [{reader}];");

            var executeAs = () => ExecuteAsync(
                admin.ConnectionString,
                "CREATE FUNCTION dbo.ReadItemsAsOwner() RETURNS TABLE WITH SCHEMABINDING, EXECUTE AS OWNER AS " +
                "RETURN (SELECT Id FROM dbo.Items);");
            (await executeAs.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(
                487,
                "SQL Server refuses EXECUTE AS on an inline function, which the inline-function-execute-as check guards");

            await AssertThroughTheToolAsync(admin.ConnectionString, readerConnectionString, reader);
        }
        finally
        {
            await ExecuteAsync(admin.ConnectionString, $"DROP USER [{reader}];");
            await ExecuteAsync(master.ConnectionString, $"DROP LOGIN [{reader}];");
        }
    }

    private static async Task AssertThroughTheToolAsync(
        string adminConnectionString,
        string readerConnectionString,
        string reader)
    {
        await ExecuteAsync(adminConnectionString, $"GRANT SELECT, VIEW DEFINITION ON dbo.ReadItem TO [{reader}];");
        using (var inline = new DatabaseReadTestWorkspace(
                   "sql-server",
                   readerConnectionString,
                   sqlServerFunctionPolicy: "inline-table-valued-select"))
        {
            using var input = DatabaseReadTestWorkspace.Request(
                "SELECT TOP (1) Id, Name FROM dbo.ReadItem(@id)",
                [new { name = "id", type = "int32", value = 1 }]);
            using var output = new MemoryStream();

            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                ["query", "--profiles", inline.ProfileCatalogPath],
                input,
                output);

            exitCode.Should().Be(0, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));
            using var response = DatabaseReadTestWorkspace.ParseOutput(output);
            var row = response.RootElement.GetProperty("result").GetProperty("rows")[0];
            row[0].GetInt32().Should().Be(1);
            row[1].GetString().Should().Be("First item");
        }

        using (var strict = new DatabaseReadTestWorkspace("sql-server", readerConnectionString))
        {
            await AssertRejectedAsync(strict, "function-select", reader);
        }

        await ExecuteAsync(adminConnectionString, $"REVOKE SELECT, VIEW DEFINITION ON dbo.ReadItem FROM [{reader}];");
        await ExecuteAsync(adminConnectionString, $"GRANT SELECT, VIEW DEFINITION ON dbo.ReadSecrets TO [{reader}];");
        using (var inline = new DatabaseReadTestWorkspace(
                   "sql-server",
                   readerConnectionString,
                   sqlServerFunctionPolicy: "inline-table-valued-select"))
        {
            await AssertRejectedAsync(inline, "inline-function-dependency-not-selectable", reader);
        }

        await ExecuteAsync(adminConnectionString, $"REVOKE SELECT, VIEW DEFINITION ON dbo.ReadSecrets FROM [{reader}];");
    }

    private static async Task AssertRejectedAsync(DatabaseReadTestWorkspace workspace, string expectedCheck, string reader)
    {
        using var input = DatabaseReadTestWorkspace.Request("SELECT TOP (1) Id FROM dbo.Items");
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(4, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        var error = response.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("read-only-principal-not-verified");
        error.GetProperty("verificationCheck").GetString().Should().Be(expectedCheck);
        error.GetProperty("stage").GetString().Should().Be("readonly-verification");
        response.RootElement.GetRawText().Should()
            .NotContain(reader).And.NotContain("ReadSecrets").And.NotContain(ReaderPassword);
    }

    private static async Task<DatabaseReadPrincipalVerification> VerifyAsync(
        IDatabaseReadProvider provider,
        string connectionString)
    {
        await using var connection = provider.CreateConnection(connectionString, Guid.NewGuid().ToString("N"), Limits);
        await connection.OpenAsync();

        return await provider.VerifyReadOnlyPrincipalAsync(connection, Limits, CancellationToken.None);
    }

    private static string Describe(DatabaseReadPrincipalVerification verification)
    {
        return verification.IsReadOnly ? "read-only" : $"rejected by {verification.FailedCheck ?? "(no check)"}";
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        const int MaxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (SqlException exception) when (exception.Number == 1205 && attempt < MaxAttempts)
            {
                // Logins are created and dropped in master, where other test processes create and drop
                // their databases at the same time; the server then picks one batch as deadlock victim.
            }
        }
    }

    /// <param name="Visible">An object the principal also gets <c>VIEW DEFINITION</c> on, so the
    /// verification can see what kind of object the function depends on.</param>
    private sealed record FunctionScenario(
        string Name,
        string Function,
        string? ExpectedCheck,
        bool ViewDefinition = true,
        string? Visible = null);
}
