using System.Text.RegularExpressions;
using DotNet.Testcontainers.Containers;
using MySqlConnector;
using Testcontainers.MariaDb;
using Testcontainers.MySql;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// The MySQL and MariaDB flavors that the database read tool's provider tests run on. Only this
/// tool supports them, so they have an enum of their own instead of extending
/// <c>RelationalTestProvider</c>, which every relational test of the repository runs over.
/// </summary>
/// <remarks>
/// The flavor is a theory parameter, so its name is part of the display name: the CI job of one
/// flavor selects its cases by it, and no case says <c>SqlServer</c>.
/// </remarks>
public enum DatabaseReadMySqlFlavor
{
    MySql = 0,
    MariaDb = 1
}

/// <summary>
/// One MySQL and one MariaDB server for the whole test process, each started when a test first
/// uses its flavor, as <c>RelationalTestServer</c> does for SQL Server and PostgreSQL. A test gets
/// a database of its own; accounts and roles belong to the server, so their names start with the
/// name of the test's database, and the test drops them when it ends. A MySQL 5.7 server, older
/// than the tool supports, is started the same way for the test that proves it is refused.
/// </summary>
internal sealed partial class DatabaseReadMySqlTestServer
{
    public const string MySqlImage = "mysql:8.4";
    public const string MariaDbImage = "mariadb:11.4";
    public const string UnsupportedMySqlImage = "mysql:5.7";

    private const string RootCredential = "test-root-credential";

    private static readonly DatabaseReadMySqlTestServer MySql = new(DatabaseReadMySqlFlavor.MySql, MySqlImage);
    private static readonly DatabaseReadMySqlTestServer MariaDb = new(DatabaseReadMySqlFlavor.MariaDb, MariaDbImage);

    private readonly string _image;
    private readonly Lazy<Task<string>> _connectionString;

    /// <summary>
    /// The started container, referenced for the life of the process; the resource reaper stops it.
    /// </summary>
    private IContainer? _container;

    private int _databaseCount;

    private DatabaseReadMySqlTestServer(DatabaseReadMySqlFlavor flavor, string image)
    {
        Flavor = flavor;
        _image = image;
        _connectionString = new Lazy<Task<string>>(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// A MySQL 5.7 server, which has no roles and no <c>CURRENT_ROLE()</c>, so the tool must refuse
    /// it before any statement. Only the test that proves this starts it.
    /// </summary>
    public static DatabaseReadMySqlTestServer UnsupportedMySql { get; } =
        new(DatabaseReadMySqlFlavor.MySql, UnsupportedMySqlImage);

    public DatabaseReadMySqlFlavor Flavor { get; }

    /// <summary>
    /// The profile value of the flavor.
    /// </summary>
    public string ProviderName => Flavor == DatabaseReadMySqlFlavor.MySql ? "mysql" : "mariadb";

    public DatabaseProviderKind Kind =>
        Flavor == DatabaseReadMySqlFlavor.MySql ? DatabaseProviderKind.MySql : DatabaseProviderKind.MariaDb;

    public static DatabaseReadMySqlTestServer For(DatabaseReadMySqlFlavor flavor)
    {
        return flavor == DatabaseReadMySqlFlavor.MySql ? MySql : MariaDb;
    }

    /// <summary>
    /// The root connection string without a default database, which starts the server on first use.
    /// </summary>
    public Task<string> GetConnectionStringAsync()
    {
        return _connectionString.Value;
    }

    /// <summary>
    /// Creates an empty database of its own and returns the root connection string of it. Its name
    /// starts with <paramref name="prefix"/> and stays short, because MySQL allows account names of
    /// 32 characters and the test's accounts start with it.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        if (!Name().IsMatch(prefix))
        {
            throw new ArgumentException(
                "The prefix consists of lowercase letters, digits and underscores, starts with a letter and has at most 12 characters.",
                nameof(prefix));
        }

        var serverConnectionString = await GetConnectionStringAsync();
        var database = prefix + "_" + Interlocked.Increment(ref _databaseCount).ToString("D3");
        await using (var connection = new MySqlConnection(serverConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE `{database}`;";
            await create.ExecuteNonQueryAsync();
        }

        return new MySqlConnectionStringBuilder(serverConnectionString) { Database = database }.ConnectionString;
    }

    /// <summary>
    /// Runs statements as root, one after the other, in the given database or without one.
    /// </summary>
    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The connection string of an account on the server, without pooling, so no idle session of it
    /// remains when the test drops it.
    /// </summary>
    public static string AccountConnectionString(string connectionString, string user, string password)
    {
        return new MySqlConnectionStringBuilder(connectionString)
        {
            UserID = user,
            Password = password,
            Pooling = false
        }.ConnectionString;
    }

    /// <summary>
    /// MySQL logs binary by default and then refuses a stored function without
    /// <c>log_bin_trust_function_creators</c>; the tests create functions to prove the routine and
    /// DEFINER view checks.
    /// </summary>
    private async Task<string> StartAsync()
    {
        if (_image == UnsupportedMySqlImage)
        {
            var unsupported = new MySqlBuilder(UnsupportedMySqlImage)
                .WithUsername("root")
                .WithPassword(RootCredential)
                .Build();
            _container = unsupported;
            await unsupported.StartAsync();
            return WithoutDatabase(unsupported.GetConnectionString());
        }

        if (Flavor == DatabaseReadMySqlFlavor.MySql)
        {
            var mySql = new MySqlBuilder(MySqlImage)
                .WithUsername("root")
                .WithPassword(RootCredential)
                .WithCommand("--log-bin-trust-function-creators=1", "--max-connections=500")
                .Build();
            _container = mySql;
            await mySql.StartAsync();

            // The image grants PROXY only to root@localhost, which connects through the server's
            // socket; the tests grant PROXY as the root that connects from the host, root@%.
            var proxy = await mySql.ExecAsync(
            [
                "mysql",
                "--protocol=SOCKET",
                "-uroot",
                $"-p{RootCredential}",
                "-e",
                "GRANT PROXY ON ''@'' TO 'root'@'%' WITH GRANT OPTION;"
            ]);
            if (proxy.ExitCode != 0)
            {
                throw new InvalidOperationException("The MySQL test server could not grant PROXY to root: " + proxy.Stderr);
            }

            return WithoutDatabase(mySql.GetConnectionString());
        }

        var mariaDb = new MariaDbBuilder(MariaDbImage)
            .WithUsername("root")
            .WithPassword(RootCredential)
            .WithCommand("--max-connections=500")
            .Build();
        _container = mariaDb;
        await mariaDb.StartAsync();
        return WithoutDatabase(mariaDb.GetConnectionString());
    }

    private static string WithoutDatabase(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        builder.Remove("Database");

        return builder.ConnectionString;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();
}
