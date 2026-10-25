using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for the execution region of the assistant (SPM-323): every profile the sample
/// assistant uses permits the region it runs in, so it starts; a host that moves the assistant to a
/// region its chat profile does not permit, itself or through its fallback, does not start, and the
/// error names the profile and the region instead of every model call failing later.
/// </summary>
public sealed class AssistantRegionSamplesTests(SamplePostgreSqlServer postgreSql)
{
    [Fact]
    public async Task SPM_323_an_assistant_whose_profiles_do_not_run_in_its_region_does_not_start()
    {
        var host = new RegionSampleHost(postgreSql, "eu-west");
        try
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.InitializeAsync().AsTask());

            Assert.StartsWith("The assistant chat profile", refused.Message, StringComparison.Ordinal);
            Assert.Contains($"'{SampleAssistantComposition.ProfileName}'", refused.Message, StringComparison.Ordinal);
            Assert.Contains("execution region 'eu-west'", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// The sample assistant, moved to a region by a later <c>UseExecutionRegion</c>, as a host for a real
    /// model in that region would be without profiles that permit it.
    /// </summary>
    private sealed class RegionSampleHost(SamplePostgreSqlServer postgreSql, string region) : AssistantSampleHost(postgreSql)
    {
        protected override void ConfigureAssistant(IServiceCollection services)
        {
            services.AddNewHeapAssistant(assistant => assistant.UseExecutionRegion(region));
        }
    }
}
