using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-375: the <c>projects.due-by</c> tool (<see cref="ProjectDeadlineAiTools"/>), whose input has a due
/// date and a time of day. The project assistant selects it with <c>projects.*</c>, so it is offered next to
/// the other project tools and to the assistant's scripts, which pass the date as <c>"2026-11-01"</c> and
/// the time as <c>"12:00"</c>.
/// </summary>
public static class SampleProjectDeadlinesComposition
{
    public static IServiceCollection AddSampleProjectDeadlines(this IServiceCollection services)
    {
        services.TryAddScoped<ProjectDeadlineAiTools>();
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ProjectDeadlineAiToolsNhAiCatalog>());
        return services;
    }
}
