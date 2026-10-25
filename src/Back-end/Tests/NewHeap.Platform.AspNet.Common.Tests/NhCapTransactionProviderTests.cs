using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Events.Cap;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// CAP's transaction factory on SQL Server and PostgreSQL: it opens the repository transaction
/// asynchronously with the caller's token, and the CAP outbox commits and rolls back with the data.
/// </summary>
public sealed class NhCapTransactionProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task CapOpensTheTransactionAsynchronouslyAndCommitsTheEventWithTheData(RelationalTestProvider provider)
    {
        await using var host = await TransactionFactoryTestHost.CreateAsync(provider, withCap: true);
        Assert.Single(host.Services.GetServices<NhTransactionFactoryAsync>());
        Assert.Empty(host.Services.GetServices<NhTransactionFactory>());

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = TransactionFactoryTestHost.RepositoryOf(scope.ServiceProvider);
            var publisher = scope.ServiceProvider.GetRequiredService<INhEventPublisher>();
            var capScope = scope.ServiceProvider.GetRequiredService<CapTransactionScope>();

            await using var transaction = await repository.StartOrGetTransactionScopeAsync();
            Assert.True(transaction.IsMyTransaction);
            Assert.IsType<CapEFDbTransaction>(transaction.Transaction.DbContextTransaction);
            Assert.NotNull(capScope.Current);
            Assert.Equal(1, host.Starts.AsynchronousStarts);
            Assert.Equal(0, host.Starts.SynchronousStarts);

            repository.Add(new TransactionNote { Text = "committed" });
            await repository.SaveChangesAsync();
            await publisher.PublishAsync(new TransactionNoteCreated { Text = "committed" });
            Assert.Empty(host.Dispatcher.Published);

            await transaction.CommitAsync();
            Assert.Equal([TransactionNoteCreated.Topic], host.Dispatcher.Published);
        }

        Assert.Equal(["committed"], await host.ReadNotesAsync());
        Assert.Equal([TransactionNoteCreated.Topic], await host.ReadOutboxAsync());
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task AFailedUnitOfWorkDiscardsTheEventWithTheData(RelationalTestProvider provider)
    {
        await using var host = await TransactionFactoryTestHost.CreateAsync(provider, withCap: true);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = TransactionFactoryTestHost.RepositoryOf(scope.ServiceProvider);
            var publisher = scope.ServiceProvider.GetRequiredService<INhEventPublisher>();
            var capScope = scope.ServiceProvider.GetRequiredService<CapTransactionScope>();

            await Assert.ThrowsAsync<DbUpdateException>(async () =>
            {
                // A service's unit of work: the write after the publication fails, so the scope is
                // disposed without a commit.
                await using var transaction = await repository.StartOrGetTransactionScopeAsync();
                repository.Add(new TransactionNote { Text = "duplicate" });
                await repository.SaveChangesAsync();
                await publisher.PublishAsync(new TransactionNoteCreated { Text = "duplicate" });

                repository.Add(new TransactionNote { Text = "duplicate" });
                await repository.SaveChangesAsync();
                await transaction.CommitAsync();
            });

            Assert.Null(capScope.Current);
            Assert.Null(repository.Context.Database.CurrentTransaction);
        }

        Assert.Empty(host.Dispatcher.Published);
        Assert.Empty(await host.ReadNotesAsync());
        Assert.Empty(await host.ReadOutboxAsync());
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task RollbackDiscardsTheEventAndTheData(RelationalTestProvider provider)
    {
        await using var host = await TransactionFactoryTestHost.CreateAsync(provider, withCap: true);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = TransactionFactoryTestHost.RepositoryOf(scope.ServiceProvider);
            var publisher = scope.ServiceProvider.GetRequiredService<INhEventPublisher>();
            var capScope = scope.ServiceProvider.GetRequiredService<CapTransactionScope>();

            await using (var transaction = await repository.StartOrGetTransactionScopeAsync())
            {
                repository.Add(new TransactionNote { Text = "rolled-back" });
                await repository.SaveChangesAsync();
                await publisher.PublishAsync(new TransactionNoteCreated { Text = "rolled-back" });

                await transaction.RollbackAsync();
            }

            Assert.Null(capScope.Current);
            Assert.Null(repository.Context.Database.CurrentTransaction);

            // Disposed synchronously without a commit, the unit of work rolls back as well.
            using (var transaction = await repository.StartOrGetTransactionScopeAsync())
            {
                repository.Add(new TransactionNote { Text = "disposed" });
                await repository.SaveChangesAsync();
                await publisher.PublishAsync(new TransactionNoteCreated { Text = "disposed" });
            }

            Assert.Null(capScope.Current);
            Assert.Null(repository.Context.Database.CurrentTransaction);
        }

        Assert.Empty(host.Dispatcher.Published);
        Assert.Empty(await host.ReadNotesAsync());
        Assert.Empty(await host.ReadOutboxAsync());
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task NestedScopesJoinTheCapTransactionAndOnlyTheOwnerCommits(RelationalTestProvider provider)
    {
        await using var host = await TransactionFactoryTestHost.CreateAsync(provider, withCap: true);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = TransactionFactoryTestHost.RepositoryOf(scope.ServiceProvider);
            var publisher = scope.ServiceProvider.GetRequiredService<INhEventPublisher>();
            var capScope = scope.ServiceProvider.GetRequiredService<CapTransactionScope>();

            // The nested scope's commit does not commit: the owner's rollback discards its work.
            await using (var owner = await repository.StartOrGetTransactionScopeAsync())
            {
                var ownerCapTransaction = capScope.Current;
                await WriteInNestedScopeAsync(repository, publisher, "nested-rolled-back");

                Assert.Same(ownerCapTransaction, capScope.Current);
                Assert.NotNull(repository.Context.Database.CurrentTransaction);
                await owner.RollbackAsync();
            }

            Assert.Empty(host.Dispatcher.Published);

            // The owner's commit commits the nested scope's data and event with its own.
            await using (var owner = await repository.StartOrGetTransactionScopeAsync())
            {
                await WriteInNestedScopeAsync(repository, publisher, "nested-committed");
                repository.Add(new TransactionNote { Text = "owner-committed" });
                await repository.SaveChangesAsync();
                await publisher.PublishAsync(new TransactionNoteCreated { Text = "owner-committed" });

                await owner.CommitAsync();
            }

            Assert.Null(capScope.Current);
        }

        Assert.Equal([TransactionNoteCreated.Topic, TransactionNoteCreated.Topic], host.Dispatcher.Published);
        Assert.Equal(["nested-committed", "owner-committed"], await host.ReadNotesAsync());
        Assert.Equal([TransactionNoteCreated.Topic, TransactionNoteCreated.Topic], await host.ReadOutboxAsync());
        Assert.Equal(2, host.Starts.AsynchronousStarts);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task CancellationWhileOpeningLeavesNoTransactionAndNoCapTransaction(RelationalTestProvider provider)
    {
        await using var host = await TransactionFactoryTestHost.CreateAsync(provider, withCap: true);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = TransactionFactoryTestHost.RepositoryOf(scope.ServiceProvider);
            var publisher = scope.ServiceProvider.GetRequiredService<INhEventPublisher>();
            var capScope = scope.ServiceProvider.GetRequiredService<CapTransactionScope>();

            // Cancelled before the call: nothing opens.
            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    repository.StartOrGetTransactionScopeAsync(cancelled.Token));
            }

            Assert.Equal(0, host.Starts.AsynchronousStarts);

            // Cancelled while the provider opens the transaction: CAP passed the caller's token on.
            using (var cancellation = new CancellationTokenSource())
            {
                host.Starts.CancelDuringNextStart(cancellation);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    repository.StartOrGetTransactionScopeAsync(cancellation.Token));
            }

            Assert.Equal(1, host.Starts.AsynchronousStarts);
            Assert.Null(repository.Context.Database.CurrentTransaction);
            Assert.Null(capScope.Current);

            // The context and the CAP scope are as before: the next unit of work commits atomically.
            await using (var transaction = await repository.StartOrGetTransactionScopeAsync())
            {
                Assert.True(transaction.IsMyTransaction);
                repository.Add(new TransactionNote { Text = "after-cancellation" });
                await repository.SaveChangesAsync();
                await publisher.PublishAsync(new TransactionNoteCreated { Text = "after-cancellation" });

                await transaction.CommitAsync();
            }
        }

        Assert.Equal(["after-cancellation"], await host.ReadNotesAsync());
        Assert.Equal([TransactionNoteCreated.Topic], await host.ReadOutboxAsync());
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task CapWrapsTheTransactionOfASynchronousFactoryRegisteredAfterIt(RelationalTestProvider provider)
    {
        using var cancellation = new CancellationTokenSource();
        var cancelAfterOpening = true;
        await using var host = await TransactionFactoryTestHost.CreateAsync(provider, withCap: true, services =>
            services.AddSingleton<NhTransactionFactory>((previous, context, _) =>
            {
                var transaction = previous ?? context.Database.BeginTransaction(IsolationLevel.Serializable);
                if (cancelAfterOpening)
                {
                    cancelAfterOpening = false;
                    cancellation.Cancel();
                }

                return transaction;
            }));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = TransactionFactoryTestHost.RepositoryOf(scope.ServiceProvider);
            var publisher = scope.ServiceProvider.GetRequiredService<INhEventPublisher>();
            var capScope = scope.ServiceProvider.GetRequiredService<CapTransactionScope>();

            // A cancellation that arrives after the synchronous factory opened: CAP's factory stops
            // and the repository disposes the transaction the synchronous factory opened.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                repository.StartOrGetTransactionScopeAsync(cancellation.Token));

            Assert.Null(repository.Context.Database.CurrentTransaction);
            Assert.Null(capScope.Current);

            // The application's factory opens the transaction, synchronously and with its isolation
            // level, whatever the registration order, and CAP wraps it into the outbox.
            await using (var transaction = await repository.StartOrGetTransactionScopeAsync())
            {
                Assert.IsType<CapEFDbTransaction>(transaction.Transaction.DbContextTransaction);
                Assert.Equal(IsolationLevel.Serializable, transaction.Transaction.DbContextTransaction.GetDbTransaction().IsolationLevel);

                repository.Add(new TransactionNote { Text = "serializable" });
                await repository.SaveChangesAsync();
                await publisher.PublishAsync(new TransactionNoteCreated { Text = "serializable" });

                await transaction.CommitAsync();
            }
        }

        Assert.Equal(2, host.Starts.SynchronousStarts);
        Assert.Equal(0, host.Starts.AsynchronousStarts);
        Assert.Equal([TransactionNoteCreated.Topic], host.Dispatcher.Published);
        Assert.Equal(["serializable"], await host.ReadNotesAsync());
        Assert.Equal([TransactionNoteCreated.Topic], await host.ReadOutboxAsync());
    }

    private static async Task WriteInNestedScopeAsync(
        Repository<TransactionNote> repository,
        INhEventPublisher publisher,
        string text)
    {
        await using var nested = await repository.StartOrGetTransactionScopeAsync();
        Assert.False(nested.IsMyTransaction);

        repository.Add(new TransactionNote { Text = text });
        await repository.SaveChangesAsync();
        await publisher.PublishAsync(new TransactionNoteCreated { Text = text });

        await nested.CommitAsync();
    }
}
