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
/// an agent around an import, stop it when it proposes wrongly, switch it off while it should not
/// work on its own and resume it, each with a reason.
/// SPM-293 and SPM-294: they also put an agent or a skill in shadow or live and replace the
/// technical account an agent answers to, without a deploy.
/// SPM-326: they find the intake review that a project's creation event started.
/// </summary>
[ApiController]
[Route("agent-maintenance")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "app.project.manage")]
public sealed class AgentMaintenanceController(
    SampleAgentMaintenanceService maintenance,
    SampleIntakeReviewLookup intakeReviews) : ControllerBase
{
    [HttpGet("intake-reviews/{eventId:guid}")]
    [EndpointSummary("Find the intake review a project's creation started")]
    [EndpointDescription("eventId is the id of the project-created event, as the outbox shows it. Returns the queued input that holds the event in the reviewer's thread of the project and the run that took it, or the run the event started while the input still waits; a redelivered event finds its first delivery. 404 while no input holds the event.")]
    [ProducesResponseType<NhAssistantTriggerEventRun>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FindIntakeReview([FromRoute] Guid eventId, CancellationToken cancellationToken)
    {
        var review = await intakeReviews.FindAsync(eventId, cancellationToken);
        if (review is null)
        {
            return NotFound();
        }

        return Ok(review);
    }

    [HttpGet]
    [EndpointSummary("List the sample's agents with their state")]
    [EndpointDescription("Returns active, paused, stopped, off or configured-off per agent, who changed it when and why, and the events that wait while an agent is paused.")]
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

    [HttpPost("{agentId}/switch-off")]
    [EndpointSummary("Switch an agent off")]
    [EndpointDescription("The agent ignores its events, schedules, webhooks, hand-offs and follow-ups and nothing waits for it. Running work finishes, open proposals stay decidable and people still chat with it and start it by hand.")]
    [ProducesResponseType<NhAssistantAgentState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> SwitchOff(string agentId, [FromBody] AgentStateChangeMutateModel model, CancellationToken cancellationToken)
    {
        return ChangeAsync(agentId, SampleAgentStateChange.SwitchOff, model, cancellationToken);
    }

    [HttpPost("{agentId}/resume")]
    [EndpointSummary("Resume an agent")]
    [EndpointDescription("Makes a paused, stopped or off agent active: every project with waiting events gets one run and held approved proposals execute; an agent that was off starts nothing. An agent disabled in configuration answers 409.")]
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
    [EndpointSummary("Pause, stop, switch off or resume one skill of an agent")]
    [EndpointDescription("change is pause, stop, switch-off or resume. The agent's other skills keep working; a stopped skill refuses its triggers, a skill that is off ignores them, and triage no longer chooses either.")]
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
            "switch-off" => SampleAgentStateChange.SwitchOff,
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

    [HttpPost("{agentId}/shadow/{mode}")]
    [EndpointSummary("Put an agent in shadow or live")]
    [EndpointDescription("mode is shadow, live or code. In shadow the agent's runs work in shadow threads that only administrators see: nobody is notified and nothing executes. code lets the mode in code decide again. A shadow variant always works in shadow and answers 409.")]
    [ProducesResponseType<NhAssistantAgentState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeShadow(
        string agentId,
        string mode,
        [FromBody] AgentStateChangeMutateModel model,
        CancellationToken cancellationToken)
    {
        if (HttpContext.GetUserId() is not { } operatorId)
        {
            return Unauthorized();
        }
        if (ShadowChange(mode) is not { } change)
        {
            return NotFound();
        }

        var result = await maintenance.ChangeShadowAsync(agentId, change, operatorId, model.Reason, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return result.Success
            ? Ok(result.Data)
            : result.GetResultItems().Any(item => item.Name == NhAssistantErrorCodes.ShadowVariant) ? Conflict() : BadRequest();
    }

    [HttpPost("{agentId}/skills/{skillId}/shadow/{mode}")]
    [EndpointSummary("Put one skill of an agent in shadow or live")]
    [EndpointDescription("mode is shadow, live or code. Work that reaches a skill in shadow goes to the agent's shadow threads; the agent's other skills keep their mode.")]
    [ProducesResponseType<NhAssistantSkillState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeSkillShadow(
        string agentId,
        string skillId,
        string mode,
        [FromBody] AgentStateChangeMutateModel model,
        CancellationToken cancellationToken)
    {
        if (HttpContext.GetUserId() is not { } operatorId)
        {
            return Unauthorized();
        }
        if (ShadowChange(mode) is not { } change)
        {
            return NotFound();
        }

        var result = await maintenance.ChangeSkillShadowAsync(agentId, skillId, change, operatorId, model.Reason, cancellationToken);
        if (result is null || result.GetResultItems().Any(item => item.Name == NhAssistantErrorCodes.SkillNotFound))
        {
            return NotFound();
        }

        return result.Success
            ? Ok(result.Data)
            : result.GetResultItems().Any(item => item.Name == NhAssistantErrorCodes.ShadowVariant) ? Conflict() : BadRequest();
    }

    [HttpPost("{agentId}/service-account")]
    [EndpointSummary("Replace the technical account of an agent")]
    [EndpointDescription("accountId is the user that answers for the agent's new runs; it must exist and be allowed to sign in. Without it the account in configuration answers again. Started runs keep their account. An agent that answers to no technical account answers 409.")]
    [ProducesResponseType<NhAssistantAgentState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeServiceAccount(
        string agentId,
        [FromBody] AgentServiceAccountMutateModel model,
        CancellationToken cancellationToken)
    {
        if (HttpContext.GetUserId() is not { } operatorId)
        {
            return Unauthorized();
        }

        var result = await maintenance.ChangeServiceAccountAsync(agentId, model.AccountId, operatorId, model.Reason, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return result.Success
            ? Ok(result.Data)
            : result.GetResultItems().Any(item => item.Name == NhAssistantErrorCodes.ServiceAccountNotUsed) ? Conflict() : BadRequest();
    }

    private static SampleShadowChange? ShadowChange(string mode)
    {
        return mode switch
        {
            "shadow" => SampleShadowChange.Shadow,
            "live" => SampleShadowChange.Live,
            "code" => SampleShadowChange.Code,
            _ => null
        };
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
