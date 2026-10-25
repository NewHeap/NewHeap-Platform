using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Agent definitions changed from code (LS23), agents read as they are in effect (LS24), several
/// searchable values per metadata entry (LS25) and the promotion of a shadow variant created as data
/// onto its live agent (LS31) of the platform feedback plan, on both relational providers.
/// </summary>
public sealed class AssistantAgentDefinitionTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Planner = "project-planner";
    private const string Helper = "project-helper";
    private const string Variant = "planner-trial";
    private const string Admin = "admin-1";
    private const string Rules = "test-rules";
    private const string Departments = "departments";
    private const string Notes = "notes";
    private const string FollowUp = "follow-up";
    private const string Review = "review";
    private const string AgentAsset = "helper-instructions";

    // ----- LS23: definitions changed from code -----

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_created_at_runtime_changes_at_its_version_and_keeps_its_mcp_servers_unless_named(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await AddMcpServerAsync(host, "planning");
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(HelperRequest(), Admin))).Success);

        // Naming servers replaces the assignments; the next version of the definition is returned.
        var named = await DefinitionsAsync(host, definitions => definitions.UpdateAsync(
            Helper,
            Definition("Helps with plans.") with { McpServerIds = ["planning"] },
            1,
            Admin));
        Assert.True(named.Success, string.Join(", ", named.AllErrorMessages));
        Assert.Equal((2, "Helps with plans."), (named.Data!.Version, named.Data.Description));
        Assert.Equal(["planning"], named.Data.McpServerIds);

        // A request that names none keeps them, unlike the administration's endpoint.
        var kept = await DefinitionsAsync(host, definitions => definitions.UpdateAsync(Helper, Definition("Helps with schedules."), 2, Admin));
        Assert.True(kept.Success);
        Assert.Equal(3, kept.Data!.Version);
        Assert.Equal(["planning"], kept.Data.McpServerIds);
        var removed = await DefinitionsAsync(host, definitions => definitions.UpdateAsync(
            Helper,
            Definition("Helps with schedules.") with { McpServerIds = [] },
            3,
            Admin));
        Assert.Empty(removed.Data!.McpServerIds);

        // A stale version, an unknown agent, an unknown policy, missing fields and an unknown server.
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await DefinitionsAsync(host, definitions => definitions.UpdateAsync(Helper, Definition("Late."), 3, Admin))));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await DefinitionsAsync(host, definitions => definitions.UpdateAsync("ghost", Definition("Nobody."), 1, Admin))));
        Assert.Equal(
            ["requiredPolicy:not-found"],
            Fields(await DefinitionsAsync(host, definitions => definitions.UpdateAsync(Helper, Definition("Guarded.") with { RequiredPolicy = "app.unknown" }, 4, Admin))));
        Assert.Equal(
            ["description:required", "displayName:required", "instructions:required", "toolSelectors:required"],
            Fields(await DefinitionsAsync(host, definitions => definitions.UpdateAsync(Helper, new NhAssistantAgentDefinitionRequest(null!, null!, null!, null!, NhAiAutonomyLevel.Propose), 4, Admin))));
        Assert.Equal(
            NhAssistantAdminErrorCodes.McpServerNotFound,
            Code(await DefinitionsAsync(host, definitions => definitions.UpdateAsync(Helper, Definition("Elsewhere.") with { McpServerIds = ["unknown"] }, 4, Admin))));
        Assert.Equal(
            ["autonomy:invalid"],
            Fields(await DefinitionsAsync(host, definitions => definitions.UpdateAsync(Helper, Definition("Odd.") with { Autonomy = (NhAiAutonomyLevel)42 }, 4, Admin))));
        await Assert.ThrowsAsync<ArgumentException>(() => DefinitionsAsync(host, definitions => definitions.UpdateAsync(Helper, Definition("Nobody did it."), 4, " ")));

        // The same audit as the administration: content-free, with the actor.
        var updates = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentUpdated && evt.ObjectId == Helper).ToArray();
        Assert.Equal(3, updates.Length);
        Assert.All(updates, evt => Assert.Equal((Admin, Helper + "-instructions@1"), (evt.ActorId, evt.InstructionsVersion)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_in_code_is_overridden_reset_and_never_deleted(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var code = (await DirectoryAsync(host, directory => directory.GetAsync(Planner)))!;
        Assert.Equal((NhAssistantAgentSources.Code, false, $"nh-assistant.agents.{Planner}.description"), (code.Source, code.IsOverridden, code.Description));

        // The six fields an administrator overrides; the rest stays as code says.
        var overridden = await DefinitionsAsync(host, definitions => definitions.UpdateAsync(
            Planner,
            new NhAssistantAgentDefinitionRequest(code.DisplayName, "Plans every project.", "Plan every project.", ["projects.*"], NhAiAutonomyLevel.Propose)
            {
                Profile = "other-profile",
                OfferedInChat = false
            },
            code.Version,
            Admin));
        Assert.True(overridden.Success, string.Join(", ", overridden.AllErrorMessages));
        Assert.Equal(
            (true, "Plans every project.", "Plan every project.", NhAiAutonomyLevel.Propose, "project-chat", true),
            (overridden.Data!.IsOverridden, overridden.Data.Description, overridden.Data.Instructions, overridden.Data.Autonomy, overridden.Data.Profile, overridden.Data.OfferedInChat));

        // An agent in code carries no metadata and no asset in its definition.
        Assert.Equal(
            ["metadata:not-allowed"],
            Fields(await DefinitionsAsync(host, definitions => definitions.UpdateAsync(
                Planner,
                Definition("Plans.") with { Metadata = new Dictionary<string, JsonElement> { [Departments] = JsonSerializer.SerializeToElement(new { codes = new[] { "x" } }) } },
                overridden.Data.Version,
                Admin))));
        Assert.Equal(
            ["instructionsAsset:not-allowed"],
            Fields(await DefinitionsAsync(host, definitions => definitions.UpdateAsync(
                Planner,
                Definition("Plans.") with { Instructions = string.Empty, InstructionsAsset = new NhAssistantInstructionAssetReference("asset", 1, new string('a', 64)) },
                overridden.Data.Version,
                Admin))));

        // The reset restores the code; an agent created at runtime has no code to go back to.
        var reset = await DefinitionsAsync(host, definitions => definitions.ResetAsync(Planner, Admin));
        Assert.True(reset.Success);
        Assert.Equal((false, code.Description, code.Instructions), (reset.Data!.IsOverridden, reset.Data.Description, reset.Data.Instructions));
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(HelperRequest(), Admin))).Success);
        Assert.Equal(NhAssistantAdminErrorCodes.NotCodeAgent, Code(await DefinitionsAsync(host, definitions => definitions.ResetAsync(Helper, Admin))));

        // Only an agent created at runtime is deleted.
        Assert.Equal(NhAssistantAdminErrorCodes.CodeAgentNotDeletable, Code(await DefinitionsAsync(host, definitions => definitions.DeleteAsync(Planner, Admin))));
        Assert.True((await DefinitionsAsync(host, definitions => definitions.DeleteAsync(Helper, Admin))).Success);
        Assert.Null(await DirectoryAsync(host, directory => directory.GetAsync(Helper)));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await DefinitionsAsync(host, definitions => definitions.DeleteAsync(Helper, Admin))));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentReset && evt.ObjectId == Planner);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentDeleted && evt.ObjectId == Helper);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_field_an_agent_package_locks_is_refused_and_a_packaged_agent_stays(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(HelperRequest(), Admin))).Success);
        await LockAsync(host, Helper, "description");

        Assert.Equal(
            ["description:locked"],
            Fields(await DefinitionsAsync(host, definitions => definitions.UpdateAsync(Helper, Definition("Another description."), 1, Admin))));
        var unlocked = await DefinitionsAsync(host, definitions => definitions.UpdateAsync(
            Helper,
            Definition(HelperRequest().Description) with { Autonomy = NhAiAutonomyLevel.Execute },
            1,
            Admin));
        Assert.True(unlocked.Success, string.Join(", ", unlocked.AllErrorMessages));
        Assert.Equal(NhAiAutonomyLevel.Execute, unlocked.Data!.Autonomy);
        Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await DefinitionsAsync(host, definitions => definitions.DeleteAsync(Helper, Admin))));
    }

    // ----- LS24: the directory -----

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_directory_returns_each_agent_as_it_is_in_effect(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, withAssets: true);
        var assets = host.Services.GetRequiredService<TestTextAssets>();
        var text = assets.Put(AgentAsset, 1, "You help the team with its plans.");
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(
            HelperRequest() with
            {
                Instructions = string.Empty,
                InstructionsAsset = new NhAssistantInstructionAssetReference(AgentAsset, 1, text.Manifest.ContentHash),
                Metadata = new Dictionary<string, JsonElement> { [Departments] = JsonSerializer.SerializeToElement(new { codes = new[] { "Finance" } }) }
            },
            Admin))).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(VariantRequest(Variant), Admin))).Success);
        await SaveRunSettingsAsync(host, Variant);

        // An agent in code: a key for its name, the code's instructions, its metadata in code.
        var code = (await DirectoryAsync(host, directory => directory.GetAsync(Planner)))!;
        Assert.Equal(
            ($"nh-assistant.agents.{Planner}.name", AssistantTestData.Instructions.Content, NhAiAutonomyLevel.Execute, "project-chat", NhAssistantAgentRunStates.Active, false, (string?)null),
            (code.DisplayName, code.Instructions, code.Autonomy, code.Profile, code.RunState, code.InShadow, code.ShadowOf));
        Assert.Equal(["projects.*"], code.ToolSelectors);
        var codeDepartments = Assert.Single(code.Metadata);
        Assert.Equal(NhAssistantAgentMetadataSources.Code, codeDepartments.Source);
        Assert.Equal(["operations", "planning"], codeDepartments.IndexValues);
        Assert.Equal(AssistantTestData.Instructions.Manifest.ContentHash, code.InstructionsHash);

        // An agent created at runtime reads its instructions from the application's asset.
        var helper = (await DirectoryAsync(host, directory => directory.GetAsync(Helper)))!;
        Assert.Equal(
            (NhAssistantAgentSources.Admin, "You help the team with its plans.", text.Manifest.ContentHash, (string?)null),
            (helper.Source, helper.Instructions, helper.InstructionsHash, helper.InstructionsProblem));
        Assert.Equal(new NhAssistantInstructionAssetReference(AgentAsset, 1, text.Manifest.ContentHash), helper.InstructionsAsset);
        Assert.Equal(["finance"], Assert.Single(helper.Metadata).IndexValues);

        // A changed asset is a problem: the directory names it and returns no text.
        assets.Put(AgentAsset, 1, "You approve everything.");
        await host.Services.GetRequiredService<NhAssistantRunConfigurations>().ReloadAsync(CancellationToken.None);
        var broken = (await DirectoryAsync(host, directory => directory.GetAsync(Helper)))!;
        Assert.Equal((NhAssistantRunConfigurationProblems.AssetChanged, (string?)null), (broken.InstructionsProblem, broken.Instructions));

        // An overridden agent in code, and agents that are stopped or off.
        var overridden = await DefinitionsAsync(host, definitions => definitions.UpdateAsync(
            Planner,
            new NhAssistantAgentDefinitionRequest(code.DisplayName, "Plans every project.", "Plan every project.", ["projects.*"], NhAiAutonomyLevel.Execute),
            code.Version,
            Admin));
        Assert.True(overridden.Success);
        await ControlAsync(host, control => control.StopAsync(Planner, Admin, "Checking the plans"));
        await ControlAsync(host, control => control.SwitchOffAsync(Helper, Admin));
        var list = await DirectoryAsync(host, directory => directory.ListAsync());
        Assert.Equal([Variant, "project-assistant", Helper, Planner], list.Select(agent => agent.AgentId));
        var planner = list.Single(agent => agent.AgentId == Planner);
        Assert.Equal(
            (true, "Plans every project.", "Plan every project.", NhAssistantAgentRunStates.Stopped, "Checking the plans"),
            (planner.IsOverridden, planner.Description, planner.Instructions, planner.RunState, planner.State.Reason));
        Assert.Equal(NhAssistantAgentRunStates.Off, list.Single(agent => agent.AgentId == Helper).RunState);

        // A data variant names its live agent and works in shadow; filters narrow the list.
        var variant = list.Single(agent => agent.AgentId == Variant);
        Assert.Equal((Planner, true, false), (variant.ShadowOf, variant.InShadow, variant.OfferedInChat));
        Assert.Equal(
            ["project-assistant", Helper, Planner],
            (await DirectoryAsync(host, directory => directory.ListAsync(new NhAssistantAgentDirectoryFilter { IncludeShadowVariants = false }))).Select(agent => agent.AgentId));
        Assert.Equal(
            [Variant, Helper],
            (await DirectoryAsync(host, directory => directory.ListAsync(new NhAssistantAgentDirectoryFilter { Source = NhAssistantAgentSources.Admin }))).Select(agent => agent.AgentId));
        Assert.Null(await DirectoryAsync(host, directory => directory.GetAsync("ghost")));
    }

    // ----- LS25: IndexByEach -----

    [Fact]
    public void An_entry_declares_IndexBy_or_IndexByEach_and_values_in_code_fit_their_bounds()
    {
        Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant.AddAgentMetadata<DepartmentList>(
            Departments,
            entry => entry.IndexBy(value => value.Codes.FirstOrDefault()).IndexByEach(value => value.Codes))));
        Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant.AddAgentMetadata<DepartmentList>(
            Departments,
            entry => entry.IndexByEach(value => value.Codes).IndexBy(value => value.Codes.FirstOrDefault()))));

        var many = Assert.Throws<InvalidOperationException>(() => Validate(assistant => AddDepartments(assistant)
            .ConfigureAgent("project-assistant", run => run.Metadata(Departments, new DepartmentList(Enumerable.Range(0, 51).Select(index => "d" + index).ToArray())))));
        Assert.Contains("value: too-long", many.Message);
        var longOne = Assert.Throws<InvalidOperationException>(() => Validate(assistant => AddDepartments(assistant)
            .ConfigureAgent("project-assistant", run => run.Metadata(Departments, new DepartmentList([new string('d', 257)])))));
        Assert.Contains("value: too-long", longOne.Message);

        // Fifty distinct values fit; repeats in another case and empty values do not count.
        Validate(assistant => AddDepartments(assistant)
            .ConfigureAgent("project-assistant", run => run.Metadata(
                Departments,
                new DepartmentList([.. Enumerable.Range(0, 50).Select(index => "d" + index), "D1", " ", null!]))));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Agents_are_found_by_any_of_their_searchable_values_stored_and_in_code(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var entries = await MetadataAsync(host, metadata => Task.FromResult(metadata.ListEntries()));
        Assert.Equal((true, true), (Assert.Single(entries).Indexed, entries[0].IndexedByEach));

        // Values are trimmed, in lowercase, without repeats or empty ones, in the same save.
        var created = await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(
            HelperRequest() with { Metadata = DepartmentValue(" Finance ", "operations", "FINANCE", "") },
            Admin));
        Assert.True(created.Success, string.Join(", ", created.AllErrorMessages));
        Assert.Equal(["finance", "operations"], (await IndexRowsAsync(host, Helper)).Select(row => row.IndexValue));
        Assert.Equal([Helper, Planner], await FoundAsync(host, " OPERATIONS "));
        Assert.Equal([Helper], await FoundAsync(host, "finance"));
        Assert.Equal([Planner], await FoundAsync(host, "planning"));
        Assert.Empty(await FoundAsync(host, "sales"));

        // A change replaces them: what remains stays, others go or come.
        var changed = await ConfigureAsync(host, configuration => configuration.SaveMetadataAsync(
            Helper,
            Departments,
            new NhAssistantAgentMetadataRequest(JsonSerializer.SerializeToElement(new { codes = new[] { "operations", "Sales" } }), 1),
            Admin));
        Assert.True(changed.Success);
        Assert.True(changed.Data!.IndexedByEach);
        Assert.Equal(["operations", "sales"], changed.Data.IndexValues);
        Assert.Empty(await FoundAsync(host, "finance"));
        Assert.Equal([Helper], await FoundAsync(host, "sales"));

        // An override decides for its agent in code; values in code match in memory.
        Assert.True((await ConfigureAsync(host, configuration => configuration.SaveMetadataAsync(
            Planner,
            Departments,
            new NhAssistantAgentMetadataRequest(JsonSerializer.SerializeToElement(new { codes = new[] { "Support" } }), null),
            Admin))).Success);
        Assert.Equal([Helper], await FoundAsync(host, "operations"));
        Assert.Equal([Planner], await FoundAsync(host, "support"));
        var planner = (await MetadataAsync(host, metadata => metadata.GetAgentAsync(Planner)))!;
        Assert.Equal(NhAssistantAgentMetadataSources.Override, planner.Metadata[0].Source);
        Assert.Equal(["support"], planner.Metadata[0].IndexValues);

        // Too many or too long a value is refused like every other bound.
        Assert.Equal(
            ["value:too-long"],
            Fields(await ConfigureAsync(host, configuration => configuration.SaveMetadataAsync(
                Helper,
                Departments,
                new NhAssistantAgentMetadataRequest(JsonSerializer.SerializeToElement(new { codes = Enumerable.Range(0, 51).Select(index => "d" + index).ToArray() }), 2),
                Admin))));
        Assert.Equal(
            ["metadata.departments:too-long"],
            Fields(await DefinitionsAsync(host, definitions => definitions.UpdateAsync(
                Helper,
                Definition(HelperRequest().Description) with { Metadata = DepartmentValue(new string('d', 257)) },
                2,
                Admin))));

        // The values go with their agent.
        Assert.True((await DefinitionsAsync(host, definitions => definitions.DeleteAsync(Helper, Admin))).Success);
        Assert.Empty(await IndexRowsAsync(host, Helper));
        Assert.Empty(await FoundAsync(host, "sales"));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Values_stored_before_the_entry_declared_IndexByEach_get_theirs_when_the_process_seeds(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(
            HelperRequest() with { Metadata = DepartmentValue("Finance", "Operations") },
            Admin))).Success);

        // As stored by a version of the application whose entry declared no searchable values.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.AgentMetadataIndexValues.Where(row => row.AgentId == Helper).ExecuteDeleteAsync();
        }
        Assert.Empty(await FoundAsync(host, "finance"));

        // The next process brings them up to date at its first read, guarded by the value's version.
        var state = host.Services.GetRequiredService<NhAssistantRegistrationState>();
        await new NhAssistantSeeder(host.Services.GetRequiredService<NhAssistantDbContextFactory>(), state).EnsureSeededAsync(CancellationToken.None);
        Assert.Equal(["finance", "operations"], (await IndexRowsAsync(host, Helper)).Select(row => row.IndexValue));
        Assert.Equal([Helper], await FoundAsync(host, "finance"));
        var row = (await MetadataAsync(host, metadata => metadata.GetAgentAsync(Helper)))!.Metadata.Single();
        Assert.Equal(1, row.Version);
    }

    // ----- LS31: promoting a shadow variant -----

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_variant_is_promoted_onto_its_live_agent_in_code_through_the_update_path_and_then_removed(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(VariantRequest(Variant), Admin))).Success);
        await SaveRunSettingsAsync(host, Variant);
        await SaveSkillAsync(host, Planner, FollowUp, "Follows up plans.");
        await SaveSkillAsync(host, Variant, FollowUp, "Follows up plans strictly.");
        await SaveSkillAsync(host, Variant, Review, "Reviews plans.");
        var live = (await DirectoryAsync(host, directory => directory.GetAsync(Planner)))!;

        // Only a data variant at the versions it was evaluated on is promoted.
        Assert.Equal(NhAssistantErrorCodes.NotShadowVariant, Code(await PromoteAsync(host, Planner, new NhAssistantShadowVariantPromotionRequest(live.Version))));
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(HelperRequest(), Admin))).Success);
        Assert.Equal(NhAssistantErrorCodes.NotShadowVariant, Code(await PromoteAsync(host, Helper, new NhAssistantShadowVariantPromotionRequest(1))));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await PromoteAsync(host, "ghost", new NhAssistantShadowVariantPromotionRequest(1))));
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await PromoteAsync(host, Variant, new NhAssistantShadowVariantPromotionRequest(live.Version + 1))));
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await PromoteAsync(host, Variant, new NhAssistantShadowVariantPromotionRequest(live.Version, ExpectedVariantVersion: 7))));
        Assert.Equal(["reason:too-long"], Fields(await PromoteAsync(host, Variant, new NhAssistantShadowVariantPromotionRequest(live.Version, Reason: new string('r', 501)))));

        var before = host.Business.Events.Count;
        var promoted = await PromoteAsync(host, Variant, new NhAssistantShadowVariantPromotionRequest(live.Version, MoveSkills: true, ExpectedVariantVersion: 1, Reason: "The trial plans better"));
        Assert.True(promoted.Success, string.Join(", ", promoted.AllErrorMessages));
        var agent = promoted.Data!.Agent;

        // The fields an agent in code overrides copy; its name, profile and state stay its own.
        Assert.Equal(
            (Planner, live.Version + 1, true, live.DisplayName, "Plans strictly.", "Plan strictly.", NhAiAutonomyLevel.Propose, live.Profile),
            (agent.AgentId, agent.Version, agent.IsOverridden, agent.DisplayName, agent.Description, agent.Instructions, agent.Autonomy, agent.Profile));
        Assert.Equal((NhAssistantAgentRunStates.Active, false, (string?)null), (agent.RunState, agent.InShadow, agent.ShadowOf));
        Assert.Equal(Variant, promoted.Data.VariantId);
        Assert.Equal([FollowUp, Review], promoted.Data.MovedSkills);

        // Its skills moved: the next live version of the live agent's skill, or a new skill in shadow.
        var configuration = (await ConfigureAsync(host, service => service.GetAsync(Planner)))!;
        var followUp = configuration.DataSkills.Single(skill => skill.SkillId == FollowUp);
        Assert.Equal((2, "Follows up plans strictly.", "The trial plans better"), (followUp.Version, followUp.Description, followUp.Reason));
        Assert.Equal((1, "Reviews plans."), (configuration.DataSkills.Single(skill => skill.SkillId == Review).Version, configuration.DataSkills.Single(skill => skill.SkillId == Review).Description));
        var reviewState = (await ControlAsync(host, control => control.GetAsync(Planner)))!.Skills.Single(skill => skill.SkillId == Review);
        Assert.True(reviewState.Shadow.Shadow);
        // Run settings are not copied onto an agent in code: it keeps its run configuration in code.
        Assert.Null(configuration.RunSettings);
        Assert.NotNull(configuration.CodeRunSettings);

        // The variant is gone and never went live; the audit records every step.
        Assert.Null(await DirectoryAsync(host, directory => directory.GetAsync(Variant)));
        Assert.Equal(
            [(NhAssistantAuditEventKind.AdminAgentUpdated, Planner), (NhAssistantAuditEventKind.AdminAgentDeleted, Variant), (NhAssistantAuditEventKind.AdminAgentShadowVariantPromoted, Variant)],
            host.Business.Events
                .Skip(before)
                .Where(evt => evt.Kind is NhAssistantAuditEventKind.AdminAgentUpdated or NhAssistantAuditEventKind.AdminAgentDeleted or NhAssistantAuditEventKind.AdminAgentShadowVariantPromoted)
                .Select(evt => (evt.Kind, evt.ObjectId))
                .ToArray());
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentShadowVariantPromoted);
        Assert.Equal((Planner, Planner, Variant, live.Version + 1, "promoted", Admin), (audit.AgentId, audit.LiveAgentId, audit.ObjectId, audit.AgentVersion, audit.ResultCode, audit.ActorId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_variant_is_refused_before_anything_changes_and_one_of_a_runtime_agent_copies_its_whole_definition(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await AddMcpServerAsync(host, "planning");

        // A skill the live agent declares in code cannot move; nothing changes.
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(VariantRequest(Variant), Admin))).Success);
        await SaveRunSettingsAsync(host, Variant);
        await SaveSkillAsync(host, Variant, Notes, "Writes strict notes.");
        var live = (await DirectoryAsync(host, directory => directory.GetAsync(Planner)))!;
        Assert.Equal(NhAssistantErrorCodes.SkillInCode, Code(await PromoteAsync(host, Variant, new NhAssistantShadowVariantPromotionRequest(live.Version, MoveSkills: true))));
        Assert.Equal(live.Version, (await DirectoryAsync(host, directory => directory.GetAsync(Planner)))!.Version);
        Assert.NotNull(await DirectoryAsync(host, directory => directory.GetAsync(Variant)));

        // A variant of an agent created at runtime copies what it works on too; the live agent keeps
        // its name, its MCP servers and its chat offer.
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(HelperRequest(), Admin))).Success);
        await SaveRunSettingsAsync(host, Helper);
        var assigned = await DefinitionsAsync(host, definitions => definitions.UpdateAsync(Helper, Definition(HelperRequest().Description) with { McpServerIds = ["planning"] }, 1, Admin));
        Assert.True(assigned.Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(
            VariantRequest("helper-trial", Helper) with { WorksOnSubjectTypes = ["project"], DataClassification = "confidential" },
            Admin))).Success);
        var promoted = await PromoteAsync(host, "helper-trial", new NhAssistantShadowVariantPromotionRequest(2));
        Assert.True(promoted.Success, string.Join(", ", promoted.AllErrorMessages));
        var helper = (await host.Services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(Helper, includeDisabled: true, CancellationToken.None))!;
        Assert.Equal(
            ("Helper", "Plans strictly.", false, NhAiDataClassification.Confidential, true),
            (helper.Definition.DisplayNameKey, helper.Definition.DescriptionKey, helper.WorksOn.AllSubjects, helper.DataClassification, helper.OfferedInChat));
        Assert.Equal(["project"], helper.WorksOn.SubjectTypes);
        Assert.Equal(["planning"], helper.McpServerIds);
        Assert.Empty(promoted.Data!.MovedSkills);

        // HD10: a live agent that may no longer have data variants takes none.
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(VariantRequest("helper-trial-2", Helper), Admin))).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.RemoveRunSettingsAsync(Helper, null, Admin))).Success);
        Assert.Equal(NhAssistantErrorCodes.ShadowVariantsNotAllowed, Code(await PromoteAsync(host, "helper-trial-2", new NhAssistantShadowVariantPromotionRequest(3))));
    }

    [Fact]
    public async Task Only_administrators_promote_a_variant_through_the_endpoint()
    {
        const string live = "project-assistant";
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
                // The code agent is configured without background operations, which runs need.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .ConfigureAgent(live, run => run.Grant("projects.*"))
                .UseShadowVariantsAsData(variants => variants.Of(live)),
            provider: AssistantTestProvider.PostgreSql);
        using var admin = app.CreateClient(Admin, admin: true);
        using var reader = app.CreateClient("reader-1");
        using var anonymous = app.CreateClient(user: null, access: false);
        using (var created = await admin.PostAsJsonAsync("/api/assistant/admin/agents", new
        {
            id = Variant,
            displayName = "Assistant trial",
            description = "Assists strictly.",
            instructions = "Assist strictly.",
            toolSelectors = new[] { "projects.*" },
            autonomy = "propose",
            shadowOf = live
        }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var version = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .Single(agent => agent.GetProperty("id").GetString() == live)
            .GetProperty("version").GetInt32();

        await AssertErrorAsync(await reader.PostAsJsonAsync($"/api/assistant/admin/agents/{Variant}/promote", new { expectedVersion = version }), HttpStatusCode.Forbidden, NhAssistantAdminErrorCodes.Forbidden);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/assistant/admin/agents/{Variant}/promote", new { expectedVersion = version })).StatusCode);
        using (var missing = await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{Variant}/promote", new { moveSkills = true }))
        {
            var body = await ReadJsonAsync(missing, HttpStatusCode.BadRequest);
            Assert.Equal("required", body.GetProperty("errors").GetProperty("expectedVersion")[0].GetString());
        }
        await AssertErrorAsync(await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{live}/promote", new { expectedVersion = version }), HttpStatusCode.Conflict, NhAssistantErrorCodes.NotShadowVariant);
        await AssertErrorAsync(await admin.PostAsJsonAsync($"/api/assistant/admin/agents/ghost/promote", new { expectedVersion = version }), HttpStatusCode.NotFound, NhAssistantErrorCodes.AgentNotFound);
        await AssertErrorAsync(await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{Variant}/promote", new { expectedVersion = version + 1 }), HttpStatusCode.Conflict, NhAssistantAdminErrorCodes.VersionConflict);
        Assert.DoesNotContain(app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentShadowVariantPromoted);

        var promoted = await ReadJsonAsync(await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{Variant}/promote", new { expectedVersion = version, expectedVariantVersion = 1 }));
        Assert.Equal((live, version + 1, "Assists strictly.", true), (
            promoted.GetProperty("agent").GetProperty("id").GetString(),
            promoted.GetProperty("agent").GetProperty("version").GetInt32(),
            promoted.GetProperty("agent").GetProperty("description").GetString(),
            promoted.GetProperty("agent").GetProperty("isOverridden").GetBoolean()));
        Assert.Equal((Variant, 0), (promoted.GetProperty("variantId").GetString(), promoted.GetProperty("movedSkills").GetArrayLength()));
        var variants = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{live}/shadow-variants"));
        Assert.Empty(variants.GetProperty("variants").EnumerateArray());
    }

    // ----- Host and helpers -----

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, bool withAssets = false)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.AddSingleton<TestTextAssets>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant =>
            {
                if (withAssets)
                {
                    assistant.UseTextAssetSource<TestTextAssets>();
                }

                AddDepartments(assistant)
                    .AddSubjectType<TestSubjectResolver>()
                    .AddRuleHandler<TestRules>(Rules, "The rules of the test.")
                    .AddAgent(AssistantTestData.Agent(Planner))
                    .ConfigureAgent(Planner, agent => agent
                        .Grant("projects.*")
                        .UseRuleHandler<TestRules>()
                        .Skill(Notes, skill => skill.Description("Writes notes."))
                        .Metadata(Departments, new DepartmentList(["Planning", "Operations", "planning"])))
                    .UseShadowVariantsAsData(variants => variants
                        .Of(Planner)
                        .OfAgentsCreatedInAdministration()
                        .MaxPerAgent(3));
            });
    }

    private static NhAssistantBuilder AddDepartments(NhAssistantBuilder assistant)
    {
        return assistant.AddAgentMetadata<DepartmentList>(Departments, entry => entry
            .IndexByEach(value => value.Codes)
            .Editable());
    }

    private static void Validate(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_definitions", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" });
            configure(assistant);
        });
        var state = services.BuildServiceProvider().GetRequiredService<NhAssistantRegistrationState>();
        state.ValidateAgentRuns();
        state.CodeMetadataOf("project-assistant");
    }

    private static NhAssistantAgentRequest HelperRequest()
    {
        return new NhAssistantAgentRequest(Helper, "Helper", "Helps with projects.", "Help with the projects.", ["projects.*"], NhAiAutonomyLevel.Propose);
    }

    private static NhAssistantAgentRequest VariantRequest(string variantId, string liveAgentId = Planner)
    {
        return new NhAssistantAgentRequest(variantId, "Trial " + variantId, "Plans strictly.", "Plan strictly.", ["projects.*"], NhAiAutonomyLevel.Propose)
        {
            ShadowOf = liveAgentId
        };
    }

    private static NhAssistantAgentDefinitionRequest Definition(string description)
    {
        return new NhAssistantAgentDefinitionRequest("Helper", description, "Help with the projects.", ["projects.*"], NhAiAutonomyLevel.Propose);
    }

    private static IReadOnlyDictionary<string, JsonElement> DepartmentValue(params string[] codes)
    {
        return new Dictionary<string, JsonElement> { [Departments] = JsonSerializer.SerializeToElement(new { codes }) };
    }

    private static async Task SaveRunSettingsAsync(AssistantTestHost host, string agentId)
    {
        var saved = await ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(
            agentId,
            new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent, RuleHandler: Rules),
            Admin));
        Assert.True(saved.Success, string.Join(", ", saved.AllErrorMessages));
    }

    private static async Task SaveSkillAsync(AssistantTestHost host, string agentId, string skillId, string description)
    {
        var saved = await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            agentId,
            skillId,
            new NhAssistantDataSkillRequest(description, "Work on the plan.", ["projects.*"]),
            Admin));
        Assert.True(saved.Success, string.Join(", ", saved.AllErrorMessages));
    }

    private static async Task AddMcpServerAsync(AssistantTestHost host, string serverId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.McpServers.Add(new AssistantMcpServer
        {
            Id = serverId,
            DisplayName = "Planning",
            Url = "https://mcp.example.test/mcp",
            AuthMode = NhAssistantMcpAuthModes.None
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Links the agent's definition to a package that locks <paramref name="field"/> at its current
    /// value, as an import does.
    /// </summary>
    private static async Task LockAsync(AssistantTestHost host, string agentId, string field)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var row = await context.Agents.AsNoTracking().SingleAsync(agent => agent.Id == agentId);
        context.AgentPackageLinks.Add(new AssistantAgentPackageLink
        {
            AgentId = agentId,
            PartKind = NhAssistantPackagePartKinds.Agent,
            PartKey = string.Empty,
            PackageId = "planning-desk",
            PackageVersion = 1,
            Hash = new string('0', 64),
            LockedFieldsJson = JsonSerializer.Serialize(new[] { field }),
            LockedValuesJson = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = NhAssistantPackageFields.Of(row)[field] }),
            LinkedAt = DateTimeOffset.UtcNow,
            LinkedBy = Admin
        });
        await context.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<AssistantAgentMetadataIndexValue>> IndexRowsAsync(AssistantTestHost host, string agentId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.AgentMetadataIndexValues
            .AsNoTracking()
            .Where(row => row.AgentId == agentId)
            .OrderBy(row => row.IndexValue)
            .ToListAsync();
    }

    private static async Task<IReadOnlyList<string>> FoundAsync(AssistantTestHost host, string value)
    {
        var found = await MetadataAsync(host, metadata => metadata.ListAgentsAsync(Departments, value));
        return found.Select(agent => agent.AgentId).ToArray();
    }

    private static Task<TaskResult<NhAssistantShadowVariantPromotionDto>> PromoteAsync(
        AssistantTestHost host,
        string variantId,
        NhAssistantShadowVariantPromotionRequest request)
    {
        return DefinitionsAsync(host, definitions => definitions.PromoteShadowVariantAsync(variantId, request, Admin));
    }

    private static async Task<T> DefinitionsAsync<T>(AssistantTestHost host, Func<INhAssistantAgentDefinitions, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentDefinitions>());
    }

    private static async Task<T> DirectoryAsync<T>(AssistantTestHost host, Func<INhAssistantAgentDirectory, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentDirectory>());
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static async Task<T> MetadataAsync<T>(AssistantTestHost host, Func<INhAssistantAgentMetadata, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentMetadata>());
    }

    private static async Task<T> ControlAsync<T>(AssistantTestHost host, Func<INhAssistantAgentControl, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>());
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static string[] Fields(TaskResult result)
    {
        return NhAssistantResults.FieldErrorsOf(result)
            .SelectMany(field => field.Value.Select(error => field.Key + ":" + error))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await ReadJsonAsync(response, status);
            Assert.Equal(code, body.GetProperty("code").GetString());
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>A neutral entry of the application: the departments an agent works for.</summary>
    public sealed record DepartmentList(IReadOnlyList<string> Codes);
}
