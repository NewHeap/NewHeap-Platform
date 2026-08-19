using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSampleProjectManagementCore(this IServiceCollection services)
    {
        services.AddScopedNhDbRepository<Project>();
        services.AddScoped<ProjectService>();
        services.AddScopedNhDbRepository<ProjectTask>();
        services.AddScoped<ProjectTaskService>();
        services.AddScoped<ProjectCompositeService>();
        services.AddScoped<ProjectCollectionSampleService>();
        services.AddScoped<ProjectSetupService>();
        services.AddScoped<ProjectAuthorizationSampleService>();

        return services;
    }
}
