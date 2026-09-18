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
                : Error(StatusCodes.Status403Forbidden, NhAssistantAdminErrorCodes.Forbidden);
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
            .WithDescription("Returns code and administrator agents, including disabled agents.")
            .Produces<NhAssistantAdminAgentDto[]>();
        admin.MapPost("agents", CreateAgentAsync)
            .WithName("NhAssistantAdminCreateAgent")
            .WithSummary("Create an agent")
            .WithDescription("Creates an administrator agent.")
            .Produces<NhAssistantAdminAgentDto>(StatusCodes.Status201Created)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPut("agents/{id}", UpdateAgentAsync)
            .WithName("NhAssistantAdminUpdateAgent")
            .WithSummary("Update an agent")
            .WithDescription("Updates or overrides an agent when expectedVersion is current; otherwise 409.")
            .Produces<NhAssistantAdminAgentDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost("agents/{id}/reset", ResetAgentAsync)
            .WithName("NhAssistantAdminResetAgent")
            .WithSummary("Reset a code agent")
            .WithDescription("Restores a code agent to its code definition and removes its MCP assignments.")
            .Produces<NhAssistantAdminAgentDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
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
        admin.MapPost("mcp-servers", CreateMcpServerAsync)
            .WithName("NhAssistantAdminCreateMcpServer")
            .WithSummary("Connect an MCP server")
            .WithDescription("Stores an MCP server; the secret is protected and never returned.")
            .Produces<NhAssistantMcpServerDto>(StatusCodes.Status201Created)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPut("mcp-servers/{id}", UpdateMcpServerAsync)
            .WithName("NhAssistantAdminUpdateMcpServer")
            .WithSummary("Update an MCP server")
            .WithDescription("Updates a server. An omitted secret is kept and an empty secret is cleared.")
            .Produces<NhAssistantMcpServerDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapDelete("mcp-servers/{id}", DeleteMcpServerAsync)
            .WithName("NhAssistantAdminDeleteMcpServer")
            .WithSummary("Delete an MCP server")
            .WithDescription("Deletes a server, its tools and its agent assignments.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPost("mcp-servers/{id}/test", TestMcpServerAsync)
            .WithName("NhAssistantAdminTestMcpServer")
            .WithSummary("Test an MCP server")
            .WithDescription("Connects and lists the remote tools without storing them.")
            .Produces<NhAssistantMcpServerTestResultDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPost("mcp-servers/{id}/sync", SyncMcpServerAsync)
            .WithName("NhAssistantAdminSyncMcpServer")
            .WithSummary("Synchronize MCP tools")
            .WithDescription("Stores the remote tools. New tools start disabled; changed schemas disable a tool.")
            .Produces<NhAssistantMcpToolDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status502BadGateway);
        admin.MapGet("mcp-servers/{id}/tools", GetMcpToolsAsync)
            .WithName("NhAssistantAdminGetMcpTools")
            .WithSummary("List MCP tools")
            .WithDescription("Returns the synchronized tools of a server.")
            .Produces<NhAssistantMcpToolDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
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
            ? Error(StatusCodes.Status404NotFound, NhAssistantAdminErrorCodes.ContextNotFound)
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
                .Select(descriptor => new NhAssistantToolCatalogEntryDto(descriptor.Id, source, EffectName(descriptor.Effect), descriptor.Description)));
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
        return Json(agents.Select(ToDto).ToArray(), Context.NhAssistantAdminAgentDtoArray);
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
            ToDto(created.Data),
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
        return updated.Success ? Json(ToDto(updated.Data!), Context.NhAssistantAdminAgentDto) : Failure(updated);
    }

    private static async Task<IResult> ResetAgentAsync(string id, HttpContext httpContext, NhAssistantAgentAdministration administration)
    {
        var actor = await ActorAsync(httpContext);
        if (actor is null)
        {
            return ContextUnavailable();
        }
        var reset = await administration.ResetAsync(id, actor, httpContext.RequestAborted);
        return reset.Success ? Json(ToDto(reset.Data!), Context.NhAssistantAdminAgentDto) : Failure(reset);
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
        return Json(servers.Select(item => ToDto(item.Server, item.AgentIds)).ToArray(), Context.NhAssistantMcpServerDtoArray);
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
            ToDto(created.Data, []),
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
        return Json(ToDto(updated.Data!, assigned), Context.NhAssistantMcpServerDto);
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
        var result = await administration.TestAsync(id, httpContext.RequestAborted);
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
            .Require(body.IsEnabled is not null, "isEnabled", NhAssistantFieldErrors.Required)
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
            body.IsEnabled!.Value), errors);
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
            body.IsEnabled!.Value), errors);
    }

    private static NhAssistantPreferencesDto ToDto(NhAssistantPreferences preferences)
    {
        return new NhAssistantPreferencesDto(preferences.Style, preferences.AddressForm, preferences.ResponseLength, preferences.CustomInstructions);
    }

    private static NhAssistantApplicationContextDto ToDto(AssistantApplicationContext context)
    {
        return new NhAssistantApplicationContextDto(context.Text, context.Version, context.Hash, context.UpdatedAt, context.UpdatedBy);
    }

    private static NhAssistantAdminAgentDto ToDto(NhAssistantAgent agent)
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
            agent.UpdatedAt);
    }

    private static NhAssistantMcpServerDto ToDto(AssistantMcpServer server, IReadOnlyList<string> agentIds)
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
            agentIds);
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

    private static async Task<string?> ActorAsync(HttpContext httpContext)
    {
        var resolved = await httpContext.RequestServices
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, httpContext.RequestAborted);
        return resolved.Success ? resolved.Data.ActorId : null;
    }

    private static async Task<T?> ReadAsync<T>(HttpContext httpContext, JsonTypeInfo<T> typeInfo)
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

    private static IResult Failure(TaskResult result)
    {
        var items = result.GetResultItems();
        var code = items
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)
                && !name.StartsWith(NhAssistantValidationErrors.FieldPrefix, StringComparison.Ordinal))
            ?? NhAssistantAdminErrorCodes.ValidationFailed;
        var fieldErrors = items
            .Where(item => item.Name.StartsWith(NhAssistantValidationErrors.FieldPrefix, StringComparison.Ordinal))
            .GroupBy(item => item.Name[NhAssistantValidationErrors.FieldPrefix.Length..], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(item => item.ErrorMessages)
                    .Select(message => message.ToString())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var status = code switch
        {
            NhAssistantErrorCodes.AgentNotFound
                or NhAssistantAdminErrorCodes.NotFound
                or NhAssistantAdminErrorCodes.ContextNotFound
                or NhAssistantAdminErrorCodes.McpServerNotFound
                or NhAssistantAdminErrorCodes.McpToolNotFound => StatusCodes.Status404NotFound,
            NhAssistantAdminErrorCodes.VersionConflict
                or NhAssistantAdminErrorCodes.AgentExists
                or NhAssistantAdminErrorCodes.McpServerExists
                or NhAssistantAdminErrorCodes.CodeAgentNotDeletable
                or NhAssistantAdminErrorCodes.NotCodeAgent => StatusCodes.Status409Conflict,
            NhAssistantAdminErrorCodes.McpUnreachable
                or NhAssistantAdminErrorCodes.McpUnauthorized => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status400BadRequest
        };
        return Error(status, code, fieldErrors.Count == 0 ? null : fieldErrors);
    }

    private static IResult Validation()
    {
        return Error(StatusCodes.Status400BadRequest, NhAssistantAdminErrorCodes.ValidationFailed);
    }

    private static IResult MissingField(string field)
    {
        return Failure(NhAssistantValidationErrors.Failed(field, NhAssistantFieldErrors.Required));
    }

    private static IResult ContextUnavailable()
    {
        return Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.ContextUnavailable);
    }

    private static IResult Error(int statusCode, string code, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        return TypedResults.Json(
            new NhAssistantErrorDto(code, NhAssistantErrorCodes.MessageKey(code), errors),
            Context.NhAssistantErrorDto,
            statusCode: statusCode);
    }

    private static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo)
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
