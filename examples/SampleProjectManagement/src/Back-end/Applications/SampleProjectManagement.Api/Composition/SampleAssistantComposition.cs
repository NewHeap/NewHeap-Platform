namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Composes the assistant sample. Scaffold placeholder: the assistant back-end lane replaces the bodies.
/// </summary>
public static class SampleAssistantComposition
{
    public static IServiceCollection AddSampleAssistant(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapSampleAssistant(this IEndpointRouteBuilder endpoints)
    {
        return endpoints;
    }
}
