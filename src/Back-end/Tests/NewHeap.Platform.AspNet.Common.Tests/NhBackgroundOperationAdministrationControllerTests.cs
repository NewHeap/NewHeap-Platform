using System.ComponentModel;
using System.Linq.Expressions;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Utilities;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Mapping;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhBackgroundOperationAdministrationControllerTests
{
    private const string AdministrationPolicy = "app.background-operation.administer";

    [Fact]
    public async Task EndpointsAreUnavailableUntilAnAdministrationPolicyIsConfigured()
    {
        var operations = Substitute.For<INhBackgroundOperationAdministrationService>();
        await using var services = CreateServices();
        var controller = CreateController(
            services,
            operations,
            administrationPolicy: null,
            CreateUser(Guid.NewGuid(), AdministrationPolicy));

        var result = await controller.GetById(Guid.NewGuid());

        result.Should().BeOfType<NotFoundResult>();
        await operations.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [Fact]
    public async Task AnonymousCallersAreRejected()
    {
        var operations = Substitute.For<INhBackgroundOperationAdministrationService>();
        await using var services = CreateServices();
        var controller = CreateController(
            services,
            operations,
            AdministrationPolicy,
            new ClaimsPrincipal(new ClaimsIdentity()));

        var result = await controller.GetById(Guid.NewGuid());

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task UsersWithoutTheAdministrationPolicyAreForbidden()
    {
        var operations = Substitute.For<INhBackgroundOperationAdministrationService>();
        await using var services = CreateServices();
        var controller = CreateController(
            services,
            operations,
            AdministrationPolicy,
            CreateUser(Guid.NewGuid(), "app.project.view"));

        var getResult = await controller.GetById(Guid.NewGuid());
        var cancelResult = await controller.Cancel(Guid.NewGuid());

        getResult.Should().BeOfType<ForbidResult>();
        cancelResult.Should().BeOfType<ForbidResult>();
        await operations.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        await operations.DidNotReceiveWithAnyArgs().RequestCancellationAsync(default, default, default);
    }

    [Fact]
    public async Task AdministratorsActInTheActiveDivisionAsThemselves()
    {
        var administratorId = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var view = new NhBackgroundOperationAdministrationViewModel
        {
            Id = operationId,
            OwnerUserId = Guid.NewGuid(),
            OwnerDisplayName = "owner@example.test"
        };
        var operations = Substitute.For<INhBackgroundOperationAdministrationService>();
        operations.GetAsync(operationId, divisionId, Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(view);
        operations.RequestCancellationAsync(operationId, administratorId, divisionId, Arg.Any<CancellationToken>())
            .Returns(TaskResult<NhBackgroundOperationAdministrationViewModel>.Succeeded(view));
        await using var services = CreateServices();
        var controller = CreateController(
            services,
            operations,
            AdministrationPolicy,
            CreateUser(
                administratorId,
                AdministrationPolicy,
                Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll),
            divisionId);

        var getResult = await controller.GetById(operationId);
        var cancelResult = await controller.Cancel(operationId);
        var missingResult = await controller.Retry(Guid.NewGuid());

        getResult.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(view);
        cancelResult.Should().BeOfType<AcceptedResult>()
            .Which.Value.Should().BeSameAs(view);
        missingResult.Should().BeOfType<NotFoundResult>();
        await operations.Received(1).RequestCancellationAsync(
            operationId,
            administratorId,
            divisionId,
            Arg.Any<CancellationToken>());
        await operations.DidNotReceiveWithAnyArgs().RetryAsync(default, default, default);
    }

    [Fact]
    public async Task ListPagesAreBoundedAndNeverLoadThePayload()
    {
        var view = new NhBackgroundOperationAdministrationViewModel { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid() };
        var operations = Substitute.For<INhBackgroundOperationAdministrationService>();
        operations.QueryForAdministration(Arg.Any<Guid?>())
            .Returns(Array.Empty<NhBackgroundOperation>().AsQueryable());
        var collection = Substitute.For<IHttpCollectionProcessingService>();
        Func<IQueryable<NhBackgroundOperation>, CancellationToken, Task<IQueryable<NhBackgroundOperation>>>? resultQuery = null;
        collection.GetCollectionResultModelAsync(
                Arg.Any<ICollectionRequestModel>(),
                Arg.Any<IQueryable<NhBackgroundOperation>>(),
                Arg.Any<Action<CollectionProcessingOptionsBuilder<NhBackgroundOperation, NhBackgroundOperationAdministrationViewModel>>>(),
                Arg.Do<Func<IQueryable<NhBackgroundOperation>, CancellationToken, Task<IQueryable<NhBackgroundOperation>>>?>(
                    value => resultQuery = value),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<(Expression<Func<NhBackgroundOperation, object>>, ListSortDirection)[]>())
            .Returns(new CollectionResultModel<NhBackgroundOperationAdministrationViewModel> { Items = [view] });
        await using var services = CreateServices();
        var controller = CreateController(
            services,
            operations,
            AdministrationPolicy,
            CreateUser(Guid.NewGuid(), AdministrationPolicy),
            collection: collection);
        var request = new NhBackgroundOperationAdministrationCollectionRequestModel { ItemsPerPage = 5_000 };

        var result = await controller.Get(request);

        result.Should().BeOfType<OkObjectResult>();
        request.ItemsPerPage.Should().Be(NhBackgroundOperationAdministrationController.MaxItemsPerPage);
        await operations.Received(1).PopulateOwnersAsync(
            Arg.Is<IReadOnlyCollection<NhBackgroundOperationAdministrationViewModel>>(items => items.Single() == view),
            Arg.Any<CancellationToken>());
        resultQuery.Should().NotBeNull();
        var page = new[]
        {
            new NhBackgroundOperation { Id = view.Id, PayloadJson = "{\"secret\":true}" }
        }.AsQueryable();
        (await resultQuery!(page, CancellationToken.None)).Single().PayloadJson.Should().Be("{}");
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => options.AddPolicy(
            AdministrationPolicy,
            policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, AdministrationPolicy)));
        return services.BuildServiceProvider();
    }

    private static NhBackgroundOperationAdministrationController CreateController(
        ServiceProvider services,
        INhBackgroundOperationAdministrationService operations,
        string? administrationPolicy,
        ClaimsPrincipal user,
        Guid? activeDivisionId = null,
        IHttpCollectionProcessingService? collection = null)
    {
        var httpContext = new DefaultHttpContext
        {
            User = user,
            RequestServices = services
        };
        if (activeDivisionId.HasValue)
        {
            httpContext.Request.Headers[Constants.HttpHeaderKeys.ActiveDivisionId] = activeDivisionId.Value.ToString();
        }

        return new NhBackgroundOperationAdministrationController(
            new ConfigurationBuilder().Build(),
            new Mapper(new MapperConfiguration(configuration =>
                configuration.AddProfile<AutomapperProfileConfiguration>())),
            NullLogger<NhBackgroundOperationAdministrationController>.Instance,
            Substitute.For<IStringLocalizer<NhBackgroundOperationAdministrationController>>(),
            collection ?? Substitute.For<IHttpCollectionProcessingService>(),
            operations,
            services.GetRequiredService<IAuthorizationService>(),
            new NhBackgroundOperationsOptions
            {
                AdministrationPolicy = administrationPolicy
            })
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    private static ClaimsPrincipal CreateUser(Guid userId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString())
        };
        claims.AddRange(permissions.Select(permission => new Claim(NhPlatformClaimTypes.Permission, permission)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
