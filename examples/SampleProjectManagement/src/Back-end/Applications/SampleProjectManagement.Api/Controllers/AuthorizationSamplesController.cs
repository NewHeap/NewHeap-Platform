using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Services;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Models;
using System.Security.Claims;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[Route("authorization-samples")]
public sealed class AuthorizationSamplesController : ControllerBase
{
    private readonly INhAuthenticationService _authenticationService;

    public AuthorizationSamplesController(
        INhAuthenticationService authenticationService)
    {
        _authenticationService = authenticationService;
    }

    [HttpGet("application/view")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Enter the application viewer area")]
    [EndpointDescription("Demonstrates application-wide visibility granted by a role carrying app.project.view.")]
    [ProducesResponseType<AuthorizationProbeSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ApplicationView() =>
        Ok(CreateResponse(
            "application-role",
            "app.project.view",
            "The current application role grants project visibility."));

    [HttpGet("application/manage")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Enter the application manager area")]
    [EndpointDescription("Demonstrates an action visible to the manager role but forbidden to the viewer role.")]
    [ProducesResponseType<AuthorizationProbeSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ApplicationManage() =>
        Ok(CreateResponse(
            "application-role",
            "app.project.manage",
            "The current application role grants project management."));

    [HttpGet("division/view")]
    [Authorize(Policy = "app.active-division.project.view")]
    [EndpointSummary("Enter the active-division project area")]
    [EndpointDescription("Demonstrates access granted by a division role only inside the active division.")]
    [ProducesResponseType<AuthorizationProbeSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult DivisionView() =>
        Ok(CreateResponse(
            "active-division",
            "project.view",
            "The active division role grants project visibility."));

    [HttpGet("projects/{projectId:guid}/confidential")]
    [Authorize(Policy = SampleAuthorizationPolicies.ProjectConfidentialView)]
    [EndpointSummary("View confidential project information")]
    [EndpointDescription("Accepts an application, active-division or project-specific permission. The project permission is valid only for the matching project in the active division.")]
    [ProducesResponseType<AuthorizationProbeSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ProjectConfidential([FromRoute] Guid projectId) =>
        Ok(CreateResponse(
            "application-or-division-or-project",
            "confidential.view",
            "One of the three authorization levels grants confidential project visibility.",
            projectId));

    [HttpGet("overrides/runtime-claims")]
    [Authorize]
    [EndpointSummary("Inspect request-scoped authorization claims")]
    [EndpointDescription("Shows the custom authentication service and the division/project claims restored from current database state by IClaimsTransformation for this request.")]
    [ProducesResponseType<AuthenticationOverrideProbeSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult RuntimeClaims()
    {
        var runtimeClaims = User.Claims
            .Where(SampleRuntimeAuthorizationClaims.IsRequestScoped)
            .OrderBy(claim => claim.Type)
            .ThenBy(claim => claim.Value)
            .Select(claim => new RuntimeAuthorizationClaimSample(
                claim.Type,
                claim.Value))
            .ToArray();

        return Ok(new AuthenticationOverrideProbeSample(
            _authenticationService.GetType().Name,
            "Application claims stay in the JWT; volatile division and project claims do not.",
            "IClaimsTransformation restores current division and project claims once per request.",
            runtimeClaims.Length > 0,
            HttpContext.GetUserId(),
            HttpContext.GetActiveDivisionId(),
            runtimeClaims,
            HttpContext.TraceIdentifier));
    }

    private AuthorizationProbeSample CreateResponse(
        string level,
        string permission,
        string message,
        Guid? projectId = null)
    {
        return new AuthorizationProbeSample(
            level,
            permission,
            message,
            HttpContext.GetActiveDivisionId(),
            projectId,
            User.FindAll(ClaimTypes.Role)
                .Select(claim => claim.Value)
                .OrderBy(role => role)
                .ToArray());
    }
}
