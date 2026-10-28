using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The agent package endpoints for administrators (PK12): export, preview, import and unlink, and the
/// reads of agents' parts and of an installed package without an export or audit (FB34).
/// </summary>
internal static class NhAssistantAgentPackageEndpoints
{
    /// <summary>
    /// The largest request body of a preview or an import: the package and its options.
    /// </summary>
    private const long MaxRequestBytes = NhAssistantAgentPackageFormat.MaxBytes + 65_536;

    /// <summary>
    /// Maps the endpoints into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapPost(Relative(NhAssistantAgentPackageRoutes.Export), ExportAsync)
            .WithName("NhAssistantAdminExportAgentPackage")
            .WithSummary("Export agents as an agent package")
            .WithDescription("Exports exactly the agents in agentIds (1 to 20) as one versioned package (format newheap.assistant.agent-package, formatVersion 1) and answers with the JSON download agent-package-{packageId}.json: agents created in the administration whole, agents in code as an overlay of their data skills, triggers, consultations, hand-offs, editable metadata and definition overrides. It never holds history, candidates, runs, run state, shadow overrides, MCP servers, the technical account or secrets. options sets the package id (by default the linked package's, otherwise the first agent's id), the version (by default the next after the linked package's, otherwise 1) and the locked fields per agent and part (* or fields of the part's kind). References to agents outside the selection (hand-off sources, the live agent of a shadow variant, consultation askers and shadow variants) are listed in the response header X-NewHeap-Package-Warnings as a JSON array, which a host serving the administration from another origin exposes in its CORS policy. An unknown agent answers 404, invalid options 400 with field errors, a package above 1 MB 413. Audited as AdminAgentPackageExported.")
            .Produces<NhAssistantAgentPackage>(StatusCodes.Status200OK, "application/json")
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status413PayloadTooLarge);
        admin.MapPost(Relative(NhAssistantAgentPackageRoutes.Preview), PreviewAsync)
            .WithName("NhAssistantAdminPreviewAgentPackage")
            .WithSummary("Preview the import of an agent package")
            .WithDescription("Reads the package strictly and answers per agent and part what an import would do: created, changed, unchanged, kept-local (unlinked by an administrator), left-behind (no longer in the package) or refused with the save validators' field errors, checked once against the host as it would be after the import, so references between the package's agents resolve. Changes nothing. options names the technical account per agent (serviceAccounts), apply (live or shadow) and the unlinked parts to take the package's version again (relink). An unknown property, another format or a newer formatVersion answers 400 assistant-package-format-unsupported, a package above 1 MB or with more than 20 agents 413 assistant-package-too-large, invalid options 400.")
            .Produces<NhAssistantAgentPackagePreviewDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status413PayloadTooLarge);
        admin.MapPost(Relative(NhAssistantAgentPackageRoutes.Import), ImportAsync)
            .WithName("NhAssistantAdminImportAgentPackage")
            .WithSummary("Import an agent package")
            .WithDescription("Imports the package as the preview shows it, in one database transaction with one change of the run configuration: every agent and part, or nothing. An agent whose id is free is created; one linked to the package is upgraded; any other agent with the id refuses the package (part outcome assistant-agent-exists). New skills and triggers start in shadow; with apply shadow a changed skill or trigger becomes the candidate next to the live version. A refused part answers 409 assistant-package-refused with the field error parts invalid, and the preview names each refused part with its field errors; a concurrent change 409 assistant-version-conflict; the format and size answer as for the preview. Every part is audited as its save, then the package as AdminAgentPackageImported.")
            .Produces<NhAssistantAgentPackageImportDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status413PayloadTooLarge);
        admin.MapPost(Relative(NhAssistantAgentPackageRoutes.Unlink), UnlinkAsync)
            .WithName("NhAssistantAdminUnlinkAgentPackage")
            .WithSummary("Unlink an agent or a part from its agent package")
            .WithDescription("Takes one part of the agent (part, such as skill:triage or agent), or the whole agent without part, out of its package: its locks no longer hold and the next upgrade keeps it as kept-local unless the import relinks it. An unknown agent answers 404 assistant-agent-not-found, an agent or part without a link 404 assistant-package-not-linked, an unknown part key or a reason above 500 characters 400. Audited as AdminAgentPackageUnlinked.")
            .Produces<NhAssistantAgentPackageUnlinkDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost(Relative(NhAssistantAgentPackageRoutes.Read), ReadPartsAsync)
            .WithName("NhAssistantAdminReadAgentPackageParts")
            .WithSummary("Read agents' parts as a package holds them, without exporting")
            .WithDescription("Answers exactly the agents in agentIds (1 to 20), in the same order, as an export would put them in a package: every part (definition, run settings, skills, triggers, consultations, hand-offs, metadata and groups) and its SHA-256 per part key in hashes, built the same way, without locks, which only an export's options set. Compare them with a shipped package's parts and hashes, or with the hashes of the parts' links in GET admin/agent-packages/{packageId}, to see which parts a host has otherwise. Agents of different packages may be read together. Nothing is exported, audited or changed. A missing, empty or malformed agentIds answers 400 with the field error agentIds (required, too-long above 20, invalid for a malformed id or an id named twice), an unknown agent 404 assistant-agent-not-found, a caller without the administration policy 403 assistant-forbidden.")
            .Produces<NhAssistantAgentPackageReadDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapGet(Relative(NhAssistantAgentPackageRoutes.Package), GetPackageAsync)
            .WithName("NhAssistantAdminGetAgentPackage")
            .WithSummary("Get a package this host imported")
            .WithDescription("Answers the package this host imported as packageId: its version, format version and hash, the agents that belong to it, when and by whom it was imported, and the current link of every part still linked to it per agent, each with its locked fields, its hash at import and whether it changed locally since. The package stays known after its agents were unlinked. Reads only; nothing is audited. A package the host never imported, or an id that is not a package id (lowercase dash-case, at most 128 characters), answers 404 assistant-package-not-found, a caller without the administration policy 403 assistant-forbidden.")
            .Produces<NhAssistantInstalledAgentPackageDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ReadPartsAsync(HttpContext httpContext, INhAssistantAgentPackages packages)
    {
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantAgentPackageReadRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        // The builders of an export, without its locks and without its audit (FB34).
        var result = await packages.ReadAsync(body.AgentIds ?? [], httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(
                new NhAssistantAgentPackageReadDto(result.Data!),
                NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantAgentPackageReadDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> GetPackageAsync(string packageId, HttpContext httpContext, INhAssistantAgentPackages packages)
    {
        var package = await packages.GetPackageAsync(packageId, httpContext.RequestAborted);
        return package is null
            ? NhAssistantHttpErrors.Error(NhAssistantErrorCodes.PackageNotFound)
            : NhAssistantAdminEndpoints.Json(package, NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantInstalledAgentPackageDto);
    }

    private static async Task<IResult> ExportAsync(HttpContext httpContext, INhAssistantAgentPackages packages)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantAgentPackageExportRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await packages.ExportAsync(body.AgentIds ?? [], body.Options ?? new NhAssistantAgentPackageExportOptions(), actor, httpContext.RequestAborted);
        if (!result.Success)
        {
            return NhAssistantAdminEndpoints.Failure(result);
        }

        var export = result.Data!;
        httpContext.Response.Headers[NhAssistantAgentPackageRoutes.WarningsHeader] = JsonSerializer.Serialize(
            export.Warnings.ToArray(),
            NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantAgentPackageWarningDtoArray);
        return TypedResults.File(
            Encoding.UTF8.GetBytes(NhAssistantAgentPackageFormat.Write(export.Package)),
            "application/json",
            "agent-package-" + export.Package.Package.Id + ".json");
    }

    private static async Task<IResult> PreviewAsync(HttpContext httpContext, INhAssistantAgentPackages packages)
    {
        var (failure, package, options) = await ReadPackageAsync(httpContext);
        if (failure is not null)
        {
            return failure;
        }

        var result = await packages.PreviewImportAsync(package!, options!, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantAgentPackagePreviewDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> ImportAsync(HttpContext httpContext, INhAssistantAgentPackages packages)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var (failure, package, options) = await ReadPackageAsync(httpContext);
        if (failure is not null)
        {
            return failure;
        }

        var result = await packages.ImportAsync(package!, options!, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantAgentPackageImportDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> UnlinkAsync(string id, HttpContext httpContext, INhAssistantAgentPackages packages)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }

        // Without a body the whole agent is unlinked.
        var body = httpContext.Request.HasJsonContentType()
            ? await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantAgentPackageUnlinkRequest)
            : new NhAssistantAgentPackageUnlinkRequest();
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await packages.UnlinkAsync(id, body.Part, actor, body.Reason, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantAgentPackageUnlinkDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    /// <summary>
    /// The package of a preview or an import, read strictly, and its options.
    /// </summary>
    private static async Task<(IResult? Failure, NhAssistantAgentPackage? Package, NhAssistantAgentPackageImportOptions? Options)> ReadPackageAsync(HttpContext httpContext)
    {
        if (httpContext.Request.ContentLength > MaxRequestBytes)
        {
            return (NhAssistantHttpErrors.Error(NhAssistantErrorCodes.PackageTooLarge), null, null);
        }

        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantAgentPackageJsonSerializerContext.Default.NhAssistantAgentPackageImportRequest);
        if (body is null)
        {
            return (NhAssistantAdminEndpoints.Validation(), null, null);
        }
        if (body.Package is not { } json)
        {
            return (NhAssistantAdminEndpoints.MissingField("package"), null, null);
        }

        var read = NhAssistantAgentPackageFormat.Read(json);
        return read.Success
            ? (null, read.Data, body.Options ?? new NhAssistantAgentPackageImportOptions())
            : (NhAssistantAdminEndpoints.Failure(read), null, null);
    }

    private static string Relative(string route)
    {
        return route["admin/".Length..];
    }
}
