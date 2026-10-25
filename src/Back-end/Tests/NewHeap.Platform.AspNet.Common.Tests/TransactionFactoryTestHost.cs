using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using DotNetCore.CAP.Internal;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Persistence;
using DotNetCore.CAP.SqlServer.Diagnostics;
using DotNetCore.CAP.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Events.Cap;
using NewHeap.Platform.Tests.Relational;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The services of an application that opens repository transactions through
/// <see cref="NhTransactionFactory"/> and <see cref="NhTransactionFactoryAsync"/> delegates, with or
/// without CAP, on an empty database of its own. CAP keeps its outbox in that database; the host is
/// not started, so a recording dispatcher stands in for the broker and receives what CAP hands over
/// after a commit.
/// </summary>
internal sealed class TransactionFactoryTestHost : IAsyncDisposable
{
    private readonly IDisposable? _commitDiagnostics;

    private TransactionFactoryTestHost(
        ProviderTestDatabase database,
        ServiceProvider services,
        TransactionStartRecorder starts,
        RecordingCapDispatcher dispatcher,
        bool withCap,
        IDisposable? commitDiagnostics)
    {
        Database = database;
        Services = services;
        Starts = starts;
        Dispatcher = dispatcher;
        WithCap = withCap;
        _commitDiagnostics = commitDiagnostics;
    }

    public ProviderTestDatabase Database { get; }

    public ServiceProvider Services { get; }

    /// <summary>
    /// How the provider opened each transaction: synchronously or asynchronously.
    /// </summary>
    public TransactionStartRecorder Starts { get; }

    /// <summary>
    /// The events CAP handed over for sending, which it does only after a commit.
    /// </summary>
    public RecordingCapDispatcher Dispatcher { get; }

    public bool WithCap { get; }

    public static async Task<TransactionFactoryTestHost> CreateAsync(
        RelationalTestProvider provider,
        bool withCap,
        Action<IServiceCollection>? configure = null)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var starts = new TransactionStartRecorder();
        var dispatcher = new RecordingCapDispatcher();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TransactionNoteContext>(options =>
        {
            database.UseNewHeapProvider(options);
            options.AddInterceptors(starts);
        });

        if (withCap)
        {
            // CAP registers its dispatcher only when none is registered yet.
            services.AddSingleton<IDispatcher>(dispatcher);
            new NhEventConfigurationBuilder(services).AddCap(cap => cap
                .WithOptions(options => options.UseConfiguredEntityFramework<TransactionNoteContext>(
                    provider == RelationalTestProvider.SqlServer ? DatabaseProvider.SqlServer : DatabaseProvider.PostgreSql))
                .WithPublishing());
        }

        configure?.Invoke(services);
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TransactionNoteContext>();
            await context.Database.EnsureCreatedAsync();
        }

        IDisposable? commitDiagnostics = null;
        if (withCap)
        {
            await serviceProvider.GetRequiredService<IStorageInitializer>().InitializeAsync(CancellationToken.None);
            if (provider == RelationalTestProvider.SqlServer)
            {
                // CAP learns of a SQL Server commit through SqlClient diagnostics, which a started CAP
                // host subscribes to; subscribe as it does.
                commitDiagnostics = DiagnosticListener.AllListeners.Subscribe(
                    serviceProvider.GetRequiredService<DiagnosticProcessorObserver>());
            }
        }

        starts.Reset();
        return new TransactionFactoryTestHost(database, serviceProvider, starts, dispatcher, withCap, commitDiagnostics);
    }

    public static Repository<TransactionNote> RepositoryOf(IServiceProvider scopedServices)
    {
        return new Repository<TransactionNote>(scopedServices.GetRequiredService<TransactionNoteContext>(), scopedServices);
    }

    /// <summary>
    /// The notes as a new context reads them after the test's unit of work ended.
    /// </summary>
    public async Task<string[]> ReadNotesAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TransactionNoteContext>();
        return await context.Notes
            .OrderBy(note => note.Text)
            .Select(note => note.Text)
            .ToArrayAsync();
    }

    /// <summary>
    /// The names of the events in CAP's outbox table, as a new connection reads them after the test's
    /// unit of work ended.
    /// </summary>
    public async Task<string[]> ReadOutboxAsync()
    {
        var table = Services.GetRequiredService<IStorageInitializer>().GetPublishedTableName();
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TransactionNoteContext>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"Name\" FROM {table} ORDER BY \"Name\"";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _commitDiagnostics?.Dispose();
        await Services.DisposeAsync();
    }
}

public sealed class TransactionNote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = string.Empty;
}

public sealed class TransactionNoteContext(DbContextOptions<TransactionNoteContext> options) : DbContext(options)
{
    public DbSet<TransactionNote> Notes => Set<TransactionNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransactionNote>(note =>
        {
            note.ToTable("TransactionNotes");
            note.HasKey(entity => entity.Id);
            note.Property(entity => entity.Text).HasMaxLength(100).IsRequired();
            note.HasIndex(entity => entity.Text).IsUnique();
        });
    }
}

public sealed class TransactionNoteCreated : INhEvent
{
    public static string Topic => "nh.transaction-note.created";

    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Records whether EF opened a transaction synchronously or asynchronously, and can cancel the
/// caller's token while the next transaction opens.
/// </summary>
internal sealed class TransactionStartRecorder : DbTransactionInterceptor
{
    private int _synchronousStarts;
    private int _asynchronousStarts;
    private CancellationTokenSource? _cancelDuringNextStart;

    public int SynchronousStarts => Volatile.Read(ref _synchronousStarts);

    public int AsynchronousStarts => Volatile.Read(ref _asynchronousStarts);

    public void Reset()
    {
        Interlocked.Exchange(ref _synchronousStarts, 0);
        Interlocked.Exchange(ref _asynchronousStarts, 0);
    }

    /// <summary>
    /// Cancels <paramref name="source"/> once the next asynchronous transaction start is under way:
    /// the connection is open and the provider is about to begin the transaction.
    /// </summary>
    public void CancelDuringNextStart(CancellationTokenSource source)
    {
        _cancelDuringNextStart = source;
    }

    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result)
    {
        Interlocked.Increment(ref _synchronousStarts);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _asynchronousStarts);

        var source = Interlocked.Exchange(ref _cancelDuringNextStart, null);
        if (source != null)
        {
            await source.CancelAsync();

            // The token EF passes on is the caller's: it is cancelled only when it reached EF.
            cancellationToken.ThrowIfCancellationRequested();
        }

        return result;
    }
}

/// <summary>
/// Stands in for CAP's dispatcher: CAP hands it a stored event after the transaction committed.
/// </summary>
internal sealed class RecordingCapDispatcher : IDispatcher
{
    private readonly ConcurrentQueue<string> _published = new();

    public string[] Published => _published.ToArray();

    public Task Start(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }

    public ValueTask EnqueueToPublish(MediumMessage message)
    {
        _published.Enqueue(message.Origin.Headers[Headers.MessageName]!);
        return ValueTask.CompletedTask;
    }

    public ValueTask EnqueueToExecute(MediumMessage message, ConsumerExecutorDescriptor? descriptor = null)
    {
        return ValueTask.CompletedTask;
    }

    public Task EnqueueToScheduler(MediumMessage message, DateTime publishTime, object? transaction = null)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
