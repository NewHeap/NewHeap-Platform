using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Policy;
using NewHeap.Platform.AspNet.Policy.AuthorizationHandlers;
using NewHeap.Platform.AspNet.Policy.Requirements;
using NSubstitute;
using System.Security.Claims;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class ActiveDivisionAccessHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ResourceDivisionId = Guid.NewGuid();
    private static readonly Guid HeaderDivisionId = Guid.NewGuid();

    [Fact]
    public async Task Without_a_request_or_scoped_resource_the_requirement_fails_without_throwing()
    {
        var userManager = CreateUserManager(allowedDivisionId: ResourceDivisionId);
        var handler = CreateHandler(httpContext: null, userManager);
        var context = CreateContext(resource: null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
        await userManager.DidNotReceiveWithAnyArgs().GetValidClaimsByUserIdAsync(default, default, default);
    }

    [Fact]
    public async Task Without_a_request_a_scoped_resource_names_the_division()
    {
        var userManager = CreateUserManager(allowedDivisionId: ResourceDivisionId);
        var handler = CreateHandler(httpContext: null, userManager);
        var context = CreateContext(new ScopedResource(ResourceDivisionId));

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_scoped_resource_wins_over_the_active_division_header()
    {
        var userManager = CreateUserManager(allowedDivisionId: HeaderDivisionId);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(userManager).BuildServiceProvider()
        };
        httpContext.Request.Headers[Constants.HttpHeaderKeys.ActiveDivisionId] = HeaderDivisionId.ToString();
        var handler = CreateHandler(httpContext, userManager);

        var scopedContext = CreateContext(new ScopedResource(ResourceDivisionId));
        await handler.HandleAsync(scopedContext);
        var headerContext = CreateContext(resource: null);
        await handler.HandleAsync(headerContext);

        scopedContext.HasSucceeded.Should().BeFalse("the resource names a division the user cannot access");
        headerContext.HasSucceeded.Should().BeTrue("without a scoped resource the header still decides");
    }

    private static INhUserManager CreateUserManager(Guid allowedDivisionId)
    {
        var userManager = Substitute.For<INhUserManager>();
        userManager.GetValidClaimsByUserIdAsync(UserId, true, Arg.Any<CancellationToken>())
            .Returns([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())]);
        userManager.DivisionAccessAsync(
                Arg.Any<Guid?>(),
                Arg.Any<IEnumerable<Claim>>(),
                Arg.Any<IEnumerable<Claim>?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid?>(0) == allowedDivisionId);
        return userManager;
    }

    private static ActiveDivisionAccessHandler<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim> CreateHandler(
        HttpContext? httpContext,
        INhUserManager userManager)
    {
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var scopeFactory = new ServiceCollection()
            .AddSingleton(userManager)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new ActiveDivisionAccessHandler<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim>(
            accessor,
            scopeFactory);
    }

    private static AuthorizationHandlerContext CreateContext(object? resource)
    {
        var requirement = new ActiveDivisionAccessRequirement([new Claim("permission", "project.view")]);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())],
            "test"));
        return new AuthorizationHandlerContext([requirement], user, resource);
    }

    private sealed record ScopedResource(Guid? DivisionId) : INhDivisionScopedResource;
}
