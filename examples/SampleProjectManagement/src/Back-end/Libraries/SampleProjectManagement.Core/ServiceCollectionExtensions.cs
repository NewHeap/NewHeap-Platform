using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AspNet.Common;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.Core.Utilities;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSampleProjectManagementCore(this IServiceCollection services)
    {
        services.AddScopedNhDbRepository<Project>();
        services.AddScoped<ProjectService>();
        services.AddScoped<IProjectAiReadService>(provider => provider.GetRequiredService<ProjectService>());
        services.AddScoped<IProjectAiMutationService>(provider => provider.GetRequiredService<ProjectService>());
        services.AddScoped<IProjectAiContextService>(provider => provider.GetRequiredService<ProjectService>());
        services.AddScoped<ProjectAiTools>();
        services.AddScopedNhDbRepository<ProjectTask>();
        services.AddScoped<ProjectTaskService>();
        services.AddScoped<ProjectCompositeService>();
        services.AddScoped<ProjectCollectionSampleService>();
        services.AddScoped<ProjectSetupService>();
        services.AddScoped<ProjectAuthorizationSampleService>();
        services.AddScoped<ProjectMappingLabelFormatter>();
        services.AddScoped<ProjectDisplayNameResolver>();
        services.AddScoped<ProjectReferenceConverter>();
        services.AddScoped<ProjectMappingEnrichmentAction>();

        return services;
    }

    public static IServiceCollection AddSampleProjectManagementAi(this IServiceCollection services)
    {
        services.TryAddKeyedSingleton<IChatClient, ProjectAiSampleChatClient>(
            "project-assistant-model");
        services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("project-assistant", profile => profile
                .UseKeyedClient("project-assistant-model")
                .RequireCapabilities(
                    NhAiModelCapability.FunctionCalling,
                    NhAiModelCapability.StructuredOutput)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local")
                .WithBudget(
                    maxInputTokens: 4_096,
                    maxOutputTokens: 1_024,
                    maxCalls: 8)
                .WithTimeout(TimeSpan.FromSeconds(30))
                .WithEvaluationBaseline("sample-project-assistant-v1"));
            ai.RequireProfile(
                "project-assistant",
                NhAiModelCapability.FunctionCalling,
                NhAiModelCapability.StructuredOutput);
            ai.UseDiscoveryPolicy<ProjectAiToolDiscoveryPolicy>();
            ai.UseIdempotencyManager<ProjectAiInMemoryIdempotencyManager>(
                ServiceLifetime.Singleton);
            ai.UseBudgetManager<ProjectAiInMemoryBudgetManager>(
                ServiceLifetime.Singleton);
            ai.AddVerifier<ProjectAiStatusVerifier>();
            ai.AddContextSource<ProjectAiContextSource>();
            ai.UseContextAuthorizationPolicy<ProjectAiContextAuthorizationPolicy>();
            ai.AddGeneratedToolCatalog<ProjectAiToolsNhAiCatalog>();
        });

        return services;
    }
}
