using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>The files tools saved in a conversation, with the conversation's live access rules.</summary>
internal static class NhAssistantArtifactEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("conversations/{id:guid}/artifacts", ListAsync)
            .WithName("NhAssistantListConversationArtifacts")
            .WithSummary("List the files of a conversation")
            .WithDescription("Returns up to 200 saved file versions, newest first, without content. Requires the same current access as reading the conversation, including its subject. Restricted agent-thread files remain shielded.")
            .Produces<NhAssistantArtifactDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapGet("conversations/{id:guid}/artifacts/{artifactId:guid}/content", DownloadAsync)
            .WithName("NhAssistantGetConversationArtifactContent")
            .WithSummary("Download a file of a conversation")
            .WithDescription("Downloads one saved file version as an attachment. Rechecks current conversation and subject access and any restricted agent result; no public download URL is issued.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(
        Guid id, HttpContext httpContext, NhAssistantArtifactStore artifacts, INhAssistantStore store)
    {
        var resolved = await NhAssistantEndpointRouteBuilderExtensions.ResolveAccessAsync(httpContext, id);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        return await ListContentAsync(id, httpContext, artifacts, store, Shield(httpContext));
    }

    private static async Task<IResult> DownloadAsync(
        Guid id, Guid artifactId, HttpContext httpContext, NhAssistantArtifactStore artifacts, INhAssistantStore store)
    {
        var resolved = await NhAssistantEndpointRouteBuilderExtensions.ResolveAccessAsync(httpContext, id);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        return await DownloadContentAsync(id, artifactId, httpContext, artifacts, store, Shield(httpContext));
    }

    private static NhAssistantRestrictedResults? Shield(HttpContext context)
    {
        return NhAssistantEndpointRouteBuilderExtensions.ThreadAccessOf(context) is { } access
            ? NhAssistantRestrictedResults.For(context, access)
            : null;
    }

    internal static async Task<IResult> ListContentAsync(
        Guid id, HttpContext httpContext, NhAssistantArtifactStore artifacts, INhAssistantStore store,
        NhAssistantRestrictedResults? shield)
    {
        var rows = await artifacts.ListAsync(id, 200, httpContext.RequestAborted);
        var items = new List<NhAssistantArtifactDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(new NhAssistantArtifactDto(
                row.Id, row.Name, row.Version, row.ContentType, row.SizeBytes, row.CreatedAt, row.RunId,
                shield is not null && !await NhAssistantAgentRunEndpoints.MayReadArtifactAsync(
                    shield, store, row.ToolInvocationId, httpContext.RequestAborted)));
        }
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(items.ToArray(), NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantArtifactDtoArray);
    }

    internal static async Task<IResult> DownloadContentAsync(
        Guid id, Guid artifactId, HttpContext httpContext, NhAssistantArtifactStore artifacts, INhAssistantStore store,
        NhAssistantRestrictedResults? shield)
    {
        var artifact = await artifacts.FindAsync(id, artifactId, httpContext.RequestAborted);
        if (artifact is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.ArtifactNotFound);
        }
        if (shield is not null && !await NhAssistantAgentRunEndpoints.MayReadArtifactAsync(
                shield, store, artifact.ToolInvocationId, httpContext.RequestAborted))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.SubjectAccessDenied);
        }
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return TypedResults.File(artifact.Content, artifact.ContentType, artifact.Name);
    }
}
