using NewHeap.Platform.AI.AspNet.Mvc;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Publishes the project and project-task controllers as governed AI tools through the
/// NewHeap API bridge. Every tool runs as the signed-in user through this API's own HTTP
/// pipeline, so the controllers' <c>[Authorize(Policy = ...)]</c> attributes stay the
/// authorization boundary. Reads are discoverable with <c>app.project.view</c>; creates and
/// updates additionally need <c>app.project.manage</c> and always require approval and an
/// idempotency key. DELETE actions are never published. The curated <c>projects.*</c> tools
/// keep their own discovery policy as the inner policy. The gateway publishes four read-only
/// tools (search, describe, query and get) over the read-only project resources. Canonical
/// NewHeap collection metadata is derived by the bridge from the documented result model.
/// Gateway results are compacted by default, can be projected with <c>fields</c> or counted
/// with <c>countOnly</c>, and never contain contact details: e-mail and phone fields are
/// redacted at every depth before the result is bounded.
/// </summary>
public static class SampleAiBridgeComposition
{
    public const string ToolSetId = "sample-api";
    public const string GatewayToolSetId = "sample-api-gateway";
    public const string SelfBaseUrlKey = "NewHeap:PlatformAspNetCommon:Settings:SelfBaseUrl";

    public static IServiceCollection AddSampleAiBridge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNewHeapPlatformAIMvcBridge(bridge => bridge
            .UseToolSetId(ToolSetId)
            .UseSelfBaseUrl(provider => provider.GetRequiredService<IConfiguration>()[SelfBaseUrlKey])
            .IncludeControllers("Project", "ProjectTask")
            .RequireExplicitPolicy(true)
            .EnableMcpExposure()
            .UseInnerDiscoveryPolicy<ProjectAiToolDiscoveryPolicy>()
            .EnableGateway(gateway => gateway
                .UseGatewayToolSetId(GatewayToolSetId)
                .IncludeReadOnlyOnly()
                .RedactResultFields("*email*", "*phoneNumber*")
                .UseLocalizedResourcePresentation<SampleAiBridgeResources>(presentation =>
                {
                    presentation.RequireLocalizedValues = false;
                    presentation.Add(
                        "project",
                        "ProjectTitle",
                        "ProjectSummary",
                        "Projects",
                        "Search and inspect projects available to the signed-in user.")
                    .Add(
                        "project-task",
                        "ProjectTaskTitle",
                        "ProjectTaskSummary",
                        "Project tasks",
                        "Search and inspect tasks within authorized projects.");
                }))
            .WithToolDefaults(defaults =>
            {
                defaults.MaxResultBytes = 65_536;
                defaults.TimeoutSeconds = 30;
                defaults.MaxInputBytes = 16_384;
            }));
        return services;
    }
}
