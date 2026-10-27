using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Mapping;

namespace SampleProjectManagement.Api.Controllers;

/// <summary>
/// SPM-211: the memberships of the active division, managed with the library's division-user
/// controller. Its policies <c>app.division.view</c> and <c>app.division.manage</c> are division
/// policies, so a division manager reads and changes the memberships of the division in the
/// <c>X-NH-ActiveDivisionId</c> header only: a membership of another division returns <c>404</c>,
/// and a body that names another division returns <c>403</c>.
/// </summary>
[Route("division-memberships")]
public sealed class DivisionMembershipController : NhBaseDivisionUserController<
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
    DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>
{
    public DivisionMembershipController(
        IConfiguration configuration,
        IMapper mapper,
        ILogger<NhBaseDivisionUserController<
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
            DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>> logger,
        IStringLocalizer<NhBaseDivisionUserController<
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
            DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>> localizer,
        INhUserManager userManager,
        NhDivisionUserService divisionUserService,
        IHttpCollectionProcessingService collectionProcessingService)
        : base(
            configuration,
            mapper,
            logger,
            localizer,
            userManager,
            divisionUserService,
            collectionProcessingService)
    {
    }

    [EndpointSummary("Get the memberships of the active division")]
    [EndpointDescription("Returns a filtered, ordered and paged collection of the active division's memberships with their user and division roles.")]
    [ProducesResponseType<CollectionResultModel<DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public override Task<IActionResult> Get(
        [FromQuery] DivisionUserCollectionRequestModel requestModel,
        CancellationToken cancellationToken = default)
    {
        return base.Get(requestModel, cancellationToken);
    }

    [EndpointSummary("Get a membership of the active division")]
    [EndpointDescription("Returns one membership of the active division; a membership of another division is not found.")]
    [ProducesResponseType<DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public override Task<IActionResult> GetById(Guid id)
    {
        return base.GetById(id);
    }

    [EndpointSummary("Add a member to the active division")]
    [EndpointDescription("Adds a person to the active division with division roles and an optional lockout window. A body that names another division is forbidden.")]
    [ProducesResponseType<DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public override Task<IActionResult> Create(
        [FromBody] NhDivisionUserMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        return base.Create(mutateModel, cancellationToken);
    }

    [EndpointSummary("Change a membership of the active division")]
    [EndpointDescription("Changes the division roles or the lockout window of a membership of the active division. A membership of another division is not found, and moving one to another division is forbidden.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public override Task<IActionResult> Update(
        [FromRoute] Guid id,
        [FromBody] NhDivisionUserMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        return base.Update(id, mutateModel, cancellationToken);
    }

    [EndpointSummary("Remove a member from the active division")]
    [EndpointDescription("Removes a membership of the active division; a membership of another division is not found.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public override Task<IActionResult> Delete(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default)
    {
        return base.Delete(id, cancellationToken);
    }
}
