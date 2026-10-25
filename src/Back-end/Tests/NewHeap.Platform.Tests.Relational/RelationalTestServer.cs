using System.Text.RegularExpressions;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace NewHeap.Platform.Tests.Relational;

/// <summary>
/// One real database server per provider for the whole test process, started when a test first
/// uses the provider. Tests get an empty database of their own on it, or a prepared slot from a
/// <see cref="RelationalTestPool"/>, instead of a container of their own.
/// </summary>
/// <remarks>
/// The servers are never stopped explicitly: the Testcontainers resource reaper removes them, with
/// every database on them, when the test process exits. A test process that only runs PostgreSQL
/// tests therefore never starts SQL Server, and the other way around.
/// </remarks>
public sealed partial class RelationalTestServer
{
    public const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    public const string PostgreSqlImage = "postgres:16-alpine";

    private static readonly RelationalTestServer SqlServer = new(RelationalTestProvider.SqlServer);
    private static readonly RelationalTestServer PostgreSql = new(RelationalTestProvider.PostgreSql);

    /// <summary>
    /// How long a command that prepares test databases may run: creating a database, emptying a
    /// pooled slot or migrating one. Under the parallel load of the full test matrix the server's
    /// disk can stall, and <c>CREATE DATABASE</c>, which writes the files of the new database, then
    /// ran into the default command timeout of 30 seconds and failed whichever test created a
    /// database at that moment. The bound only matters when the server hangs.
    /// </summary>
    public static readonly TimeSpan PreparationCommandTimeout = TimeSpan.FromMinutes(5);

    private readonly Lazy<Task<string>> _connectionString;

    /// <summary>
    /// <c>CREATE DATABASE</c> copies the <c>model</c> database under an exclusive lock, so SQL Server
    /// creates one database at a time; a second one at the same moment can fail on that lock.
    /// </summary>
    private readonly SemaphoreSlim _sqlServerDatabaseCreation = new(1, 1);

    /// <summary>
    /// The started container, referenced for the life of the process; the resource reaper stops it.
    /// </summary>
    private IContainer? _container;

    private int _databaseCount;

    private RelationalTestServer(RelationalTestProvider provider)
    {
        Provider = provider;
        _connectionString = new Lazy<Task<string>>(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RelationalTestProvider Provider { get; }

    public static RelationalTestServer For(RelationalTestProvider provider)
    {
        return provider == RelationalTestProvider.SqlServer ? SqlServer : PostgreSql;
    }

    /// <summary>
    /// The administrator's connection string of the server's default database, which starts the
    /// server on first use. Tests that change the server itself, such as logins or roles, give the
    /// objects they create names of their own: every test of the process shares the server.
    /// </summary>
    public Task<string> GetConnectionStringAsync()
    {
        return _connectionString.Value;
    }

    /// <summary>
    /// Creates an empty database of its own and returns the administrator's connection string of it.
    /// Its name starts with <paramref name="prefix"/>, which helps to recognize it when a test fails.
    /// </summary>
    /// <remarks>
    /// SQL Server databases use delayed durability, as the PostgreSQL container runs without
    /// <c>fsync</c> and synchronous commit: their commits do not wait for the log flush, which only
    /// matters when the server crashes and ends the test run anyway. Transactions, locks and
    /// isolation behave as without it.
    /// </remarks>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        ValidateName(prefix, nameof(prefix));
        var serverConnectionString = await GetConnectionStringAsync();
        var database = prefix + "_" + Interlocked.Increment(ref _databaseCount).ToString("D3");
        if (Provider == RelationalTestProvider.SqlServer)
        {
            await CreateSqlServerDatabaseAsync(serverConnectionString, database);
            return new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = database }.ConnectionString;
        }

        await CreatePostgreSqlDatabaseAsync(serverConnectionString, database);
        return new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = database }.ConnectionString;
    }

    private async Task CreateSqlServerDatabaseAsync(string serverConnectionString, string database)
    {
        await _sqlServerDatabaseCreation.WaitAsync();
        try
        {
            await using var connection = new SqlConnection(serverConnectionString);
            await connection.OpenAsync();
            await using var create = new SqlCommand(
                """
                DECLARE @statements nvarchar(max) =
                    N'CREATE DATABASE ' + QUOTENAME(@database) + N'; '
                    + N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET DELAYED_DURABILITY = FORCED;';
                EXEC sys.sp_executesql @statements;
                """,
                connection);
            create.CommandTimeout = PreparationCommandTimeoutSeconds;
            create.Parameters.AddWithValue("@database", database);
            await create.ExecuteNonQueryAsync();
        }
        finally
        {
            _sqlServerDatabaseCreation.Release();
        }
    }

    /// <summary>
    /// PostgreSQL cannot take a database name as a parameter; the name consists of the checked
    /// prefix and a number only.
    /// </summary>
    private static async Task CreatePostgreSqlDatabaseAsync(string serverConnectionString, string database)
    {
        await using var connection = new NpgsqlConnection(serverConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        create.CommandTimeout = PreparationCommandTimeoutSeconds;
        await create.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// <see cref="PreparationCommandTimeout"/> in seconds, as ADO.NET commands take it.
    /// </summary>
    internal static int PreparationCommandTimeoutSeconds => (int)PreparationCommandTimeout.TotalSeconds;

    private async Task<string> StartAsync()
    {
        if (Provider == RelationalTestProvider.SqlServer)
        {
            var sqlServer = new MsSqlBuilder(SqlServerImage).Build();
            _container = sqlServer;
            await sqlServer.StartAsync();
            return sqlServer.GetConnectionString();
        }

        var postgreSql = new PostgreSqlBuilder(PostgreSqlImage)
            // Test classes run in parallel and every database has its own connection pool; the
            // default of 100 connections is too tight for that.
            .WithCommand("-c", "max_connections=300")
            .Build();
        _container = postgreSql;
        await postgreSql.StartAsync();
        return postgreSql.GetConnectionString();
    }

    /// <summary>
    /// Checks a name that becomes part of a database or schema name: both providers accept it
    /// without quoting, and it stays well below their identifier limits.
    /// </summary>
    internal static void ValidateName(string name, string parameterName)
    {
        if (!Name().IsMatch(name))
        {
            throw new ArgumentException(
                "The name consists of lowercase letters, digits and underscores, starts with a letter and has at most 40 characters.",
                parameterName);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();
}
