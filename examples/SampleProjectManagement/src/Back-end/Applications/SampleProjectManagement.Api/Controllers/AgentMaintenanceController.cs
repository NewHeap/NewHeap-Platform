using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

/// <summary>
/// SPM-284: the application's maintenance page for its agents. Operators who manage projects pause
/// an agent around an import, stop it when it proposes wrongly and resume it, each with a reason.
/// </summary>
[ApiController]
[Route("agent-maintenance")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "app.project.manage")]
public sealed class AgentMaintenanceController(SampleAgentMaintenanceService maintenance) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("List the sample's agents with their state")]
    [EndpointDescription("Returns active, paused, stopped or configured-off per agent, who changed it when and why, and the events that wait while an agent is paused.")]
    [ProducesResponseType<NhAssistantAgentState[]>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        return Ok(await maintenance.ListAsync(cancellationToken));
    }

    [HttpPost("{agentId}/pause")]
    [EndpointSummary("Pause an agent")]
    [EndpointDescription("No new runs start; events wait and run after the resume. Running work finishes and approved proposals execute.")]
    [ProducesResponseType<NhAssistantAgentState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Pause(string agentId, [FromBody] AgentStateChangeMutateModel model, CancellationToken cancellationToken)
    {
        return ChangeAsync(agentId, SampleAgentStateChange.Pause, model, cancellationToken);
    }

    [HttpPost("{agentId}/stop")]
    [EndpointSummary("Stop an agent")]
    [EndpointDescription("Cancels the agent's active runs, withdraws their open proposals, drops waiting events and holds approved proposals until the agent is resumed.")]
    [ProducesResponseType<NhAssistantAgentState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Stop(string agentId, [FromBody] AgentStateChangeMutateModel model, CancellationToken cancellationToken)
    {
        return ChangeAsync(agentId, SampleAgentStateChange.Stop, model, cancellationToken);
    }

    [HttpPost("{agentId}/resume")]
    [EndpointSummary("Resume an agent")]
    [EndpointDescription("Makes a paused or stopped agent active: every project with waiting events gets one run and held approved proposals execute. An agent disabled in configuration answers 409.")]
    [ProducesResponseType<NhAssistantAgentState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Resume(string agentId, [FromBody] AgentStateChangeMutateModel model, CancellationToken cancellationToken)
    {
        return ChangeAsync(agentId, SampleAgentStateChange.Resume, model, cancellationToken);
    }

    [HttpPost("{agentId}/skills/{skillId}/{change}")]
    [EndpointSummary("Pause, stop or resume one skill of an agent")]
    [EndpointDescription("change is pause, stop or resume. The agent's other skills keep working; a stopped skill refuses its triggers and triage no longer chooses it.")]
    [ProducesResponseType<NhAssistantSkillState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangeSkill(
        string agentId,
        string skillId,
        string change,
        [FromBody] AgentStateChangeMutateModel model,
        CancellationToken cancellationToken)
    {
        if (HttpContext.GetUserId() is not { } operatorId)
        {
            return Unauthorized();
        }
        SampleAgentStateChange? parsed = change switch
        {
            "pause" => SampleAgentStateChange.Pause,
            "stop" => SampleAgentStateChange.Stop,
            "resume" => SampleAgentStateChange.Resume,
            _ => null
        };
        if (parsed is not { } value)
        {
            return NotFound();
        }

        var result = await maintenance.ChangeSkillAsync(agentId, skillId, value, operatorId, model.Reason, cancellationToken);
        if (result is null || result.GetResultItems().Any(item => item.Name == NhAssistantErrorCodes.SkillNotFound))
        {
            return NotFound();
        }

        return result.Success ? Ok(result.Data) : BadRequest();
    }

    private async Task<IActionResult> ChangeAsync(
        string agentId,
        SampleAgentStateChange change,
        AgentStateChangeMutateModel model,
        CancellationToken cancellationToken)
    {
        if (HttpContext.GetUserId() is not { } operatorId)
        {
            return Unauthorized();
        }

        var result = await maintenance.ChangeAsync(agentId, change, operatorId, model.Reason, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return result.Success
            ? Ok(result.Data)
            : result.GetResultItems().Any(item => item.Name == NhAssistantErrorCodes.AgentConfiguredOff)
                ? Conflict()
                : BadRequest();
    }
}
