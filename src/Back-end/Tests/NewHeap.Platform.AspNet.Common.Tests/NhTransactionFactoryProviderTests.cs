using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// How <c>StartTransactionAsync</c> runs the registered transaction factories on SQL Server and
/// PostgreSQL: the synchronous <see cref="NhTransactionFactory"/> as before, then the asynchronous
/// <see cref="NhTransactionFactoryAsync"/>, and a failing factory leaves no transaction behind.
/// </summary>
public sealed class NhTransactionFactoryProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ASynchronousFactoryStillOpensTheTransaction(RelationalTestProvider provider)
    {
        var calls = 0;
        await using var host = await TransactionFactoryTestHost.CreateAsync(provider, withCap: false, services =>
            services.AddSingleton<NhTransactionFactory>((previous, context, _) =>
            {
                Interlocked.Increment(ref calls);
                return previous ?? context.Database.BeginTransaction();
            }));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = TransactionFactoryTestHost.RepositoryOf(scope.ServiceProvider);

            await using (var transaction = await repository.StartOrGetTransactionScopeAsync())
            {
                Assert.True(transaction.IsMyTransaction);
                repository.Add(new TransactionNote { Text = "committed" });
                await repository.SaveChangesAsync();
                await transaction.CommitAsync();
            }

            await using (var transaction = await repository.StartOrGetTransactionScopeAsync())
            {
                repository.Add(new TransactionNote { Text = "rolled-back" });
                await repository.SaveChangesAsync();
                await transaction.RollbackAsync();
            }
        }

        Assert.Equal(2, calls);
        Assert.Equal(2, host.Starts.SynchronousStarts);
        Assert.Equal(0, host.Starts.AsynchronousStarts);
        Assert.Equal(["committed"], await host.ReadNotesAsync());
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task SynchronousFactoriesRunBeforeAsynchronousOnes(RelationalTestProvider provider)
    {
        var order = new List<string>();
        IDbContextTransaction? receivedByAsynchronousFactory = null;
        IDbContextTransaction? openedBySynchronousFactory = null;
        await using var host = await TransactionFactoryTestHost.CreateAsync(provider, withCap: false, services =>
        {
            // Registered first, but runs after every synchronous factory.
            services.AddSingleton<NhTransactionFactoryAsync>((previous, _, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("asynchronous");
                receivedByAsynchronousFactory = previous;
                return Task.FromResult(previous!);
            });
            services.AddSingleton<NhTransactionFactory>((previous, context, _) =>
            {
                order.Add("synchronous");
                openedBySynchronousFactory = previous ?? context.Database.BeginTransaction();
                return openedBySynchronousFactory;
            });
        });

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = TransactionFactoryTestHost.RepositoryOf(scope.ServiceProvider);

            await using var transaction = await repository.StartTransactionAsync();
            Assert.Equal(["synchronous", "asynchronous"], order);
            Assert.NotNull(openedBySynchronousFactory);
            Assert.Same(openedBySynchronousFactory, receivedByAsynchronousFactory);
            Assert.Same(openedBySynchronousFactory, transaction.DbContextTransaction);

            repository.Add(new TransactionNote { Text = "composed" });
            await repository.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Assert.Equal(["composed"], await host.ReadNotesAsync());
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task AFailingAsynchronousFactoryDisposesTheTransactionOpenedBeforeIt(RelationalTestProvider provider)
    {
        var fail = true;
        await using var host = await TransactionFactoryTestHost.CreateAsync(provider, withCap: false, services =>
        {
            services.AddSingleton<NhTransactionFactory>((previous, context, _) =>
                previous ?? context.Database.BeginTransaction());
            services.AddSingleton<NhTransactionFactoryAsync>((previous, _, _, _) =>
            {
                if (fail)
                {
                    fail = false;
                    throw new InvalidOperationException("The factory could not wrap the transaction.");
                }

                return Task.FromResult(previous!);
            });
        });

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = TransactionFactoryTestHost.RepositoryOf(scope.ServiceProvider);

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.StartOrGetTransactionScopeAsync());
            Assert.Null(repository.Context.Database.CurrentTransaction);

            // The context holds no transaction the failed call opened, so the next call owns its own.
            await using var transaction = await repository.StartOrGetTransactionScopeAsync();
            Assert.True(transaction.IsMyTransaction);
            repository.Add(new TransactionNote { Text = "after-failure" });
            await repository.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Assert.Equal(["after-failure"], await host.ReadNotesAsync());
    }
}
