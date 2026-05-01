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
    public static NhEventConfigurationBuilder AddCap(this NhEventConfigurationBuilder builder,
        Action<NhCapEventBuilder> configure)
    {
        var capBuilder = new NhCapEventBuilder(builder.ServiceCollection);
        configure(capBuilder);
        builder.ServiceCollection.AddScoped<CapTransactionScope>();
        builder.ServiceCollection.AddSingleton<NhTransactionFactory>((t, ctx,sp) =>
            {
                var publisher = sp.GetRequiredService<ICapPublisher>();
                // Transaction already exists, wrap it in a CapTransaction and return it
                publisher.Transaction = ActivatorUtilities.CreateInstance<SqlServerCapTransaction>(publisher.ServiceProvider);
                publisher.Transaction.DbTransaction = t ?? ctx.Database.BeginTransaction();
                publisher.Transaction.AutoCommit = false;
                
                var scope = sp.GetRequiredService<CapTransactionScope>();
                scope.Current = publisher.Transaction;
                return new CapEFDbTransaction(publisher.Transaction, scope);
            }
        );
        return builder;
    }
}

public class NhCapEventBuilder
{
    private readonly IServiceCollection _serviceCollection;

    public NhCapEventBuilder AddSubscriber<TSubscriber, TEvent>()
        where TSubscriber : INhEventConsumer<TEvent>
        where TEvent : INhEvent
    {
        _serviceCollection.AddKeyedTransient(typeof(INhEventConsumerInternal),"nh-cap", typeof(TSubscriber));
        return this;
    }

    public NhCapEventBuilder AddCustomTopicSubscriber<TSubscriber>()
    where TSubscriber : INhCustomTopicEventConsumer
    {
        _serviceCollection.AddKeyedTransient(typeof(INhEventConsumerInternal),"nh-cap", typeof(TSubscriber));
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

    public CapEFDbTransaction(ICapTransaction transaction, CapTransactionScope scope)
    {
        _transaction = transaction;
        _scope = scope;
        var dbContextTransaction = (IDbContextTransaction)_transaction.DbTransaction!;
        TransactionId = dbContextTransaction.TransactionId;
    }

    public Guid TransactionId { get; }

    public void Dispose()
    {
        _transaction.Dispose();
    }

    public void Commit()
    {
        _transaction.Commit();
    }

    public void Rollback()
    {
        _transaction.Rollback();
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        
        return new ValueTask(Task.Run(() =>
        {
            if (_scope.Current == _transaction)
            {
                _scope.Current = null;
            }
            _transaction.Dispose();
        }));
    }

    public DbTransaction Instance
    {
        get
        {
            var dbContextTransaction = (IDbContextTransaction)_transaction.DbTransaction!;
            return dbContextTransaction.GetDbTransaction();
        }
    }
}