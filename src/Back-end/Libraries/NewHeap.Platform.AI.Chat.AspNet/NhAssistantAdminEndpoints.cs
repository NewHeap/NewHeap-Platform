using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Preferences for every assistant user and the administration API behind the admin policy.
/// Both sit inside the kill-switch group, so a disabled assistant answers <c>404</c>.
/// </summary>
internal static class NhAssistantAdminEndpoints
{
    public static void Map(RouteGroupBuilder enabled, string adminPolicy)
    {
        enabled.MapGet("preferences", GetPreferencesAsync)
            .WithName("NhAssistantGetPreferences")
            .WithSummary("Get my assistant preferences")
            .WithDescription("Returns the caller's style preferences, or the defaults.")
            .Produces<NhAssistantPreferencesDto>();
        enabled.MapPut("preferences", PutPreferencesAsync)
            .WithName("NhAssistantUpdatePreferences")
            .WithSummary("Update my assistant preferences")
            .WithDescription("Stores the caller's style preferences. Custom instructions are limited to 1,000 characters.")
            .Produces<NhAssistantPreferencesDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest);

        var admin = enabled.MapGroup("admin");
        admin.AddEndpointFilter(async (context, next) =>
        {
            // Authorized in a filter instead of RequireAuthorization, so that a caller without
            // the admin policy receives the contract body { code: "assistant-forbidden", messageKey }.
            var httpContext = context.HttpContext;
            var authorization = httpContext.RequestServices.GetRequiredService<IAuthorizationService>();
            var allowed = await authorization.AuthorizeAsync(httpContext.User, adminPolicy);
            return allowed.Succeeded
                ? await next(context)
                : NhAssistantHttpErrors.Error(NhAssistantAdminErrorCodes.Forbidden);
        });
        admin.MapGet("context", GetContextAsync)
            .WithName("NhAssistantAdminGetContext")
            .WithSummary("Get the application context")
            .WithDescription("Returns the current application context.")
            .Produces<NhAssistantApplicationContextDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPut("context", PutContextAsync)
            .WithName("NhAssistantAdminUpdateContext")
            .WithSummary("Update the application context")
            .WithDescription("Stores a new version when expectedVersion is current; otherwise 409.")
            .Produces<NhAssistantApplicationContextDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("context/versions", GetContextVersionsAsync)
            .WithName("NhAssistantAdminGetContextVersions")
            .WithSummary("List application context versions")
            .WithDescription("Returns the version history without texts.")
            .Produces<NhAssistantApplicationContextVersionDto[]>();
        admin.MapGet("tools", GetToolsAsync)
            .WithName("NhAssistantAdminGetTools")
            .WithSummary("List tools for agents")
            .WithDescription("Returns local, bridge and MCP tools that agents can be given.")
            .Produces<NhAssistantToolCatalogEntryDto[]>();
        admin.MapGet("agents", GetAgentsAsync)
            .WithName("NhAssistantAdminGetAgents")
            .WithSummary("List agents")
            .WithDescription("Returns code and administrator agents with their run state, including paused, stopped, switched-off and code-disabled agents.")
            .Produces<NhAssistantAdminAgentDto[]>();
        admin.MapPost("agents", CreateAgentAsync)
            .WithName("NhAssistantAdminCreateAgent")
            .WithSummary("Create an agent")
            .WithDescription("Creates an administrator agent, which starts active. isEnabled is accepted and ignored.")
            .Produces<NhAssistantAdminAgentDto>(StatusCodes.Status201Created)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPut("agents/{id}", UpdateAgentAsync)
            .WithName("NhAssistantAdminUpdateAgent")
            .WithSummary("Update an agent")
            .WithDescription("Updates or overrides an agent when expectedVersion is current; otherwise 409. isEnabled is accepted and ignored: the run state changes with POST agents/{id}/state.")
            .Produces<NhAssistantAdminAgentDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost("agents/{id}/reset", ResetAgentAsync)
            .WithName("NhAssistantAdminResetAgent")
            .WithSummary("Reset a code agent")
            .WithDescription("Restores a code agent to its code definition and removes its MCP assignments. The run state stays as it is.")
            .Produces<NhAssistantAdminAgentDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost("agents/{id}/kill-switch", SetKillSwitchAsync)
            .WithName("NhAssistantAdminSetAgentKillSwitch")
            .WithSummary("Stop or resume the runs of an agent")
            .WithDescription("engaged: true stops the agent: its triggers are rejected, its active runs cancelled and its approved proposals held. engaged: false resumes it; held proposals are executed then. The same as POST agents/{id}/state without a reason.")
            .Produces<NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantKillSwitchDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("agents/{id}/state", GetAgentStateAsync)
            .WithName("NhAssistantAdminGetAgentState")
            .WithSummary("Get the run state of an agent")
            .WithDescription("Returns active, paused, stopped, off or configured-off, who changed it when and why, and the inputs that wait while the agent is paused.")
            .Produces<NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantAgentState>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPost("agents/{id}/skills/{skillId}/state", SetSkillStateAsync)
            .WithName("NhAssistantAdminSetSkillState")
            .WithSummary("Pause, stop, switch off or resume one skill of an agent")
            .WithDescription("paused: triggers that name the skill let their events wait and triage no longer offers it. stopped: its active runs are cancelled and its waiting events dropped. off: triggers that name the skill are ignored (reason skill-off), rules and triage no longer route to it, its running runs finish and nothing waits. active: resumes it with one run per thread with waiting events; a skill that was off starts nothing. The agent's other skills keep working; the agent's own state covers all skills.")
            .Produces<NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantSkillState>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPost("agents/{id}/skills/{skillId}/shadow", SetSkillShadowAsync)
            .WithName("NhAssistantAdminSetSkillShadow")
            .WithSummary("Put one skill of an agent in shadow or live")
            .WithDescription("mode shadow: work that reaches the skill goes to the agent's shadow threads, which only administrators see, where nobody is notified and nothing executes. live: the skill works live. code: the mode in code decides again. A skill of an agent in shadow works in shadow whatever its own mode; a shadow variant cannot change (409).")
            .Produces<NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantSkillState>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost("agents/{id}/shadow", SetAgentShadowAsync)
            .WithName("NhAssistantAdminSetAgentShadow")
            .WithSummary("Put an agent in shadow or live")
            .WithDescription("mode shadow: the agent's runs work in its shadow threads, which only administrators see; nobody is notified, nothing executes and consultations and requests are refused. live: the agent works live. code: the mode in code decides again. Started runs finish in their mode; the live threads and their open proposals stay. A shadow variant cannot change (409).")
            .Produces<NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantAgentState>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost("agents/{id}/service-account", SetAgentServiceAccountAsync)
            .WithName("NhAssistantAdminSetAgentServiceAccount")
            .WithSummary("Change the technical account of an agent")
            .WithDescription("actorId: the account that answers for the agent's new runs; it must exist and be allowed to sign in. null: the account in code answers again. Started runs and their proposals keep their account. An agent that does not answer to a technical account in code cannot change (409).")
            .Produces<NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantAgentState>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost("agents/{id}/state", SetAgentStateAsync)
            .WithName("NhAssistantAdminSetAgentState")
            .WithSummary("Pause, stop, switch off or resume an agent")
            .WithDescription("paused: no new runs start and events wait; running runs finish and approved proposals execute. stopped: active runs are cancelled, waiting inputs end, approved proposals are held and events are refused. off: events, schedules, webhooks, hand-offs and follow-ups are ignored (reason agent-off) and nothing waits; running runs finish, open proposals stay decidable and people still chat and start the agent by hand. active: resumes, with one run per thread that has waiting inputs; an agent that was off starts nothing. A code-disabled agent cannot be resumed (409). The reason has at most 500 characters.")
            .Produces<NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantAgentState>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        AgentRuns.NhAssistantProposalFeedbackEndpoints.MapAdmin(admin);
        AgentRuns.NhAssistantAgentEvaluationEndpoints.MapAdmin(admin);
        AgentRuns.NhAssistantAgentConfigurationEndpoints.MapAdmin(admin);
        AgentRuns.NhAssistantRunHistoryEndpoints.MapAdmin(admin);
        AgentRuns.NhAssistantBoardEndpoints.MapAdmin(admin);
        admin.MapDelete("agents/{id}", DeleteAgentAsync)
            .WithName("NhAssistantAdminDeleteAgent")
            .WithSummary("Delete an agent")
            .WithDescription("Deletes an administrator agent. Code agents return 409; disable them instead.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("mcp-servers", GetMcpServersAsync)
            .WithName("NhAssistantAdminGetMcpServers")
            .WithSummary("List MCP servers")
            .WithDescription("Returns the MCP servers without secrets.")
            .Produces<NhAssistantMcpServerDto[]>();
        admin.MapGet("mcp-auth-modes", GetMcpAuthModes)
            .WithName("NhAssistantAdminGetMcpAuthModes")
            .WithSummary("List the application's MCP authentication modes")
            .WithDescription("Returns the authentication modes the application registered in code with AddMcpCredentialProvider, by name, with whose credential each sends (organization or person). The built-in modes are not listed.")
            .Produces<NhAssistantMcpAuthModeDto[]>();
        admin.MapPost("mcp-servers", CreateMcpServerAsync)
            .WithName("NhAssistantAdminCreateMcpServer")
            .WithSummary("Connect an MCP server")
            .WithDescription("Stores an MCP server; the secret is protected and never returned. headlessUse starts at never unless the body sets organization. Invalid fields answer 400 assistant-validation with errors per field (id, displayName, url, authMode, headerName, secret, secretReference, userName, clientCertificateReference, transport, connectionScope, requiredPolicy, headlessUse and the dotted oauth.scopes, oauth.clientId, oauth.clientSecret, oauth.clientSecretReference, oauth.organizationAccountLabel, oauth.confirmAuthorization); a reference that names no available secret or certificate is not-found at its field. Confirming the authorization runs discovery (502 when it fails); a secret resolver that fails answers 503 assistant-mcp-secret-unavailable.")
            .Produces<NhAssistantMcpServerDto>(StatusCodes.Status201Created)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status502BadGateway)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status503ServiceUnavailable);
        admin.MapPut("mcp-servers/{id}", UpdateMcpServerAsync)
            .WithName("NhAssistantAdminUpdateMcpServer")
            .WithSummary("Update an MCP server")
            .WithDescription("Updates a server. An omitted secret is kept and an empty secret is cleared; an omitted headlessUse is kept. Field errors, discovery and secret failures answer as for creating a server.")
            .Produces<NhAssistantMcpServerDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status502BadGateway)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status503ServiceUnavailable);
        admin.MapDelete("mcp-servers/{id}", DeleteMcpServerAsync)
            .WithName("NhAssistantAdminDeleteMcpServer")
            .WithSummary("Delete an MCP server")
            .WithDescription("Deletes a server, its tools and its agent assignments.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPost("mcp-servers/{id}/test", TestMcpServerAsync)
            .WithName("NhAssistantAdminTestMcpServer")
            .WithSummary("Test an MCP server")
            .WithDescription("Connects and lists the remote tools without storing them. A server with an organization credential records the outcome as its connection state; a server tested with the administrator's own credential does not.")
            .Produces<NhAssistantMcpServerTestResultDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPost("mcp-servers/{id}/sync", SyncMcpServerAsync)
            .WithName("NhAssistantAdminSyncMcpServer")
            .WithSummary("Synchronize MCP tools")
            .WithDescription("Stores the remote tools. New tools start disabled; changed schemas disable a tool. A failed connection answers with its code: 409 when a connection or a stored secret needs an administrator, 502 for the remote server, 503 when a secret resolver fails. Only a server with an organization credential records a failure as its connection state.")
            .Produces<NhAssistantMcpToolDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status502BadGateway)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status503ServiceUnavailable);
        admin.MapGet("mcp-servers/{id}/tools", GetMcpToolsAsync)
            .WithName("NhAssistantAdminGetMcpTools")
            .WithSummary("List MCP tools")
            .WithDescription("Returns the synchronized tools of a server.")
            .Produces<NhAssistantMcpToolDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPost("mcp-servers/discover", DiscoverMcpServerAsync)
            .WithName("NhAssistantAdminDiscoverMcpServer")
            .WithSummary("Discover an MCP server's authorization")
            .WithDescription("Reads the protected resource metadata of an MCP server URL and the metadata of its authorization server: the resource, the issuer, the challenged and supported scopes, the grant types, PKCE and the registration options. Nothing is stored; saving the server with oauth.confirmAuthorization pins the authorization server.")
            .Produces<NhAssistantMcpDiscoveryDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status502BadGateway);
        admin.MapGet("mcp-servers/{id}/connection", GetMcpOrganizationConnectionAsync)
            .WithName("NhAssistantAdminGetMcpConnection")
            .WithSummary("Get the organization's OAuth connection")
            .WithDescription("Returns the status of the organization's OAuth connection of the server (oauth or oauth-client-credentials), its scopes and who connected it, without tokens. For a server each person connects to it is the administrator's own connection, the one authorize starts. A server without OAuth answers 409 assistant-mcp-connection-not-supported.")
            .Produces<NhAssistantMcpOrganizationConnectionDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost("mcp-servers/{id}/connection/authorize", AuthorizeMcpOrganizationConnectionAsync)
            .WithName("NhAssistantAdminAuthorizeMcpConnection")
            .WithSummary("Connect the organization's account")
            .WithDescription("Starts the administrator's authorization of the organization connection of an OAuth server: everyone using the server's agents then acts as the connected account. For a server each person connects to, it connects the administrator's own account, which tests and syncs use. Open the returned URL in the administrator's browser; the callback hands a one-time completion code to it, which POST mcp-connections/complete activates. A server without the authorization code flow answers 409 assistant-mcp-connection-not-supported; too many unfinished authorizations 429.")
            .Produces<NhAssistantMcpAuthorizationStartDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status429TooManyRequests)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status502BadGateway)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status503ServiceUnavailable);
        admin.MapDelete("mcp-servers/{id}/connection", DeleteMcpOrganizationConnectionAsync)
            .WithName("NhAssistantAdminDeleteMcpConnection")
            .WithSummary("Disconnect the organization's account")
            .WithDescription("Removes the organization's OAuth connection and revokes its tokens at the authorization server; for a server each person connects to, the administrator's own connection. A server without OAuth answers 409 assistant-mcp-connection-not-supported.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("mcp-servers/{id}/connections/summary", GetMcpPersonalConnectionsSummaryAsync)
            .WithName("NhAssistantAdminGetMcpConnectionsSummary")
            .WithSummary("Count the personal connections")
            .WithDescription("Returns the number of personal OAuth connections of the server per status. Only a server each person connects to has them; another answers 409 assistant-mcp-connection-not-supported.")
            .Produces<NhAssistantMcpConnectionsSummaryDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapDelete("mcp-servers/{id}/connections", RevokeMcpPersonalConnectionsAsync)
            .WithName("NhAssistantAdminRevokeMcpConnections")
            .WithSummary("Revoke all personal connections")
            .WithDescription("Removes every person's OAuth connection of the server and revokes the tokens; each person connects again. Only a server each person connects to has them; another answers 409 assistant-mcp-connection-not-supported and nothing is audited.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPut("mcp-servers/{id}/tools/{remoteName}", UpdateMcpToolAsync)
            .WithName("NhAssistantAdminUpdateMcpTool")
            .WithSummary("Update an MCP tool")
            .WithDescription("Enables or disables a tool, sets its effect and overrides its description.")
            .Produces<NhAssistantMcpToolDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    // ----- Preferences -----

    private static async Task<IResult> GetPreferencesAsync(HttpContext httpContext, NhAssistantPersonalization personalization)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        return Json(ToDto(await personalization.GetPreferencesAsync(actor, httpContext.RequestAborted)), Context.NhAssistantPreferencesDto);
    }

    private static async Task<IResult> PutPreferencesAsync(HttpContext httpContext, NhAssistantPersonalization personalization)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(httpContext, Context.NhAssistantPreferencesDto);
        if (body is null)
        {
            return Validation();
        }
        var saved = await personalization.SavePreferencesAsync(
            actor,
            new NhAssistantPreferences(
                body.Style ?? string.Empty,
                body.AddressForm ?? string.Empty,
                body.ResponseLength ?? string.Empty,
                body.CustomInstructions),
            httpContext.RequestAborted);
        return saved.Success ? Json(ToDto(saved.Data!), Context.NhAssistantPreferencesDto) : Failure(saved);
    }

    // ----- Application context -----

    private static async Task<IResult> GetContextAsync(HttpContext httpContext, NhAssistantPersonalization personalization)
    {
        var context = await personalization.GetApplicationContextAsync(httpContext.RequestAborted);
        return context is null
            ? NhAssistantHttpErrors.Error(NhAssistantAdminErrorCodes.ContextNotFound)
            : Json(ToDto(context), Context.NhAssistantApplicationContextDto);
    }

    private static async Task<IResult> PutContextAsync(HttpContext httpContext, NhAssistantPersonalization personalization)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(httpContext, Context.NhAssistantUpdateContextRequest);
        if (body is null)
        {
            return Validation();
        }
        if (body.ExpectedVersion is not { } expectedVersion)
        {
            return MissingField("expectedVersion");
        }
        var saved = await personalization.UpdateApplicationContextAsync(body.Text, expectedVersion, actor, httpContext.RequestAborted);
        return saved.Success ? Json(ToDto(saved.Data!), Context.NhAssistantApplicationContextDto) : Failure(saved);
    }

    private static async Task<IResult> GetContextVersionsAsync(HttpContext httpContext, NhAssistantPersonalization personalization)
    {
        var versions = await personalization.GetApplicationContextVersionsAsync(httpContext.RequestAborted);
        return Json(
            versions.Select(version => new NhAssistantApplicationContextVersionDto(version.Version, version.Hash, version.UpdatedAt, version.UpdatedBy)).ToArray(),
            Context.NhAssistantApplicationContextVersionDtoArray);
    }

    // ----- Tools -----

    private static async Task<IResult> GetToolsAsync(
        HttpContext httpContext,
        IEnumerable<INhAiToolCatalog> catalogs,
        NhAssistantAdminStore store)
    {
        var entries = new List<NhAssistantToolCatalogEntryDto>();
        foreach (var catalog in catalogs)
        {
            var source = catalog is INhAiAttestedToolCatalog ? "bridge" : "local";
            entries.AddRange(catalog.Descriptors
                .Where(descriptor => (descriptor.Exposure & NhAiToolExposure.Agent) != 0)
                .Select(descriptor => new NhAssistantToolCatalogEntryDto(descriptor.Id, source, EffectName(descriptor.Effect), descriptor.Description)
                {
                    SubjectType = descriptor.Subject?.Type
                }));
        }
        foreach (var (server, _) in await store.ListMcpServersAsync(httpContext.RequestAborted))
        {
            entries.AddRange((await store.ListMcpToolsAsync(server.Id, httpContext.RequestAborted))
                .Select(tool => new NhAssistantToolCatalogEntryDto(
                    tool.LocalId,
                    "mcp",
                    tool.Effect,
                    tool.DescriptionOverride ?? tool.Description)));
        }
        return Json(
            entries.DistinctBy(entry => entry.Id).OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray(),
            Context.NhAssistantToolCatalogEntryDtoArray);
    }

    // ----- Agents -----

    private static async Task<IResult> GetAgentsAsync(HttpContext httpContext, NhAssistantAgentCatalog catalog)
    {
        var agents = await catalog.ListAsync(includeDisabled: true, httpContext.RequestAborted);
        // The hints of every agent in one read of the threads, and their groups in one read.
        var known = await KnownSubjectTypesAsync(httpContext, null);
        var groups = await httpContext.RequestServices.GetRequiredService<AgentRuns.NhAssistantAgentGroups>()
            .MembershipsAsync(agents.Select(agent => agent.Id).ToArray(), httpContext.RequestAborted);
        var dtos = new List<NhAssistantAdminAgentDto>(agents.Count);
        foreach (var agent in agents)
        {
            dtos.Add(await ToDtoAsync(agent, httpContext, known, groups));
        }
        return Json(dtos.ToArray(), Context.NhAssistantAdminAgentDtoArray);
    }

    private static async Task<IResult> CreateAgentAsync(
        HttpContext httpContext,
        NhAssistantAgentAdministration administration,
        IAuthorizationPolicyProvider policies)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(httpContext, Context.NhAssistantAdminAgentInputDto);
        if (body is null)
        {
            return Validation();
        }
        var (input, errors) = await ToInputAsync(body, policies);
        if (input is null)
        {
            return Failure(errors.ToResult());
        }
        var created = await administration.CreateAsync(input, actor, httpContext.RequestAborted);
        if (!created.Success)
        {
            return Failure(created);
        }
        return new CreatedJson<NhAssistantAdminAgentDto>(
            $"{httpContext.Request.PathBase}{httpContext.Request.Path.Value?.TrimEnd('/')}/{created.Data!.Id}",
            await ToDtoAsync(created.Data, httpContext),
            Context.NhAssistantAdminAgentDto);
    }

    private static async Task<IResult> UpdateAgentAsync(
        string id,
        HttpContext httpContext,
        NhAssistantAgentAdministration administration,
        IAuthorizationPolicyProvider policies)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(httpContext, Context.NhAssistantAdminAgentInputDto);
        if (body is null)
        {
            return Validation();
        }
        var (input, errors) = await ToInputAsync(body with { Id = id }, policies);
        if (body.ExpectedVersion is null)
        {
            errors.Add("expectedVersion", NhAssistantFieldErrors.Required);
        }
        if (input is null || body.ExpectedVersion is not { } expectedVersion)
        {
            return Failure(errors.ToResult());
        }
        var updated = await administration.UpdateAsync(input, expectedVersion, actor, httpContext.RequestAborted);
        return updated.Success ? Json(await ToDtoAsync(updated.Data!, httpContext), Context.NhAssistantAdminAgentDto) : Failure(updated);
    }

    private static async Task<IResult> ResetAgentAsync(string id, HttpContext httpContext, NhAssistantAgentAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var reset = await administration.ResetAsync(id, actor, httpContext.RequestAborted);
        return reset.Success ? Json(await ToDtoAsync(reset.Data!, httpContext), Context.NhAssistantAdminAgentDto) : Failure(reset);
    }

    private static async Task<IResult> SetKillSwitchAsync(string id, HttpContext httpContext)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(
            httpContext,
            AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantKillSwitchRequest);
        if (body?.Engaged is not { } engaged)
        {
            return Validation();
        }

        var control = httpContext.RequestServices.GetRequiredService<AgentRuns.INhAssistantAgentControl>();
        var result = await control.SetKillSwitchAsync(id, engaged, actor, httpContext.RequestAborted);
        return result.Success
            ? Json(
                new AgentRuns.NhAssistantKillSwitchDto(
                    id,
                    result.Data!.RunState != NhAssistantAgentRunStates.Stopped,
                    result.Data.CancelledRuns)
                {
                    RunState = result.Data.RunState
                },
                AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantKillSwitchDto)
            : Failure(result);
    }

    private static async Task<IResult> GetAgentStateAsync(string id, HttpContext httpContext)
    {
        var control = httpContext.RequestServices.GetRequiredService<AgentRuns.INhAssistantAgentControl>();
        var state = await control.GetAsync(id, httpContext.RequestAborted);
        return state is null
            ? NhAssistantHttpErrors.Error(NhAssistantErrorCodes.AgentNotFound)
            : Json(state, AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentState);
    }

    private static async Task<IResult> SetSkillStateAsync(string id, string skillId, HttpContext httpContext)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(
            httpContext,
            AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentStateRequest);
        if (body is null)
        {
            return Validation();
        }
        if (body.State is null)
        {
            return MissingField("state");
        }
        if (!NhAssistantAgentRunStates.IsStored(body.State))
        {
            return Failure(NhAssistantValidationErrors.Failed("state", NhAssistantFieldErrors.Invalid));
        }

        var control = httpContext.RequestServices.GetRequiredService<AgentRuns.INhAssistantAgentControl>();
        var result = body.State switch
        {
            NhAssistantAgentRunStates.Paused => await control.PauseSkillAsync(id, skillId, actor, body.Reason, httpContext.RequestAborted),
            NhAssistantAgentRunStates.Off => await control.SwitchOffSkillAsync(id, skillId, actor, body.Reason, httpContext.RequestAborted),
            NhAssistantAgentRunStates.Stopped => await control.StopSkillAsync(id, skillId, actor, body.Reason, httpContext.RequestAborted),
            _ => await control.ResumeSkillAsync(id, skillId, actor, body.Reason, httpContext.RequestAborted)
        };
        return result.Success
            ? Json(result.Data!, AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSkillState)
            : Failure(result);
    }

    private static async Task<IResult> SetSkillShadowAsync(string id, string skillId, HttpContext httpContext)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(
            httpContext,
            AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantShadowRequest);
        if (body is null)
        {
            return Validation();
        }
        if (body.Mode is null)
        {
            return MissingField("mode");
        }
        if (!AgentRuns.NhAssistantShadowChoices.TryParse(body.Mode, out var shadow))
        {
            return Failure(NhAssistantValidationErrors.Failed("mode", NhAssistantFieldErrors.Invalid));
        }

        var control = httpContext.RequestServices.GetRequiredService<AgentRuns.INhAssistantAgentControl>();
        var result = await control.SetSkillShadowAsync(id, skillId, shadow, actor, body.Reason, httpContext.RequestAborted);
        return result.Success
            ? Json(result.Data!, AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSkillState)
            : Failure(result);
    }

    private static async Task<IResult> SetAgentShadowAsync(string id, HttpContext httpContext)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(
            httpContext,
            AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantShadowRequest);
        if (body is null)
        {
            return Validation();
        }
        if (body.Mode is null)
        {
            return MissingField("mode");
        }
        if (!AgentRuns.NhAssistantShadowChoices.TryParse(body.Mode, out var shadow))
        {
            return Failure(NhAssistantValidationErrors.Failed("mode", NhAssistantFieldErrors.Invalid));
        }

        var control = httpContext.RequestServices.GetRequiredService<AgentRuns.INhAssistantAgentControl>();
        var result = await control.SetShadowAsync(id, shadow, actor, body.Reason, httpContext.RequestAborted);
        return result.Success
            ? Json(result.Data!, AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentState)
            : Failure(result);
    }

    private static async Task<IResult> SetAgentServiceAccountAsync(string id, HttpContext httpContext)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(
            httpContext,
            AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantServiceAccountRequest);
        if (body is null)
        {
            return Validation();
        }

        var control = httpContext.RequestServices.GetRequiredService<AgentRuns.INhAssistantAgentControl>();
        var result = await control.SetServiceAccountAsync(id, body.ActorId, actor, body.Reason, httpContext.RequestAborted);
        return result.Success
            ? Json(result.Data!, AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentState)
            : Failure(result);
    }

    private static async Task<IResult> SetAgentStateAsync(string id, HttpContext httpContext)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(
            httpContext,
            AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentStateRequest);
        if (body is null)
        {
            return Validation();
        }
        if (body.State is null)
        {
            return MissingField("state");
        }
        if (!NhAssistantAgentRunStates.IsStored(body.State))
        {
            return Failure(NhAssistantValidationErrors.Failed("state", NhAssistantFieldErrors.Invalid));
        }

        var control = httpContext.RequestServices.GetRequiredService<AgentRuns.INhAssistantAgentControl>();
        var result = body.State switch
        {
            NhAssistantAgentRunStates.Paused => await control.PauseAsync(id, actor, body.Reason, httpContext.RequestAborted),
            NhAssistantAgentRunStates.Off => await control.SwitchOffAsync(id, actor, body.Reason, httpContext.RequestAborted),
            NhAssistantAgentRunStates.Stopped => await control.StopAsync(id, actor, body.Reason, httpContext.RequestAborted),
            _ => await control.ResumeAsync(id, actor, body.Reason, httpContext.RequestAborted)
        };
        return result.Success
            ? Json(result.Data!, AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentState)
            : Failure(result);
    }

    private static async Task<IResult> DeleteAgentAsync(string id, HttpContext httpContext, NhAssistantAgentAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var deleted = await administration.DeleteAsync(id, actor, httpContext.RequestAborted);
        return deleted.Success ? TypedResults.NoContent() : Failure(deleted);
    }

    // ----- MCP servers -----

    private static async Task<IResult> GetMcpServersAsync(HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var servers = await administration.ListAsync(httpContext.RequestAborted);
        var connections = (await httpContext.RequestServices.GetRequiredService<Persistence.NhAssistantMcpOAuthStore>()
                .ListTokenConnectionsAsync(NhAssistantMcpConnectionOwners.Organization, null, httpContext.RequestAborted))
            .ToDictionary(connection => connection.ServerId, StringComparer.Ordinal);
        return Json(
            servers.Select(item => ToDto(item.Server, item.AgentIds, connections.GetValueOrDefault(item.Server.Id))).ToArray(),
            Context.NhAssistantMcpServerDtoArray);
    }

    private static IResult GetMcpAuthModes(NhAssistantMcpAuthModeCatalog modes)
    {
        return Json(
            modes.Registered
                .Select(item => new NhAssistantMcpAuthModeDto(
                    item.Mode,
                    item.Ownership == NhAssistantMcpCredentialOwnership.Person ? "person" : "organization"))
                .ToArray(),
            Context.NhAssistantMcpAuthModeDtoArray);
    }

    private static async Task<IResult> DiscoverMcpServerAsync(HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var body = await ReadAsync(httpContext, Context.NhAssistantMcpDiscoverRequest);
        if (body?.Url is not { } url)
        {
            return MissingField("url");
        }
        var discovered = await administration.DiscoverAsync(url, httpContext.RequestAborted);
        if (!discovered.Success)
        {
            return Failure(discovered);
        }
        var result = discovered.Data!;
        var server = result.AuthorizationServer;
        return Json(new NhAssistantMcpDiscoveryDto(
            result.Resource,
            server.Issuer,
            result.AuthorizationServers,
            result.ChallengedScopes,
            result.ResourceScopes.Union(server.ScopesSupported, StringComparer.Ordinal).ToArray(),
            server.GrantTypes,
            server.SupportsS256,
            server.ClientIdMetadataDocumentSupported,
            server.RegistrationEndpoint is not null,
            server.TokenEndpointAuthMethods), Context.NhAssistantMcpDiscoveryDto);
    }

    private static async Task<IResult> GetMcpOrganizationConnectionAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var found = await administration.GetConnectionAsync(id, actor, httpContext.RequestAborted);
        return found.Success
            ? Json(ToDto(found.Data.Connection), Context.NhAssistantMcpOrganizationConnectionDto)
            : Failure(found);
    }

    private static async Task<IResult> AuthorizeMcpOrganizationConnectionAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = httpContext.Request.HasJsonContentType()
            ? await ReadAsync(httpContext, Context.NhAssistantMcpAuthorizeRequest)
            : new NhAssistantMcpAuthorizeRequest(null, null);
        if (body is null)
        {
            return Validation();
        }
        var started = await administration.AuthorizeOrganizationAsync(id, actor, body.ReturnUrl, httpContext.RequestAborted);
        return started.Success
            ? Json(new NhAssistantMcpAuthorizationStartDto(started.Data!.FlowId, started.Data.AuthorizationUrl, started.Data.ExpiresAt, started.Data.CallbackOrigin), Context.NhAssistantMcpAuthorizationStartDto)
            : Failure(started);
    }

    private static async Task<IResult> DeleteMcpOrganizationConnectionAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var removed = await administration.DisconnectOrganizationAsync(id, actor, httpContext.RequestAborted);
        return removed.Success ? TypedResults.NoContent() : Failure(removed);
    }

    private static async Task<IResult> GetMcpPersonalConnectionsSummaryAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var summary = await administration.SummarizePersonalConnectionsAsync(id, httpContext.RequestAborted);
        if (!summary.Success)
        {
            return Failure(summary);
        }
        var counts = summary.Data!;
        return Json(new NhAssistantMcpConnectionsSummaryDto(
            counts.GetValueOrDefault(NhAssistantMcpConnectionStatuses.Active),
            counts.GetValueOrDefault(NhAssistantMcpConnectionStatuses.ReauthorizationRequired),
            counts.GetValueOrDefault(NhAssistantMcpConnectionStatuses.IssuerChanged)), Context.NhAssistantMcpConnectionsSummaryDto);
    }

    private static async Task<IResult> RevokeMcpPersonalConnectionsAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var revoked = await administration.RevokePersonalConnectionsAsync(id, actor, httpContext.RequestAborted);
        return revoked.Success ? TypedResults.NoContent() : Failure(revoked);
    }

    private static async Task<IResult> CreateMcpServerAsync(HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(httpContext, Context.NhAssistantMcpServerInputDto);
        if (body is null)
        {
            return Validation();
        }
        var (input, errors) = ToInput(body, null);
        if (input is null)
        {
            return Failure(errors.ToResult());
        }
        var created = await administration.CreateAsync(input, actor, httpContext.RequestAborted);
        if (!created.Success)
        {
            return Failure(created);
        }
        return new CreatedJson<NhAssistantMcpServerDto>(
            $"{httpContext.Request.PathBase}{httpContext.Request.Path.Value?.TrimEnd('/')}/{created.Data!.Id}",
            ToDto(created.Data, [], null),
            Context.NhAssistantMcpServerDto);
    }

    private static async Task<IResult> UpdateMcpServerAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(httpContext, Context.NhAssistantMcpServerInputDto);
        if (body is null)
        {
            return Validation();
        }
        var (input, errors) = ToInput(body, id);
        if (input is null)
        {
            return Failure(errors.ToResult());
        }
        var updated = await administration.UpdateAsync(input, actor, httpContext.RequestAborted);
        if (!updated.Success)
        {
            return Failure(updated);
        }
        var assigned = (await administration.ListAsync(httpContext.RequestAborted))
            .FirstOrDefault(item => item.Server.Id == id).AgentIds ?? [];
        var connection = await administration.FindOrganizationConnectionAsync(id, httpContext.RequestAborted);
        return Json(ToDto(updated.Data!, assigned, connection), Context.NhAssistantMcpServerDto);
    }

    private static async Task<IResult> DeleteMcpServerAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var deleted = await administration.DeleteAsync(id, actor, httpContext.RequestAborted);
        return deleted.Success ? TypedResults.NoContent() : Failure(deleted);
    }

    private static async Task<IResult> TestMcpServerAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var result = await administration.TestAsync(id, await ActorAsync(httpContext), httpContext.RequestAborted);
        return result.Success
            ? Json(new NhAssistantMcpServerTestResultDto(result.Data!.Ok, result.Data.Code, result.Data.ToolCount), Context.NhAssistantMcpServerTestResultDto)
            : Failure(result);
    }

    private static async Task<IResult> SyncMcpServerAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var synced = await administration.SyncAsync(id, actor, httpContext.RequestAborted);
        return synced.Success ? Json(synced.Data!.Select(ToDto).ToArray(), Context.NhAssistantMcpToolDtoArray) : Failure(synced);
    }

    private static async Task<IResult> GetMcpToolsAsync(string id, HttpContext httpContext, NhAssistantMcpAdministration administration)
    {
        var tools = await administration.ListToolsAsync(id, httpContext.RequestAborted);
        return tools.Success ? Json(tools.Data!.Select(ToDto).ToArray(), Context.NhAssistantMcpToolDtoArray) : Failure(tools);
    }

    private static async Task<IResult> UpdateMcpToolAsync(
        string id,
        string remoteName,
        HttpContext httpContext,
        NhAssistantMcpAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var body = await ReadAsync(httpContext, Context.NhAssistantUpdateMcpToolRequest);
        if (body is null)
        {
            return Validation();
        }
        if (body.IsEnabled is not { } isEnabled)
        {
            return MissingField("isEnabled");
        }
        var updated = await administration.UpdateToolAsync(
            id,
            remoteName,
            isEnabled,
            body.Effect,
            body.DescriptionOverride,
            actor,
            httpContext.RequestAborted);
        return updated.Success ? Json(ToDto(updated.Data!), Context.NhAssistantMcpToolDto) : Failure(updated);
    }

    // ----- Mapping and helpers -----

    private static NhAssistantJsonSerializerContext Context => NhAssistantJsonSerializerContext.Default;

    private static async Task<(NhAssistantAgentInput? Input, NhAssistantValidationErrors Errors)> ToInputAsync(
        NhAssistantAdminAgentInputDto body,
        IAuthorizationPolicyProvider policies)
    {
        var autonomyValid = Enum.TryParse<NhAiAutonomyLevel>(body.Autonomy, ignoreCase: true, out var autonomy)
            && string.Equals(body.Autonomy, autonomy.ToString(), StringComparison.OrdinalIgnoreCase)
            && !body.Autonomy.Any(char.IsUpper);
        var requiredPolicy = string.IsNullOrWhiteSpace(body.RequiredPolicy) ? null : body.RequiredPolicy;
        var errors = new NhAssistantValidationErrors()
            .Require(body.Id is not null, "id", NhAssistantFieldErrors.Required)
            .Require(body.DisplayName is not null, "displayName", NhAssistantFieldErrors.Required)
            .Require(body.Description is not null, "description", NhAssistantFieldErrors.Required)
            .Require(body.Instructions is not null, "instructions", NhAssistantFieldErrors.Required)
            .Require(body.ToolSelectors is not null, "toolSelectors", NhAssistantFieldErrors.Required)
            .Require(body.Autonomy is not null, "autonomy", NhAssistantFieldErrors.Required)
            .Require(body.Autonomy is null || autonomyValid, "autonomy", NhAssistantFieldErrors.Invalid);
        if (requiredPolicy is not null && await policies.GetPolicyAsync(requiredPolicy) is null)
        {
            errors.Add("requiredPolicy", NhAssistantFieldErrors.NotFound);
        }
        if (!errors.IsEmpty)
        {
            return (null, errors);
        }
        return (new NhAssistantAgentInput(
            body.Id!,
            body.DisplayName!,
            body.Description!,
            body.Instructions!,
            body.ToolSelectors!,
            body.McpServerIds ?? [],
            requiredPolicy,
            autonomy,
            body.OfferedInChat,
            NhAssistantAgentWorksOn.FromInput(
                body.WorksOnSubjectTypes,
                body.WorksOnAllSubjects,
                body.WorksOnApplication,
                body.PerDivision,
                body.RunPolicy,
                body.ViewPolicy),
            body.StartsOff == true,
            string.IsNullOrWhiteSpace(body.Profile) ? null : body.Profile.Trim(),
            string.IsNullOrWhiteSpace(body.DataClassification) ? null : body.DataClassification.Trim()), errors);
    }

    private static (NhAssistantMcpServerInput? Input, NhAssistantValidationErrors Errors) ToInput(
        NhAssistantMcpServerInputDto body,
        string? id)
    {
        var errors = new NhAssistantValidationErrors()
            .Require((id ?? body.Id) is not null, "id", NhAssistantFieldErrors.Required)
            .Require(body.DisplayName is not null, "displayName", NhAssistantFieldErrors.Required)
            .Require(body.Url is not null, "url", NhAssistantFieldErrors.Required)
            .Require(body.AuthMode is not null, "authMode", NhAssistantFieldErrors.Required)
            .Require(body.IsEnabled is not null, "isEnabled", NhAssistantFieldErrors.Required);
        if (!errors.IsEmpty)
        {
            return (null, errors);
        }
        return (new NhAssistantMcpServerInput(
            id ?? body.Id!,
            body.DisplayName!,
            body.Url!,
            body.AuthMode!,
            string.IsNullOrWhiteSpace(body.HeaderName) ? null : body.HeaderName,
            body.Secret,
            string.IsNullOrWhiteSpace(body.RequiredPolicy) ? null : body.RequiredPolicy,
            body.IsEnabled!.Value,
            body.HeadlessUse)
        {
            UserName = body.UserName,
            SecretReference = body.SecretReference,
            ClientCertificateReference = body.ClientCertificateReference,
            Transport = body.Transport,
            ConnectionScope = body.ConnectionScope,
            OAuth = body.OAuth is { } oauth
                ? new NhAssistantMcpOAuthInput(
                    oauth.ClientId,
                    oauth.ClientSecret,
                    oauth.ClientSecretReference,
                    oauth.Scopes,
                    oauth.OrganizationAccountLabel,
                    oauth.ConfirmAuthorization == true)
                : null,
            // A reader sent as null has no fields, so it is refused field by field like an empty one.
            Readers = body.Readers?
                .Select(reader => new NhAssistantMcpReaderInput(
                    reader?.MediaTypes ?? [],
                    reader?.ToolName,
                    reader?.MaxClassification,
                    reader?.Priority ?? 0))
                .ToArray()
        }, errors);
    }

    private static NhAssistantPreferencesDto ToDto(NhAssistantPreferences preferences)
    {
        return new NhAssistantPreferencesDto(preferences.Style, preferences.AddressForm, preferences.ResponseLength, preferences.CustomInstructions);
    }

    private static NhAssistantApplicationContextDto ToDto(AssistantApplicationContext context)
    {
        return new NhAssistantApplicationContextDto(context.Text, context.Version, context.Hash, context.UpdatedAt, context.UpdatedBy);
    }

    private static async Task<NhAssistantAdminAgentDto> ToDtoAsync(
        NhAssistantAgent agent,
        HttpContext httpContext,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? known = null,
        IReadOnlyDictionary<string, AgentRuns.NhAssistantAgentGroupMembershipDto>? groups = null)
    {
        var control = httpContext.RequestServices.GetRequiredService<AgentRuns.NhAssistantAgentControl>();
        known ??= await KnownSubjectTypesAsync(httpContext, [agent.Id]);
        groups ??= await httpContext.RequestServices.GetRequiredService<AgentRuns.NhAssistantAgentGroups>()
            .MembershipsAsync([agent.Id], httpContext.RequestAborted);
        var membership = groups.GetValueOrDefault(agent.Id);
        return ToDto(agent, await control.StateOfAsync(agent, httpContext.RequestAborted)) with
        {
            KnownSubjectTypes = known.GetValueOrDefault(agent.Id) ?? [],
            Groups = membership?.Groups ?? [],
            CodeGroups = membership?.CodeGroups ?? [],
            // A save succeeds also when no profile permits the agent's classification.
            Warnings = NhAssistantClassificationChecks.WarningsOf(
                httpContext.RequestServices.GetService<INhAiModelProfileRegistry>(),
                agent.Definition.ProfileName,
                agent.DataClassification)
        };
    }

    /// <summary>
    /// The object types agents are known to work on, from the run configuration in effect and their
    /// threads.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> KnownSubjectTypesAsync(
        HttpContext httpContext,
        IReadOnlyCollection<string>? agentIds)
    {
        var services = httpContext.RequestServices;
        await services.GetRequiredService<NhAssistantRunConfigurations>().EnsureCurrentAsync(httpContext.RequestAborted);
        return await services.GetRequiredService<NhAssistantAgentKnownSubjectTypes>().ListAsync(agentIds, httpContext.RequestAborted);
    }

    private static NhAssistantAdminAgentDto ToDto(NhAssistantAgent agent, AgentRuns.NhAssistantAgentState state)
    {
        var definition = agent.Definition;
        return new NhAssistantAdminAgentDto(
            agent.Id,
            definition.DisplayNameKey,
            definition.DescriptionKey,
            definition.Instructions.Content,
            definition.ToolSelectors,
            agent.McpServerIds,
            definition.RequiredPolicy,
            definition.Autonomy.ToString().ToLowerInvariant(),
            agent.IsEnabled,
            definition.Version,
            agent.Source,
            agent.IsOverridden,
            definition.Instructions.Manifest.ContentHash,
            agent.UpdatedAt,
            state.RunState,
            state.ChangedAt,
            state.ChangedBy,
            state.Reason,
            state.WaitingInputs)
        {
            Skills = state.Skills,
            ShadowOf = state.ShadowOf,
            Shadow = state.Shadow,
            ServiceAccount = state.ServiceAccount,
            OfferedInChat = agent.OfferedInChat,
            Profile = agent.Definition.ProfileName,
            DataClassification = agent.DataClassification is { } classification
                ? NhAssistantDataClassificationNames.NameOf(classification)
                : null,
            WorksOnSubjectTypes = agent.WorksOn.SubjectTypes,
            WorksOnAllSubjects = agent.WorksOn.AllSubjects,
            WorksOnApplication = agent.WorksOn.Application,
            PerDivision = agent.WorksOn.PerDivision,
            RunPolicy = agent.WorksOn.RunPolicy,
            ViewPolicy = agent.WorksOn.ViewPolicy,
            WorksOnDeclared = agent.WorksOn.Declared
        };
    }

    private static NhAssistantMcpOrganizationConnectionDto ToDto(AssistantMcpConnection? connection)
    {
        return connection is null
            ? new NhAssistantMcpOrganizationConnectionDto("not-connected", null, null, null, null, null, null)
            : new NhAssistantMcpOrganizationConnectionDto(
                connection.Status ?? "not-connected",
                connection.ConnectedAt,
                connection.ConnectedBy,
                connection.GrantedScopes,
                connection.RequiredScopes,
                connection.AccessTokenExpiresAt,
                connection.LastFailureCode);
    }

    private static NhAssistantMcpServerDto ToDto(AssistantMcpServer server, IReadOnlyList<string> agentIds, AssistantMcpConnection? organizationConnection)
    {
        return new NhAssistantMcpServerDto(
            server.Id,
            server.DisplayName,
            server.Url,
            server.AuthMode,
            server.HeaderName,
            server.ProtectedSecret is not null,
            server.RequiredPolicy,
            server.IsEnabled,
            server.LastSyncAt,
            server.LastSyncStatus,
            agentIds)
        {
            HeadlessUse = server.HeadlessUse,
            LastConnectAt = server.LastConnectAt,
            LastConnectCode = server.LastConnectCode,
            UserName = server.UserName,
            SecretReference = server.SecretReference,
            ClientCertificateReference = server.ClientCertificateReference,
            Transport = server.Transport,
            ConnectionScope = server.ConnectionScope,
            OAuth = NhAssistantMcpAuthModes.IsOAuth(server.AuthMode)
                ? new NhAssistantMcpOAuthDto(
                    server.OAuthClientId,
                    server.ProtectedOAuthClientSecret is not null,
                    server.OAuthClientSecretReference,
                    server.OAuthScopes,
                    server.OrganizationAccountLabel)
                : null,
            Authorization = server.AuthorizationServerIssuer is { } issuer && server.ResourceUri is { } resource && server.AuthorizationConfirmedAt is { } confirmedAt
                ? new NhAssistantMcpAuthorizationDto(issuer, resource, confirmedAt, server.AuthorizationConfirmedBy)
                : null,
            OrganizationConnection = NhAssistantMcpAuthModes.IsOrganizationCredential(server.AuthMode, server.ConnectionScope) && NhAssistantMcpAuthModes.IsOAuth(server.AuthMode)
                ? ToDto(organizationConnection)
                : null,
            Readers = server.Readers
                .OrderByDescending(reader => reader.Priority)
                .ThenBy(reader => reader.ToolName, StringComparer.Ordinal)
                .Select(reader => new NhAssistantMcpReaderDto(
                    NhAssistantMcpReaders.Split(reader.MediaTypes),
                    reader.ToolName,
                    reader.MaxClassification,
                    reader.Priority))
                .ToArray()
        };
    }

    private static NhAssistantMcpToolDto ToDto(AssistantMcpTool tool)
    {
        return new NhAssistantMcpToolDto(
            tool.RemoteName,
            tool.LocalId,
            tool.Description,
            tool.DescriptionOverride,
            tool.IsEnabled,
            tool.Effect,
            tool.Status,
            tool.ReadOnlyHint);
    }

    private static string EffectName(NhAiToolEffect effect)
    {
        return effect switch
        {
            NhAiToolEffect.ReadOnly => "read-only",
            NhAiToolEffect.IdempotentMutation => "idempotent-mutation",
            NhAiToolEffect.Destructive => "destructive",
            _ => "mutation"
        };
    }

    internal static async Task<string?> ActorAsync(HttpContext httpContext)
    {
        var resolved = await httpContext.RequestServices
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, httpContext.RequestAborted);
        return resolved.Success ? resolved.Data.ActorId : null;
    }

    internal static async Task<T?> ReadAsync<T>(HttpContext httpContext, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (!httpContext.Request.HasJsonContentType())
        {
            return null;
        }
        try
        {
            return await httpContext.Request.ReadFromJsonAsync(typeInfo, httpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The error response of an expected failure: its code with the one status the assistant gives
    /// it (<see cref="NhAssistantHttpErrors"/>) and its field errors.
    /// </summary>
    internal static IResult Failure(TaskResult result)
    {
        return NhAssistantHttpErrors.Failure(result);
    }

    internal static IResult Validation()
    {
        return NhAssistantHttpErrors.Error(NhAssistantAdminErrorCodes.ValidationFailed);
    }

    internal static IResult MissingField(string field)
    {
        return Failure(NhAssistantValidationErrors.Failed(field, NhAssistantFieldErrors.Required));
    }

    internal static IResult ContextUnavailable()
    {
        return NhAssistantHttpErrors.Error(NhAssistantErrorCodes.ContextUnavailable);
    }

    internal static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return TypedResults.Json(value, typeInfo);
    }

    private sealed class CreatedJson<T>(string location, T value, JsonTypeInfo<T> typeInfo) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            httpContext.Response.Headers.Location = location;
            return httpContext.Response.WriteAsJsonAsync(value, typeInfo, cancellationToken: httpContext.RequestAborted);
        }
    }
}
