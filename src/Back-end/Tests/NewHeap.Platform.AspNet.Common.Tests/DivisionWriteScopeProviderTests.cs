using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Tests.Relational;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The inherited writes of the base controllers stay inside the controller's own scope, on both
/// relational providers: an update or partial update of a row outside <c>GetQueryableAsync</c>
/// returns <c>404</c> and changes nothing, as a delete does. The division and division-user
/// controllers keep a manager without the division access-all permission inside the active
/// division, and a caller with that permission still manages every division.
/// </summary>
public sealed class DivisionWriteScopeProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task WritesStayInTheActiveDivisionOnEachRelationalProvider(RelationalTestProvider provider)
    {
        await using var host = await DivisionProviderTestHost.CreateAsync(provider);
        var world = await World.SeedAsync(host);

        await VerifyMembershipWritesAsync(host, world);
        await VerifyDivisionWritesAsync(host, world);
        await VerifyCompositeWritesAsync(host, world);
    }

    private static async Task VerifyMembershipWritesAsync(DivisionProviderTestHost host, World world)
    {
        var newcomer = await host.AddUserAsync("newcomer");

        await using (var scope = host.CreateScope())
        {
            var controller = MembershipController(scope, world.ManagerOfA, world.DivisionA);

            // A membership of another division: the body is refused before anything is stored.
            Assert.IsType<ForbidResult>(await controller.Create(Membership(world.DivisionB, world.ManagerOfA, world.ManagerRole)));

            // A membership of division B by its id, with or without a body that names B.
            Assert.IsType<ForbidResult>(await controller.Update(
                world.ManagerOfBMembership,
                Membership(world.DivisionB, world.ManagerOfB, world.MemberRole)));
            Assert.IsType<NotFoundResult>(await controller.Update(
                world.ManagerOfBMembership,
                Membership(world.DivisionA, world.ManagerOfB, world.MemberRole)));
            Assert.IsType<NotFoundResult>(await controller.Delete(world.ManagerOfBMembership));

            // Moving a membership of division A to division B is refused as well.
            Assert.IsType<ForbidResult>(await controller.Update(
                world.MemberOfAMembership,
                Membership(world.DivisionB, world.MemberOfA, world.ManagerRole)));
        }

        Assert.Empty(await host.ReadAsync(context => context.Set<NhDivisionUser>()
            .Where(x => x.UserId == world.ManagerOfA && x.DivisionId == world.DivisionB)
            .ToListAsync()));
        var untouched = await host.ReadAsync(context => context.Set<NhDivisionUser>()
            .Include(x => x.DivisionUserRoles)
            .SingleAsync(x => x.Id == world.ManagerOfBMembership));
        Assert.Equal(world.DivisionB, untouched.DivisionId);
        Assert.Equal([world.ManagerRole], untouched.DivisionUserRoles.Select(x => x.DivisionRoleId));
        var memberOfA = await host.ReadAsync(context => context.Set<NhDivisionUser>()
            .SingleAsync(x => x.Id == world.MemberOfAMembership));
        Assert.Equal(world.DivisionA, memberOfA.DivisionId);

        // The active division itself is managed as before.
        await using (var scope = host.CreateScope())
        {
            var controller = MembershipController(scope, world.ManagerOfA, world.DivisionA);

            Assert.IsType<OkObjectResult>(await controller.Create(Membership(world.DivisionA, newcomer, world.MemberRole)));
            Assert.IsType<OkResult>(await controller.Update(
                world.MemberOfAMembership,
                Membership(world.DivisionA, world.MemberOfA, world.ManagerRole)));
        }

        Assert.Single(await host.ReadAsync(context => context.Set<NhDivisionUser>()
            .Where(x => x.UserId == newcomer && x.DivisionId == world.DivisionA)
            .ToListAsync()));
        Assert.Equal(
            [world.ManagerRole],
            await host.ReadAsync(context => context.Set<NhDivisionUserRole>()
                .Where(x => x.DivisionUserId == world.MemberOfAMembership)
                .Select(x => x.DivisionRoleId)
                .ToListAsync()));

        // The division access-all permission manages every division.
        await using (var scope = host.CreateScope())
        {
            var controller = MembershipController(scope, world.Administrator, world.DivisionA, accessAll: true);

            Assert.IsType<OkObjectResult>(await controller.Create(Membership(world.DivisionB, newcomer, world.MemberRole)));
        }

        Assert.Single(await host.ReadAsync(context => context.Set<NhDivisionUser>()
            .Where(x => x.UserId == newcomer && x.DivisionId == world.DivisionB)
            .ToListAsync()));
    }

    private static async Task VerifyDivisionWritesAsync(DivisionProviderTestHost host, World world)
    {
        await using (var scope = host.CreateScope())
        {
            var controller = DivisionController(scope, world.ManagerOfA, world.DivisionA);

            Assert.IsType<NotFoundResult>(await controller.GetById(world.DivisionB));
            Assert.IsType<NotFoundResult>(await controller.Update(world.DivisionB, DivisionChange("Renamed from A")));
            Assert.IsType<NotFoundResult>(await controller.UpdatePartial(
                world.DivisionB,
                JObject.FromObject(new { description = "Changed from A" })));
            // An id that does not exist is outside the scope as well.
            Assert.IsType<NotFoundResult>(await controller.Update(Guid.NewGuid(), DivisionChange("Unknown")));

            // A new division lies outside the active division.
            Assert.IsType<ForbidResult>(await controller.Create(DivisionChange("Created from A")));
        }

        var divisionB = await host.ReadAsync(context => context.Set<NhDivision>().SingleAsync(x => x.Id == world.DivisionB));
        Assert.Equal(world.DivisionBName, divisionB.Name);
        Assert.Equal("Division B", divisionB.Description);
        Assert.False(await host.ReadAsync(context => context.Set<NhDivision>().AnyAsync(x => x.Name.StartsWith("Created from A"))));

        await using (var scope = host.CreateScope())
        {
            var controller = DivisionController(scope, world.ManagerOfA, world.DivisionA);

            Assert.IsType<OkResult>(await controller.Update(world.DivisionA, DivisionChange($"Division A renamed {host.Label}")));
            Assert.IsType<NoContentResult>(await controller.UpdatePartial(
                world.DivisionA,
                JObject.FromObject(new { description = "Changed in A" })));
        }

        var divisionA = await host.ReadAsync(context => context.Set<NhDivision>().SingleAsync(x => x.Id == world.DivisionA));
        Assert.Equal(($"Division A renamed {host.Label}", "Changed in A"), (divisionA.Name, divisionA.Description));

        await using (var scope = host.CreateScope())
        {
            var controller = DivisionController(scope, world.Administrator, world.DivisionA, accessAll: true);

            Assert.IsType<OkObjectResult>(await controller.Create(DivisionChange($"Created by an administrator {host.Label}")));
            Assert.IsType<NoContentResult>(await controller.UpdatePartial(
                world.DivisionB,
                JObject.FromObject(new { description = "Changed by an administrator" })));
        }

        Assert.Equal(
            "Changed by an administrator",
            await host.ReadAsync(context => context.Set<NhDivision>()
                .Where(x => x.Id == world.DivisionB)
                .Select(x => x.Description)
                .SingleAsync()));
    }

    private static async Task VerifyCompositeWritesAsync(DivisionProviderTestHost host, World world)
    {
        await using (var scope = host.CreateScope())
        {
            var httpContext = DivisionProviderTestHost.CreateRequest(scope, world.ManagerOfA, world.DivisionA);
            var service = ActivatorUtilities.CreateInstance<CompositeDivisionService>(scope.ServiceProvider);
            var controller = DivisionProviderTestHost.CreateController<CompositeDivisionController>(scope, httpContext, service);

            Assert.IsType<NotFoundResult>(await controller.Update(world.DivisionB, DivisionChange("Composite from A")));
            Assert.IsType<NotFoundResult>(await controller.UpdatePartial(
                world.DivisionB,
                JObject.FromObject(new { description = "Composite from A" })));

            Assert.IsType<OkResult>(await controller.Update(world.DivisionA, DivisionChange($"Composite A {host.Label}")));
            Assert.IsType<NoContentResult>(await controller.UpdatePartial(
                world.DivisionA,
                JObject.FromObject(new { description = "Composite in A" })));
        }

        var divisionB = await host.ReadAsync(context => context.Set<NhDivision>().SingleAsync(x => x.Id == world.DivisionB));
        Assert.Equal(world.DivisionBName, divisionB.Name);
        Assert.Equal("Changed by an administrator", divisionB.Description);
        var divisionA = await host.ReadAsync(context => context.Set<NhDivision>().SingleAsync(x => x.Id == world.DivisionA));
        Assert.Equal(($"Composite A {host.Label}", "Composite in A"), (divisionA.Name, divisionA.Description));
    }

    private static TestDivisionUserController MembershipController(
        AsyncServiceScope scope,
        Guid userId,
        Guid activeDivisionId,
        bool accessAll = false)
    {
        var httpContext = DivisionProviderTestHost.CreateRequest(scope, userId, activeDivisionId, accessAll);
        return DivisionProviderTestHost.CreateController<TestDivisionUserController>(scope, httpContext);
    }

    private static TestDivisionController DivisionController(
        AsyncServiceScope scope,
        Guid userId,
        Guid activeDivisionId,
        bool accessAll = false)
    {
        var httpContext = DivisionProviderTestHost.CreateRequest(scope, userId, activeDivisionId, accessAll);
        return DivisionProviderTestHost.CreateController<TestDivisionController>(scope, httpContext);
    }

    private static NhDivisionUserMutateModel Membership(Guid divisionId, Guid userId, Guid roleId)
    {
        return new NhDivisionUserMutateModel
        {
            DivisionId = divisionId,
            UserId = userId,
            RoleIds = [roleId]
        };
    }

    private static NhDivisionMutateModel DivisionChange(string name)
    {
        return new NhDivisionMutateModel
        {
            Name = name,
            Description = name,
            TimeZoneId = "UTC"
        };
    }

    private sealed record World(
        Guid DivisionA,
        Guid DivisionB,
        string DivisionBName,
        Guid MemberRole,
        Guid ManagerRole,
        Guid ManagerOfA,
        Guid ManagerOfB,
        Guid ManagerOfBMembership,
        Guid MemberOfA,
        Guid MemberOfAMembership,
        Guid Administrator)
    {
        public static async Task<World> SeedAsync(DivisionProviderTestHost host)
        {
            var divisionA = await host.AddDivisionAsync("Division A");
            var divisionB = await host.AddDivisionAsync("Division B");
            var memberRole = await host.AddDivisionRoleAsync($"member-{host.Label}", "general.view");
            var managerRole = await host.AddDivisionRoleAsync($"manager-{host.Label}", "general.view", "division.manage");

            var managerOfA = await host.AddUserAsync("manager-of-a");
            var managerOfB = await host.AddUserAsync("manager-of-b");
            var memberOfA = await host.AddUserAsync("member-of-a");
            var administrator = await host.AddUserAsync("administrator");

            await host.AddMembershipAsync(managerOfA, divisionA, [managerRole]);
            var managerOfBMembership = await host.AddMembershipAsync(managerOfB, divisionB, [managerRole]);
            var memberOfAMembership = await host.AddMembershipAsync(memberOfA, divisionA, [memberRole]);

            return new World(
                divisionA,
                divisionB,
                $"Division B {host.Label}",
                memberRole,
                managerRole,
                managerOfA,
                managerOfB,
                managerOfBMembership,
                memberOfA,
                memberOfAMembership,
                administrator);
        }
    }

    internal sealed class TestDivisionUserController(
        IConfiguration config,
        IMapper mapper,
        ILogger<NhBaseDivisionUserController<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, NhDivisionUserMutateModel, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel, DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>> logger,
        IStringLocalizer<NhBaseDivisionUserController<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, NhDivisionUserMutateModel, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel, DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>> localizer,
        INhUserManager userManager,
        NhDivisionUserService divisionUserService,
        IHttpCollectionProcessingService collectionProcessingService)
        : NhBaseDivisionUserController<
            NhUser,
            NhDivision,
            NhDivisionUser,
            NhDivisionRole,
            NhDivisionUserRole,
            NhDivisionRoleClaim,
            NhDivisionUserMutateModel,
            NhUserViewModel<NhDivisionViewModel>,
            NhDivisionViewModel,
            NhDivisionRoleViewModel,
            DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>(
                config,
                mapper,
                logger,
                localizer,
                userManager,
                divisionUserService,
                collectionProcessingService);

    internal sealed class TestDivisionController(
        IConfiguration config,
        IMapper mapper,
        ILogger<NhBaseDivisionController<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, NhDivisionMutateModel, NhDivisionViewModel, NhDivisionRoleViewModel>> logger,
        IStringLocalizer<NhBaseDivisionController<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, NhDivisionMutateModel, NhDivisionViewModel, NhDivisionRoleViewModel>> localizer,
        NhDivisionService divisionService,
        IHttpCollectionProcessingService collectionProcessingService)
        : NhBaseDivisionController<
            NhUser,
            NhDivision,
            NhDivisionUser,
            NhDivisionRole,
            NhDivisionUserRole,
            NhDivisionRoleClaim,
            NhDivisionMutateModel,
            NhDivisionViewModel,
            NhDivisionRoleViewModel>(
                config,
                mapper,
                logger,
                localizer,
                divisionService,
                collectionProcessingService)
    {
        public Task<IActionResult> UpdatePartial(Guid id, JObject partialUpdate)
        {
            return DoUpdatePartial(id, partialUpdate);
        }
    }

    /// <summary>
    /// A composite controller over divisions that, like the division controller, is scoped to the
    /// active division.
    /// </summary>
    internal sealed class CompositeDivisionController(
        IMapper mapper,
        ILogger<CompositeDivisionController> logger,
        IConfiguration config,
        IStringLocalizer<CompositeDivisionController> localizer,
        IHttpCollectionProcessingService collectionProcessingService,
        CompositeDivisionService service)
        : CompositeDbEntityProtectedNhBaseController<
            NhDivision,
            NhDivisionMutateModel,
            NhDivision,
            NhDivisionViewModel,
            CompositeDivisionService,
            DivisionCollectionRequestModel>(
                mapper,
                logger,
                config,
                localizer,
                collectionProcessingService,
                service)
    {
        protected override Task<IQueryable<NhDivision>> GetQueryableAsync(CancellationToken cancellationToken = default)
        {
            var query = _compositeDbEntityService.GetRepository().GetAll();

            return Task.FromResult(ApplyDivisionFilter(query, x => x.Id == ActiveDivisionId));
        }

        public Task<IActionResult> Update(Guid id, NhDivisionMutateModel mutateModel)
        {
            return DoUpdate(id, mutateModel);
        }

        public Task<IActionResult> UpdatePartial(Guid id, JObject partialUpdate)
        {
            return DoUpdatePartial(id, partialUpdate);
        }
    }

    internal sealed class CompositeDivisionService(
        IRepository<NhDivision> repository,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        IMapper mapper,
        IStringLocalizer<CompositeDivisionService> localizer,
        ValidationService validationService)
        : CompositeBaseDbEntityService<NhDivision, NhDivisionMutateModel, NhDivision, CompositeDivisionService>(
            repository,
            dbLogService,
            logHelperService,
            mapper,
            localizer,
            validationService)
    {
        public override Task<NhDivision?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return _repository.GetAll().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        }

        public override Task<TaskResult<NhDivision?>> CreateAsync(
            NhDivisionMutateModel mutateModel,
            Guid? committedByUserId = null,
            Action<NhDivision>? beforeSave = null,
            CancellationToken cancellationToken = default,
            CompositeBaseDbEntityServiceOperationOptions? options = null)
        {
            return DoCreateAsync(mutateModel, committedByUserId, beforeSave, cancellationToken, options);
        }

        public override Task<TaskResult<NhDivision?>> UpdateAsync(
            Guid id,
            NhDivisionMutateModel mutateModel,
            Guid? committedByUserId = null,
            Action<NhDivision>? beforeSave = null,
            CancellationToken cancellationToken = default,
            CompositeBaseDbEntityServiceOperationOptions? options = null)
        {
            return DoUpdateAsync(id, mutateModel, committedByUserId, beforeSave, cancellationToken, options);
        }

        public override Task<TaskResult<NhDivision?>> DeleteAsync(
            Guid id,
            Guid? committedByUserId = null,
            CancellationToken cancellationToken = default,
            CompositeBaseDbEntityServiceOperationOptions? options = null)
        {
            return DoDeleteAsync(id, committedByUserId, cancellationToken, options);
        }

        protected override Task ValidateCreateUpdateDeleteAsync(
            CreateUpdateDeleteValidateModel<NhDivision, NhDivision, NhDivisionMutateModel> model,
            CancellationToken cancellationToken = default)
        {
            return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
        }
    }
}
