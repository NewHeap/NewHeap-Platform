using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhUserManagerTest
{
    [Fact]
    public async Task GetValidClaimsReturnsEmptyWhenTheUserNoLongerExists()
    {
        var userId = Guid.NewGuid();
        var userStore = Substitute.For<IUserStore<NhUser>>();
        userStore
            .FindByIdAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns((NhUser?)null);
        var normalizer = Substitute.For<ILookupNormalizer>();
        var identityErrors = new IdentityErrorDescriber();
        using var roleManager = new RoleManager<NhUserRole>(
            Substitute.For<IRoleStore<NhUserRole>>(),
            [],
            normalizer,
            identityErrors,
            Substitute.For<ILogger<RoleManager<NhUserRole>>>());
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        using var userManager = new NhUserManager(
            Substitute.For<IWebHostEnvironment>(),
            userStore,
            Options.Create(new IdentityOptions()),
            Substitute.For<IPasswordHasher<NhUser>>(),
            [],
            [],
            normalizer,
            identityErrors,
            serviceProvider,
            Substitute.For<ILogger<UserManager<NhUser>>>(),
            Options.Create(new MicrosoftAuthSettings()),
            Substitute.For<IRepository<NhUser>>(),
            roleManager,
            new ValidationService(serviceProvider),
            Substitute.For<INhDbLogService>());

        var claims = await userManager.GetValidClaimsByUserIdAsync(
            userId,
            withDivision: true);

        Assert.Empty(claims);
    }
}
