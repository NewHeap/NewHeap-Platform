extern alias CapPostgreSql;
extern alias CapSqlServer;

using DotNetCore.CAP;
using DotNetCore.CAP.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Events;
using System.Data.Common;

namespace NewHeap.Platform.Events.Cap;

public static class NhEventConfigurationBuilderExtensions
{
    /// <summary>
    /// Registers CAP event publishing and an asynchronous transaction factory
    /// (<see cref="NhTransactionFactoryAsync"/>) that joins every repository transaction to the CAP
    /// outbox: the factory opens the transaction with <c>BeginTransactionAsync</c> and the caller's
    /// cancellation token, or wraps the transaction a factory before it returned.
    /// </summary>
    public static NhEventConfigurationBuilder AddCap(this NhEventConfigurationBuilder builder,
        Action<NhCapEventBuilder> configure)
    {
        var capBuilder = new NhCapEventBuilder(builder.ServiceCollection);
        configure(capBuilder);
        builder.ServiceCollection.AddScoped<CapTransactionScope>();
        builder.ServiceCollection.AddSingleton<NhTransactionFactoryAsync>(OpenCapTransactionAsync);
        return builder;
    }

    private static async Task<IDbContextTransaction> OpenCapTransactionAsync(
        IDbContextTransaction? previous,
        DbContext dbContext,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var publisher = serviceProvider.GetRequiredService<ICapPublisher>();
        var capTransaction = CreateCapTransaction(publisher, dbContext);
        if (previous != null)
        {
            // A factory before this one opened the transaction: wrap it in the CAP transaction.
            return AttachCapTransaction(capTransaction, previous, publisher, serviceProvider);
        }

        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return AttachCapTransaction(capTransaction, transaction, publisher, serviceProvider);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ICapTransaction CreateCapTransaction(ICapPublisher publisher, DbContext dbContext)
    {
        return dbContext.Database.ProviderName switch
        {
            "Microsoft.EntityFrameworkCore.SqlServer" =>
                ActivatorUtilities.CreateInstance<CapSqlServer::DotNetCore.CAP.SqlServerCapTransaction>(publisher.ServiceProvider),
            DatabaseProviderConfigurationExtensions.PostgreSqlProviderName =>
                ActivatorUtilities.CreateInstance<CapPostgreSql::DotNetCore.CAP.PostgreSqlCapTransaction>(publisher.ServiceProvider),
            var providerName => throw new InvalidOperationException(
                $"CAP transaction integration does not support EF provider '{providerName}'.")
        };
    }

    /// <summary>
    /// Makes the CAP transaction the outbox of the scope's events. It is attached only once its
    /// database transaction exists, so a failed or cancelled open leaves the scope as it was.
    /// </summary>
    private static CapEFDbTransaction AttachCapTransaction(
        ICapTransaction capTransaction,
        IDbContextTransaction transaction,
        ICapPublisher publisher,
        IServiceProvider serviceProvider)
    {
        capTransaction.DbTransaction = transaction;
        capTransaction.AutoCommit = false;
        publisher.Transaction = capTransaction;

        var scope = serviceProvider.GetRequiredService<CapTransactionScope>();
        scope.Current = capTransaction;
        scope.IsCommitStarted = false;
        return new CapEFDbTransaction(capTransaction, scope);
    }

    /// <summary>
    /// Registers the CAP EF Core storage implementation for the selected database provider.
    /// </summary>
    public static void UseConfiguredEntityFramework<TDbContext>(
        this CapOptions capOptions,
        DatabaseProvider databaseProvider)
        where TDbContext : DbContext
    {
        switch (databaseProvider)
        {
            case DatabaseProvider.SqlServer:
                CapSqlServer::Microsoft.Extensions.DependencyInjection.CapOptionsExtensions
                    .UseEntityFramework<TDbContext>(capOptions);
                break;
            case DatabaseProvider.PostgreSql:
                CapPostgreSql::Microsoft.Extensions.DependencyInjection.CapOptionsExtensions
                    .UseEntityFramework<TDbContext>(capOptions);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(databaseProvider));
        }
    }
}

public class NhCapEventBuilder
{
    private readonly IServiceCollection _serviceCollection;

    public NhCapEventBuilder AddSubscriber<TSubscriber, TEvent>()
        where TSubscriber : INhEventConsumer<TEvent>
        where TEvent : INhEvent
    {
        _serviceCollection.AddKeyedTransient(typeof(INhEventConsumerInternal), "nh-cap", typeof(TSubscriber));
        return this;
    }

    public NhCapEventBuilder AddCustomTopicSubscriber<TSubscriber>()
        where TSubscriber : INhCustomTopicEventConsumer
    {
        _serviceCollection.AddKeyedTransient(typeof(INhEventConsumerInternal), "nh-cap", typeof(TSubscriber));
        return this;
    }

    public NhCapEventBuilder WithPublishing()
    {
        _serviceCollection.AddTransient<INhEventPublisher, NhCapEventPublisher>();
        return this;
    }

    public NhCapEventBuilder WithOptions(Action<CapOptions> configure)
    {
        _serviceCollection.AddCap(configure);
        return this;
    }

    internal NhCapEventBuilder(IServiceCollection serviceCollection)
    {
        _serviceCollection = serviceCollection;
        _serviceCollection.AddSingleton<IConsumerServiceSelector, NhConsumerSelector>();
        _serviceCollection.AddOptions<NhEventOptions>();
    }
}

public class CapEFDbTransaction : IDbContextTransaction, IInfrastructure<DbTransaction>
{
    private readonly ICapTransaction _transaction;
    private readonly CapTransactionScope _scope;

    /// <summary>
    /// The EF transaction the CAP transaction wraps. CAP's SQL Server integration replaces its own
    /// reference after a commit, so the wrapper keeps this one to dispose it.
    /// </summary>
    private readonly IDbContextTransaction _dbContextTransaction;

    public CapEFDbTransaction(ICapTransaction transaction, CapTransactionScope scope)
    {
        _transaction = transaction;
        _scope = scope;
        _dbContextTransaction = (IDbContextTransaction)_transaction.DbTransaction!;
        TransactionId = _dbContextTransaction.TransactionId;
    }

    public Guid TransactionId { get; }

    public void Commit()
    {
        _scope.IsCommitStarted = true;
        _transaction.Commit();
    }

    public void Rollback()
    {
        _transaction.Rollback();
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        _scope.IsCommitStarted = true;
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        DoDispose();
        _dbContextTransaction.Dispose();
        ReleaseCapTransaction();
    }

    /// <summary>
    /// Disposes the EF transaction asynchronously, which rolls back an uncommitted transaction and
    /// closes the connection it opened, and then lets CAP clear its own transaction state.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        DoDispose();
        await _dbContextTransaction.DisposeAsync().ConfigureAwait(false);
        ReleaseCapTransaction();
    }

    public DbTransaction Instance
    {
        get
        {
            return _dbContextTransaction.GetDbTransaction();
        }
    }

    private void DoDispose()
    {
        if (_scope.Current == _transaction)
        {
            _scope.Current = null;
        }
    }

    /// <summary>
    /// The EF transaction is disposed already: CAP clears its reference to it without disposing it
    /// a second time.
    /// </summary>
    private void ReleaseCapTransaction()
    {
        _transaction.DbTransaction = null;
        _transaction.Dispose();
    }
}
