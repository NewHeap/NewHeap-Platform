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
    private const string Summary = "summary";
    private const string Current = "current-follow-up";

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

    /// <summary>
    /// FB32: a new version of the asset becomes a candidate in shadow of every live data skill that names
    /// an older one, through the skill's own save, with the locks of agent packages and an outcome per
    /// skill; agents created in the administration are listed only.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_new_asset_version_becomes_a_candidate_of_every_live_skill_that_names_an_older_one(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var assets = host.Services.GetRequiredService<TestTextAssets>();
        var first = assets.Put(Asset, 1, "Name the task that comes next and why.");
        Assert.True((await SaveSkillAsync(host, FollowUp, Request(Reference(first)) with { MaxTokensPerRun = 4_000 })).Success);
        Assert.True((await SaveSkillAsync(host, Summary, Request(Reference(first)))).Success);
        Assert.True((await SaveSkillAsync(host, "inline", new NhAssistantDataSkillRequest("Writes its own text.", "Say what changed."))).Success);
        Assert.True((await CreateAgentAsync(host, Input(Follower, Reference(first)))).Success);
        // An agent package the agent's skills were imported from locks the summary's instructions.
        await LockSummaryInstructionsAsync(host);
        var second = assets.Put(Asset, 2, "Name the two tasks that come next and why.");
        // A skill that names the new version already is current; an agent on it is current too.
        Assert.True((await SaveSkillAsync(host, Current, Request(Reference(second)))).Success);
        Assert.True((await CreateAgentAsync(host, Input("project-follower-next", Reference(second)))).Success);
        var audited = host.Business.Events.Count;

        var proposed = await ProposeAsync(host, Asset, 2, "Version 2 names two tasks");

        Assert.True(proposed.Success, string.Join(", ", proposed.AllErrorMessages));
        Assert.Equal((Asset, 2, second.Manifest.ContentHash), (proposed.Data!.AssetId, proposed.Data.AssetVersion, proposed.Data.Hash));
        Assert.Equal(
            [
                (FollowUp, 1, 1, NhAssistantInstructionAssetCandidateOutcomes.Proposed, (int?)2, (string?)null),
                (Summary, 1, 1, NhAssistantInstructionAssetCandidateOutcomes.Locked, null, NhAssistantErrorCodes.PackageLocked)
            ],
            proposed.Data.Skills.Select(skill => (skill.SkillId, skill.LiveVersion, skill.LiveAssetVersion, skill.Outcome, skill.CandidateVersion, skill.Code)).ToArray());
        Assert.Equal(["locked"], proposed.Data.Skills.Single(skill => skill.SkillId == Summary).Errors!["instructions"]);
        // The agent is listed only: an administrator saves it as its next version.
        var agent = Assert.Single(proposed.Data.Agents);
        Assert.Equal((Follower, 1), (agent.AgentId, agent.AssetVersion));

        // The candidate runs next to the live version, which keeps naming version 1; its other fields
        // are the live version's.
        var configuration = (await ConfigurationAsync(host, service => service.GetAsync(AgentId)))!;
        var followUp = configuration.DataSkills.Single(skill => skill.SkillId == FollowUp);
        Assert.Equal((1, 1), (followUp.Version, followUp.InstructionsAsset!.Version));
        var candidate = followUp.Candidate!;
        Assert.Equal(
            (2, 2, second.Manifest.ContentHash, (long?)4_000, "Version 2 names two tasks"),
            (candidate.Version, candidate.InstructionsAsset!.Version, candidate.InstructionsAsset.Hash, candidate.MaxTokensPerRun, candidate.Reason));
        var summary = configuration.DataSkills.Single(skill => skill.SkillId == Summary);
        Assert.Null(summary.Candidate);
        Assert.Null(configuration.DataSkills.Single(skill => skill.SkillId == Current).Candidate);
        var audit = Assert.Single(host.Business.Events.Skip(audited));
        Assert.Equal(
            (NhAssistantAuditEventKind.AdminAgentSkillDefinitionChanged, FollowUp, 2, "candidate", Asset + "@2"),
            (audit.Kind, audit.SkillId, audit.AgentVersion, audit.ResultCode, audit.InstructionsVersion));

        // Proposed again, the candidate that names the version stays; nothing is saved twice.
        var again = await ProposeAsync(host, Asset, 2);
        Assert.Equal(
            [(FollowUp, NhAssistantInstructionAssetCandidateOutcomes.AlreadyProposed, (int?)2), (Summary, NhAssistantInstructionAssetCandidateOutcomes.Locked, null)],
            again.Data!.Skills.Select(skill => (skill.SkillId, skill.Outcome, skill.CandidateVersion)).ToArray());
        Assert.Equal(audited + 1, host.Business.Events.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_of_an_asset_version_that_cannot_be_instructions_is_refused_and_saves_nothing(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var assets = host.Services.GetRequiredService<TestTextAssets>();
        var first = assets.Put(Asset, 1, "Name the task that comes next and why.");
        Assert.True((await SaveSkillAsync(host, FollowUp, Request(Reference(first)))).Success);
        assets.Put(Asset, 2, "Name what an outsider asks for.", NhAiContextTrust.UntrustedRetrieved);
        var audited = host.Business.Events.Count;

        Assert.Equal(["assetId:invalid"], Fields(await ProposeAsync(host, "Follow-Up-Instructions", 2)));
        Assert.Equal(["assetVersion:invalid"], Fields(await ProposeAsync(host, Asset, 0)));
        Assert.Equal(["reason:too-long"], Fields(await ProposeAsync(host, Asset, 2, new string('r', 501))));
        Assert.Equal(NhAssistantAdminErrorCodes.InstructionsAssetNotFound, Code(await ProposeAsync(host, Asset, 3)));
        // Version 2 is no trusted instructions of the application.
        Assert.Equal(["instructionsAsset:invalid"], Fields(await ProposeAsync(host, Asset, 2)));
        assets.Fail = true;
        Assert.Equal(NhAssistantAdminErrorCodes.InstructionsAssetUnavailable, Code(await ProposeAsync(host, Asset, 2)));
        assets.Fail = false;

        Assert.Null((await ConfigurationAsync(host, service => service.GetAsync(AgentId)))!.DataSkills.Single().Candidate);
        Assert.Equal(audited, host.Business.Events.Count);
    }

    /// <summary>
    /// FB32: exports the agent's data skills as a package that locks the summary's instructions and
    /// imports it back, so the host's skills are linked to it.
    /// </summary>
    private static async Task LockSummaryInstructionsAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var packages = scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>();
        var exported = await packages.ExportAsync(
            [AgentId],
            new NhAssistantAgentPackageExportOptions(
                "project-assistant-skills",
                Locks: new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
                {
                    [AgentId] = new Dictionary<string, IReadOnlyList<string>> { ["skill:" + Summary] = ["instructions"] }
                }),
            Admin);
        Assert.True(exported.Success, string.Join(", ", exported.AllErrorMessages));
        var imported = await packages.ImportAsync(
            exported.Data!.Package,
            new NhAssistantAgentPackageImportOptions(Relink: [new NhAssistantPackagePartReference(AgentId)]),
            Admin);
        Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));
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

    private static async Task<TaskResult<NhAssistantInstructionAssetCandidatesDto>> ProposeAsync(
        AssistantTestHost host,
        string assetId,
        int assetVersion,
        string? reason = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantInstructionAssetUsage>().ProposeCandidatesAsync(assetId, assetVersion, Admin, reason);
    }

    private static async Task<T> ConfigurationAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
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
