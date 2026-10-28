using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-362: the planning service, a remote MCP server whose <c>next_milestone</c> tool the API offers
/// through its own code-owned tool <see cref="ProjectPlanningMilestoneTools"/> (<c>projects.next-milestone</c>)
/// instead of passing an imported tool on (FB31). The application reviews the one remote tool it calls,
/// connects with its own address and credential from <see cref="SectionName"/> and passes on a bounded
/// answer; the tool has the application's own contract, policy, capability and exposure, so MCP clients
/// of this API see it as one of the API's tools. Without the section the tool fails closed with
/// <c>planning-not-configured</c>; there is no development default.
/// </summary>
public static class SamplePlanningMilestonesComposition
{
    /// <summary>
    /// The configuration section of the planning service: <c>Url</c>, its MCP endpoint, and
    /// <c>ApiKeyCredential</c>, the key it expects in <c>X-Api-Key</c>, from the secrets.
    /// </summary>
    public const string SectionName = "SampleProjectManagement:PlanningService";

    public static IServiceCollection AddSamplePlanningMilestones(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<SamplePlanningServiceOptions>().Bind(configuration.GetSection(SectionName));
        services.TryAddScoped<IProjectPlanningMilestones, SampleMcpPlanningMilestones>();
        services.TryAddScoped<ProjectPlanningMilestoneTools>();
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ProjectPlanningMilestoneToolsNhAiCatalog>());
        return services;
    }
}

/// <summary>
/// SPM-362: where the planning service is and the key it expects.
/// </summary>
public sealed class SamplePlanningServiceOptions
{
    /// <summary>The absolute address of the planning service's MCP endpoint.</summary>
    public string? Url { get; set; }

    /// <summary>The key the planning service expects in <c>X-Api-Key</c>; kept in the secrets.</summary>
    public string? ApiKeyCredential { get; set; }
}

/// <summary>
/// SPM-362: calls the one tool of the planning service the application reviewed, with fixed arguments,
/// over Streamable HTTP. Nothing here passes a caller's token or arguments on, and nothing logs the
/// answer.
/// </summary>
public sealed class SampleMcpPlanningMilestones(
    IOptions<SamplePlanningServiceOptions> options,
    ILogger<SampleMcpPlanningMilestones> logger) : IProjectPlanningMilestones
{
    /// <summary>The one tool of the planning service the application calls.</summary>
    public const string RemoteToolName = "next_milestone";

    public async Task<TaskResult<string>> NextMilestoneAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        var settings = options.Value;
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback))
        {
            return TaskResult<string>.Failed(ProjectPlanningMilestoneTools.NotConfiguredCode, "The application has no planning service.");
        }

        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(settings.ApiKeyCredential))
            {
                headers["X-Api-Key"] = settings.ApiKeyCredential;
            }

            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = headers
                }),
                cancellationToken: cancellationToken);
            var result = await client.CallToolAsync(
                RemoteToolName,
                new Dictionary<string, object?> { ["project"] = projectKey },
                cancellationToken: cancellationToken);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            if (result.IsError == true || string.IsNullOrWhiteSpace(text))
            {
                return TaskResult<string>.Failed(ProjectPlanningMilestoneTools.UnavailableCode, "The planning service did not answer.");
            }

            return TaskResult<string>.Succeeded(text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("The planning service did not answer ({ExceptionType}).", exception.GetType().Name);
            return TaskResult<string>.Failed(ProjectPlanningMilestoneTools.UnavailableCode, "The planning service did not answer.");
        }
    }
}
