using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Which skills and agents name a text asset (LS30): <see cref="INhAssistantInstructionAssetUsage"/>
/// lists every version of the data skills and the agents created in the administration that name an
/// asset of the application, with the version and hash each one uses, and a new version of the asset
/// never takes effect by itself; on both relational providers.
/// </summary>
public sealed class AssistantInstructionAssetUsageTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string FollowUp = "follow-up";
    private const string Follower = "project-follower";
    private const string Admin = "admin-1";
    private const string Asset = "follow-up-instructions";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_usage_lists_the_skill_versions_and_agents_that_name_an_asset(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var assets = host.Services.GetRequiredService<TestTextAssets>();
        var first = assets.Put(Asset, 1, "Name the task that comes next and why.");
        var other = assets.Put("risk-review-instructions", 1, "Name the riskiest task.");
        Assert.True((await SaveSkillAsync(host, FollowUp, Request(Reference(first)))).Success);
        Assert.True((await SaveSkillAsync(host, "risk-review", Request(Reference(other)))).Success);
        Assert.True((await SaveSkillAsync(host, "inline", new NhAssistantDataSkillRequest("Writes its own text.", "Say what changed."))).Success);
        Assert.True((await CreateAgentAsync(host, Input(Follower, Reference(first)))).Success);
        // An agent whose instructions are text has an asset id of its own, here the same id; it does
        // not name the application's asset.
        Assert.True((await CreateAgentAsync(host, Input("follow-up", null, "Follow up by hand."))).Success);

        var usage = await UsageAsync(host, Asset);

        Assert.True(usage.Success, string.Join(", ", usage.AllErrorMessages));
        Assert.Equal(Asset, usage.Data!.AssetId);
        var skill = Assert.Single(usage.Data.Skills);
        Assert.Equal((AgentId, FollowUp, 1, NhAssistantSkillDefinitionStatuses.Live, 1, first.Manifest.ContentHash), (skill.AgentId, skill.SkillId, skill.Version, skill.Status, skill.AssetVersion, skill.Hash));
        var agent = Assert.Single(usage.Data.Agents);
        Assert.Equal((Follower, 1, 1, first.Manifest.ContentHash), (agent.AgentId, agent.Version, agent.AssetVersion, agent.Hash));

        // A new version of the asset changes nothing by itself: everything still names version 1.
        var second = assets.Put(Asset, 2, "Name the two tasks that come next and why.");
        var unchanged = await UsageAsync(host, Asset);
        Assert.Equal([1], unchanged.Data!.Skills.Select(item => item.AssetVersion));
        Assert.Equal([1], unchanged.Data.Agents.Select(item => item.AssetVersion));

        // Saved as the skill's next version, the history keeps which version each one used.
        Assert.True((await SaveSkillAsync(host, FollowUp, Request(Reference(second)) with { ExpectedVersion = 1 })).Success);
        var moved = await UsageAsync(host, Asset);
        Assert.Equal(
            [(2, NhAssistantSkillDefinitionStatuses.Live, 2, second.Manifest.ContentHash), (1, NhAssistantSkillDefinitionStatuses.Superseded, 1, first.Manifest.ContentHash)],
            moved.Data!.Skills.Select(item => (item.Version, item.Status, item.AssetVersion, item.Hash)).ToArray());
        Assert.Equal([1], moved.Data.Agents.Select(item => item.AssetVersion));

        // The other asset has its own users; an asset nothing names has none.
        Assert.Equal(["risk-review"], (await UsageAsync(host, "risk-review-instructions")).Data!.Skills.Select(item => item.SkillId));
        var unused = await UsageAsync(host, "unused-instructions");
        Assert.Empty(unused.Data!.Skills);
        Assert.Empty(unused.Data.Agents);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_id_that_is_not_an_asset_id_is_refused_and_nothing_is_audited(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var first = host.Services.GetRequiredService<TestTextAssets>().Put(Asset, 1, "Name the task that comes next and why.");
        Assert.True((await SaveSkillAsync(host, FollowUp, Request(Reference(first)))).Success);
        var audited = host.Business.Events.Count;

        // Ids are lowercase, so a provider whose comparison ignores case finds nothing more.
        Assert.Equal(["assetId:invalid"], Fields(await UsageAsync(host, "Follow-Up-Instructions")));
        Assert.Equal(["assetId:invalid"], Fields(await UsageAsync(host, new string('a', 129))));
        Assert.Single((await UsageAsync(host, Asset)).Data!.Skills);
        Assert.Equal(audited, host.Business.Events.Count);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TestTextAssets>();
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .UseTextAssetSource<TestTextAssets>()
                .AddSubjectType<TestSubjectResolver>()
                .ConfigureAgent(AgentId, agent => agent.Grant("projects.*")));
    }

    private static NhAssistantInstructionAssetReference Reference(NhAiTextAsset asset)
    {
        return new NhAssistantInstructionAssetReference(asset.Manifest.Id, asset.Manifest.Version, asset.Manifest.ContentHash);
    }

    private static NhAssistantDataSkillRequest Request(NhAssistantInstructionAssetReference reference)
    {
        return new NhAssistantDataSkillRequest("Follows up changed projects.") { InstructionsAsset = reference };
    }

    private static NhAssistantAgentInput Input(string id, NhAssistantInstructionAssetReference? reference, string instructions = "")
    {
        return new NhAssistantAgentInput(
            id,
            "Project follower",
            "Follows up changed projects.",
            instructions,
            ["projects.*"],
            [],
            null,
            NhAiAutonomyLevel.Propose,
            InstructionsAsset: reference);
    }

    private static async Task<TaskResult<NhAssistantAgent>> CreateAgentAsync(AssistantTestHost host, NhAssistantAgentInput input)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(input, Admin, CancellationToken.None);
    }

    private static async Task<TaskResult<NhAssistantDataSkillDto>> SaveSkillAsync(AssistantTestHost host, string skillId, NhAssistantDataSkillRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().SaveSkillAsync(AgentId, skillId, request, Admin);
    }

    private static async Task<TaskResult<NhAssistantInstructionAssetUsageDto>> UsageAsync(AssistantTestHost host, string assetId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantInstructionAssetUsage>().ListAsync(assetId);
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static IReadOnlyList<string> Fields(TaskResult result)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(result));
        return NhAssistantResults.FieldErrorsOf(result)
            .SelectMany(field => field.Value.Select(error => field.Key + ":" + error))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
