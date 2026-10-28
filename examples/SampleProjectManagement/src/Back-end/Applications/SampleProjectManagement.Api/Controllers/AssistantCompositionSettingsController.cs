using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Controllers;

/// <summary>
/// SPM-371: the portal's own setting whether the assistant's agents use compositions and the script
/// tool. The sample serves its divisions without tenants, so it has one environment. Administrators of
/// the assistant change it; what the assistant then actually does shows next to it through
/// <c>GET api/assistant/admin/agents/{id}/compositions</c>.
/// </summary>
[ApiController]
[Route("assistant-composition-settings")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = SampleAssistantComposition.AdminPolicy)]
public sealed class AssistantCompositionSettingsController(
    SampleAssistantCompositionSettings settings,
    INhAssistantAgentDirectory agents) : ControllerBase
{
    /// <summary>The sample's one environment: it has no tenants.</summary>
    private const string Environment = "";

    [HttpGet]
    [EndpointSummary("List the agents' settings of compositions and scripts")]
    [EndpointDescription("Returns the stored setting per agent of the sample's environment, ordered by agent; mcp is the MCP exposure. An agent without a stored setting uses neither. The ceiling in code still applies: the project assistant allows both, agents created in the administration the script tool only.")]
    [ProducesResponseType<AssistantCompositionSettingViewModel[]>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var stored = await settings.ListAsync(Environment, cancellationToken);
        return Ok(stored.Select(ToViewModel).ToArray());
    }

    [HttpPut("{agentId}")]
    [EndpointSummary("Switch compositions and scripts for one agent")]
    [EndpointDescription("Stores whether the agent uses compositions and the script tool in the sample's environment; the next turn, run step or MCP request follows it without a restart. agentId is an agent of the assistant or mcp for the MCP exposure; another id answers 404.")]
    [ProducesResponseType<AssistantCompositionSettingViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Put(
        [FromRoute] string agentId,
        [FromBody] AssistantCompositionSettingMutateModel model,
        CancellationToken cancellationToken)
    {
        if (HttpContext.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        if (agentId != SampleAssistantCompositionSwitch.McpAgentId
            && (agentId.Length > AssistantCompositionSetting.MaxAgentIdLength || await agents.GetAsync(agentId, cancellationToken) is null))
        {
            return NotFound();
        }

        var saved = await settings.SaveAsync(Environment, agentId, model.Compositions, model.Scripts, userId, cancellationToken);
        return Ok(ToViewModel(saved));
    }

    private static AssistantCompositionSettingViewModel ToViewModel(SampleAssistantCompositionSetting setting)
    {
        return new AssistantCompositionSettingViewModel
        {
            AgentId = setting.AgentId,
            Compositions = setting.Compositions,
            Scripts = setting.Scripts
        };
    }
}
