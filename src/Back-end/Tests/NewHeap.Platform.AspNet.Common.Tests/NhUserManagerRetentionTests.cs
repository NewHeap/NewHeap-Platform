using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// Deleting a user through NewHeap Identity first lets every platform module forget the user, in
/// the same transaction.
/// </summary>
public sealed class NhUserManagerRetentionTests
{
    [Fact]
    public async Task DeletingAUserForgetsItInEveryModuleInOneTransaction()
    {
        var user = new NhUser { Id = Guid.NewGuid(), UserName = "member@example.test" };
        var retention = Substitute.For<INhRetention>();
        retention.ForgetUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(TaskResult.Succeeded());
        var setup = Create(retention);

        var result = await setup.UserManager.DeleteAsync(user);

        Assert.True(result.Succeeded);
        Received.InOrder(() =>
        {
            retention.ForgetUserAsync(user.Id, Arg.Any<CancellationToken>());
            setup.Store.DeleteAsync(user, Arg.Any<CancellationToken>());
            setup.Transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
        await setup.Transaction.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AModuleThatCannotForgetTheUserKeepsTheUser()
    {
        var user = new NhUser { Id = Guid.NewGuid(), UserName = "member@example.test" };
        var retention = Substitute.For<INhRetention>();
        retention.ForgetUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(TaskResult.Failed("background-operation-retention-blocked", "background-operation.retention-blocked"));
        var setup = Create(retention);

        var result = await setup.UserManager.DeleteAsync(user);

        Assert.False(result.Succeeded);
        Assert.Equal("background-operation-retention-blocked", Assert.Single(result.Errors).Code);
        await setup.Store.DidNotReceive().DeleteAsync(Arg.Any<NhUser>(), Arg.Any<CancellationToken>());
        await setup.Transaction.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await setup.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutModulesTheUserIsDeletedAsBefore()
    {
        var user = new NhUser { Id = Guid.NewGuid(), UserName = "member@example.test" };
        var setup = Create(retention: null);

        var result = await setup.UserManager.DeleteAsync(user);

        Assert.True(result.Succeeded);
        await setup.Store.Received(1).DeleteAsync(user, Arg.Any<CancellationToken>());
        await setup.Repository.DidNotReceive().StartOrGetTransactionScopeAsync(Arg.Any<CancellationToken>());
    }

    private static Setup Create(INhRetention? retention)
    {
        var store = Substitute.For<IUserStore<NhUser>>();
        store.DeleteAsync(Arg.Any<NhUser>(), Arg.Any<CancellationToken>()).Returns(IdentityResult.Success);
        var transaction = Substitute.For<INhDbTransactionScope>();
        var repository = Substitute.For<IRepository<NhUser>>();
        repository.StartOrGetTransactionScopeAsync(Arg.Any<CancellationToken>()).Returns(transaction);

        var services = new ServiceCollection();
        if (retention is not null)
        {
            services.AddSingleton(retention);
        }

        var serviceProvider = services.BuildServiceProvider();
        var normalizer = Substitute.For<ILookupNormalizer>();
        var errors = new IdentityErrorDescriber();
        var roleManager = new RoleManager<NhUserRole>(
            Substitute.For<IRoleStore<NhUserRole>>(),
            [],
            normalizer,
            errors,
            Substitute.For<ILogger<RoleManager<NhUserRole>>>());
        var userManager = new NhUserManager(
            Substitute.For<IWebHostEnvironment>(),
            store,
            Options.Create(new IdentityOptions()),
            Substitute.For<IPasswordHasher<NhUser>>(),
            [],
            [],
            normalizer,
            errors,
            serviceProvider,
            Substitute.For<ILogger<UserManager<NhUser>>>(),
            Options.Create(new MicrosoftAuthSettings()),
            repository,
            roleManager,
            new ValidationService(serviceProvider),
            Substitute.For<INhDbLogService>());
        return new Setup(userManager, store, repository, transaction);
    }

    private sealed record Setup(
        NhUserManager UserManager,
        IUserStore<NhUser> Store,
        IRepository<NhUser> Repository,
        INhDbTransactionScope Transaction);
}
