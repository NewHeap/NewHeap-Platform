using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite;

/// <summary>Owns versioned SQLite redirect snapshots. Writes persist only; activation is a separate operation.</summary>
public sealed class NhProxySqliteConfigurationStore : INhProxyConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _databasePath;
    private readonly string _connectionString;
    internal string ConnectionString => _connectionString;
    private readonly INhProxyConfigurationValidator _validator;
    // ponytail: configuration operations share one connection and gate; use per-operation connections if management throughput requires it.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private FileStream? _ownership;
    private bool _disposed;

    public NhProxySqliteConfigurationStore(
        IOptions<NhProxySqliteOptions> options,
        IWebHostEnvironment? environment = null,
        INhProxyConfigurationValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.DatabasePath) || settings.DatabasePath == ":memory:")
        {
            throw new ArgumentException("NewHeap Proxy requires a persistent SQLite file path.", nameof(options));
        }

        if (settings.BusyTimeout <= TimeSpan.Zero || settings.BusyTimeout.TotalSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SQLite BusyTimeout must be positive and fit in whole seconds.");
        }

        _databasePath = Path.GetFullPath(settings.DatabasePath, environment?.ContentRootPath ?? Environment.CurrentDirectory);
        if (!string.IsNullOrEmpty(environment?.WebRootPath))
        {
            var webRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.WebRootPath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (_databasePath.Equals(webRoot, comparison) || _databasePath.StartsWith(webRoot + Path.DirectorySeparatorChar, comparison))
            {
                throw new ArgumentException("The proxy database must be outside the webroot.", nameof(options));
            }
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = (int)Math.Ceiling(settings.BusyTimeout.TotalSeconds)
        }.ToString();
        _validator = validator ?? new NhProxyConfigurationValidator(Options.Create(new NhProxyOptions()));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is not null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            try
            {
                _ownership = new FileStream(_databasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("Cannot acquire exclusive ownership of the proxy database.", exception);
            }

            _connection = new SqliteConnection(_connectionString);
            try
            {
                await _connection.OpenAsync(cancellationToken);
                using var command = _connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                var version = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
                if (version is < 0 or > 2)
                {
                    throw new InvalidDataException($"Unsupported NewHeap Proxy SQLite schema version {version}.");
                }

                if (version == 0)
                {
                    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';";
                    if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 0)
                    {
                        throw new InvalidDataException("The database contains an unrecognized schema; it will not be overwritten.");
                    }
                }

                command.CommandText = "PRAGMA journal_mode=WAL;";
                await command.ExecuteScalarAsync(cancellationToken);
                if (version == 0)
                {
                    using var transaction = _connection.BeginTransaction();
                    command.Transaction = transaction;
                    command.CommandText = """
                        CREATE TABLE NhProxyConfiguration (
                            Engine TEXT PRIMARY KEY NOT NULL CHECK (Engine IN ('Redirect', 'Rewrite')),
                            Revision INTEGER NOT NULL CHECK (Revision >= 0),
                            FormatVersion INTEGER NOT NULL,
                            Document TEXT NOT NULL
                        );
                        INSERT INTO NhProxyConfiguration (Engine, Revision, FormatVersion, Document)
                        VALUES ('Redirect', 0, 1, $document);
                        PRAGMA user_version=1;
                        """;
                    command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(new NhProxyRedirectConfiguration(), JsonOptions));
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }

                if (version < 2)
                {
                    using var transaction = _connection.BeginTransaction();
                    command.Transaction = transaction;
                    command.Parameters.Clear();
                    command.CommandText = """
                        CREATE TABLE NhProxyLoginAudit (
                            Id TEXT PRIMARY KEY NOT NULL,
                            OccurredAtUtc INTEGER NOT NULL,
                            ClientIp TEXT,
                            ConnectionPeerIp TEXT,
                            Outcome INTEGER NOT NULL,
                            CorrelationId TEXT NOT NULL,
                            AuthenticatedUserName TEXT
                        );
                        CREATE INDEX IX_NhProxyLoginAudit_Time ON NhProxyLoginAudit (OccurredAtUtc DESC, Id);
                        PRAGMA user_version=2;
                        """;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch
            {
                await _connection.DisposeAsync();
                _connection = null;
                await _ownership.DisposeAsync();
                _ownership = null;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<NhProxyRewriteConfiguration> LoadRewritesAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<NhProxyRedirectConfiguration> LoadRedirectsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var command = GetConnection().CreateCommand();
            command.CommandText = "SELECT Revision, FormatVersion, Document FROM NhProxyConfiguration WHERE Engine='Redirect';";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException("The persisted redirect snapshot is missing.");
            }

            var json = reader.GetString(2);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("revision", out _)
                || !document.RootElement.TryGetProperty("formatVersion", out _)
                || !document.RootElement.TryGetProperty("rules", out _))
            {
                throw new InvalidDataException("The persisted redirect document is incomplete.");
            }

            var snapshot = JsonSerializer.Deserialize<NhProxyRedirectConfiguration>(json, JsonOptions)
                ?? throw new InvalidDataException("The persisted redirect document is null.");
            if (snapshot.Revision != reader.GetInt64(0) || snapshot.FormatVersion != reader.GetInt32(1) || snapshot.FormatVersion != 1)
            {
                throw new InvalidDataException("The persisted redirect revision or format is inconsistent or unsupported.");
            }

            var validation = await _validator.ValidateRedirectsAsync(new NhProxyRedirectSaveRequest(snapshot.Revision, snapshot.Rules), cancellationToken);
            if (!validation.Success)
            {
                throw new InvalidDataException("The persisted redirect snapshot contains invalid or unsupported rules.");
            }

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<TaskResult<NhProxyRewriteConfiguration>> SaveRewritesAsync(NhProxyRewriteSaveRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<TaskResult<NhProxyRedirectConfiguration>> SaveRedirectsAsync(NhProxyRedirectSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedRevision == long.MaxValue)
        {
            return TaskResult<NhProxyRedirectConfiguration>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.revision-exhausted");
        }

        var validation = await _validator.ValidateRedirectsAsync(request, cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<NhProxyRedirectConfiguration>.Failed(validation);
        }

        var snapshot = new NhProxyRedirectConfiguration { Revision = checked(request.ExpectedRevision + 1), Rules = request.Rules };
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var command = GetConnection().CreateCommand();
            command.CommandText = """
                UPDATE NhProxyConfiguration SET Revision=$revision, FormatVersion=1, Document=$document
                WHERE Engine='Redirect' AND Revision=$expected AND FormatVersion=1;
                """;
            command.Parameters.AddWithValue("$revision", snapshot.Revision);
            command.Parameters.AddWithValue("$expected", request.ExpectedRevision);
            command.Parameters.AddWithValue("$document", json);
            // One conditional UPDATE is an atomic SQLite transaction; no read-then-write race.
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return TaskResult<NhProxyRedirectConfiguration>.Failed(NhProxyErrorCodes.RevisionConflict, NhProxyErrorCodes.RevisionConflict);
            }

            return TaskResult<NhProxyRedirectConfiguration>.Succeeded(snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            if (_ownership is not null)
            {
                await _ownership.DisposeAsync();
                _ownership = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection GetConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection ?? throw new InvalidOperationException("Initialize the proxy store before reading or writing configuration.");
    }
}
