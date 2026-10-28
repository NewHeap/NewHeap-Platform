using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// A variant promoted as the package's next version (FB11 of the platform feedback plan on 21.0.0):
/// with <c>AsPackageVersion</c> a shadow variant whose every part is linked to one agent package
/// applies its parts to the live agent with the import's rules, relinks the live agent to that
/// package version and is deleted with its links, in one transaction with one audit event. With
/// <c>UncarriedParts = Relink</c> (FB33 of the plan on 23.0.0) the live agent's linked parts the variant
/// does not carry stay linked to that version with their locks. Proved on both relational providers;
/// the promotion without the option stays as it was.
/// </summary>
public sealed class AssistantPackageVersionPromotionTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Kickoff = AssistantAgentPackageTests.Kickoff;
    private const string Intake = AssistantAgentPackageTests.Intake;
    private const string Trial = AssistantAgentPackageTests.Trial;
    private const string Planner = AssistantAgentPackageTests.Planner;
    private const string PlannerTrial = "project-planner-trial";
    private const string Desks = AssistantAgentPackageTests.PackageId;
    private const string NextDesks = "project-desks-next";
    private const string NextPlanner = "project-planner-next";
    private const string Admin = AssistantAgentPackageTests.Admin;
    private const string TrialTrigger = "kickoff-trial-on-change";
    private const string NewInstructions = "Start the project once its plan and budget are approved.";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_package_linked_variant_becomes_the_packages_next_version_of_its_live_agent(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await ImportAsync(host, AssistantAgentPackageTests.Desks(locked: true), AssistantAgentPackageTests.Options());
        await ImportAsync(host, NextDesksPackage(), TrialOptions());
        var live = (await DirectoryAsync(host, directory => directory.GetAsync(Kickoff)))!;
        var before = host.Business.Events.Count;

        var promoted = await PromoteAsync(host, Trial, new NhAssistantShadowVariantPromotionRequest(live.Version, ExpectedVariantVersion: 1, Reason: "The trial starts projects carefully")
        {
            AsPackageVersion = true
        });

        Assert.True(promoted.Success, string.Join(", ", promoted.AllErrorMessages));
        var agent = promoted.Data!.Agent;
        // The definition of the package version, with the live agent's name and chat offer.
        Assert.Equal(
            (Kickoff, live.Version + 1, "Kickoff desk", "Starts projects carefully.", "Explain the plan before you start the project.", true, (string?)null),
            (agent.AgentId, agent.Version, agent.DisplayName, agent.Description, agent.Instructions, agent.OfferedInChat, agent.ShadowOf));
        Assert.Equal(Trial, promoted.Data.VariantId);
        Assert.Equal(["kickoff"], promoted.Data.MovedSkills);

        // Its run settings, skill, trigger and metadata apply as an import applies them.
        var settings = (await host.Services.GetRequiredService<NhAssistantRunConfigurationStore>().FindRunSettingsAsync(Kickoff, CancellationToken.None))!;
        Assert.Equal((5, PackagePrincipalFactory.Account), (settings.RetryCount, settings.ServiceAccountActorId));
        var configuration = (await ConfigurationAsync(host, service => service.GetAsync(Kickoff)))!;
        var skill = Assert.Single(configuration.DataSkills);
        Assert.Equal((2, NewInstructions, "The trial starts projects carefully"), (skill.Version, skill.Instructions, skill.Reason));
        Assert.Contains(configuration.DataTriggers, trigger => trigger.TriggerId == TrialTrigger);
        // The trigger of the earlier version stays, local.
        var earlier = Assert.Single(configuration.DataTriggers, trigger => trigger.TriggerId == "kickoff-on-change");
        Assert.Null(earlier.Package);
        var metadata = Assert.Single(await host.Services.GetRequiredService<NhAssistantAgentMetadataStore>().ListAsync(Kickoff, CancellationToken.None));
        Assert.Contains("CC-200", metadata.ValueJson);

        // The live agent is linked to the variant's package version with its locks and left the other.
        var link = (await PackagesAsync(host, packages => packages.GetLinkAsync(Kickoff)))!;
        Assert.Equal((NextDesks, 2), (link.PackageId, link.PackageVersion));
        Assert.Equal(
            ["agent", "metadata:cost-center", "runSettings", "skill:kickoff", "trigger:" + TrialTrigger],
            link.Parts.Select(part => part.Part).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["instructions"], link.Parts.Single(part => part.Part == "agent").LockedFields);
        Assert.Equal(["retryCount"], link.Parts.Single(part => part.Part == "runSettings").LockedFields);
        Assert.All(link.Parts, part => Assert.False(part.ChangedLocally));
        var next = (await PackagesAsync(host, packages => packages.GetPackageAsync(NextDesks)))!;
        Assert.Equal(2, next.Version);
        Assert.Equal([Kickoff], next.AgentIds);
        Assert.Equal(next.Hash, promoted.Data.Package!.Hash);
        var desks = (await PackagesAsync(host, packages => packages.GetPackageAsync(Desks)))!;
        Assert.Equal(1, desks.Version);
        Assert.Equal([Intake], desks.AgentIds);

        // The package's lock holds on the live agent.
        var locked = await DefinitionsAsync(host, definitions => definitions.UpdateAsync(
            Kickoff,
            new NhAssistantAgentDefinitionRequest("Kickoff desk", "Starts projects carefully.", "Start right away.", ["projects.*"], NhAiAutonomyLevel.Execute),
            agent.Version,
            Admin));
        Assert.Equal(["instructions:locked"], Fields(locked));

        // The variant is gone with its links.
        Assert.Null(await host.Services.GetRequiredService<NhAssistantAdminStore>().FindAgentAsync(Trial, CancellationToken.None));
        Assert.Empty(await host.Services.GetRequiredService<NhAssistantAgentPackageStore>().ListLinksAsync(Trial, CancellationToken.None));

        // One audit event records the promotion, after the change events of the parts it wrote.
        var events = host.Business.Events.Skip(before).ToArray();
        var audit = Assert.Single(events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentShadowVariantPromoted);
        Assert.Equal(
            (Kickoff, Kickoff, Trial, agent.Version, NhAssistantShadowVariantPromotions.PromotedAsPackageVersion, NextDesks, 2, next.Hash, Admin),
            (audit.AgentId, audit.LiveAgentId, audit.ObjectId, audit.AgentVersion, audit.ResultCode, audit.PackageId, audit.PackageVersion, audit.PackageHash, audit.ActorId));
        Assert.Same(audit, events[^1]);
        Assert.DoesNotContain(events, evt => evt.Kind is NhAssistantAuditEventKind.AdminAgentPackageImported
            or NhAssistantAuditEventKind.AdminAgentPackageUnlinked
            or NhAssistantAuditEventKind.AdminAgentDeleted);
        Assert.Contains(events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentUpdated && evt.ObjectId == Kickoff);
        Assert.Equal(
            NhAssistantPackagePartOutcomes.Created,
            promoted.Data.Package.Parts.Single(part => part.Part == "trigger:" + TrialTrigger).Outcome);
    }

    /// <summary>
    /// FB33: with <c>UncarriedParts = Relink</c> the live agent's linked parts that the variant does not
    /// carry stay as they are and stay linked, to the variant's package version, with the locks and the
    /// hash of their link, reported as <c>kept-linked</c>.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task With_relink_the_parts_the_variant_does_not_carry_stay_linked_to_its_package_version_with_their_locks(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        // The earlier version locks the event of the trigger that the next version no longer carries.
        await ImportAsync(
            host,
            AssistantAgentPackageTests.Desks(locked: true, kickoff: agent => agent with
            {
                Locks = new Dictionary<string, IReadOnlyList<string>>(agent.Locks!) { ["trigger:kickoff-on-change"] = ["event"] }
            }),
            AssistantAgentPackageTests.Options());
        await ImportAsync(host, NextDesksPackage(), TrialOptions());
        var earlier = (await PackagesAsync(host, packages => packages.GetLinkAsync(Kickoff)))!;
        // The hand-off, which no package locks, changed here after its import.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var changed = await scope.ServiceProvider.GetRequiredService<INhAssistantDataHandOffs>().SaveAsync(
                Kickoff,
                "project",
                new NhAssistantDataHandOffRequest([Intake], "Starts ready projects.", ExpectedVersion: 1),
                Admin);
            Assert.True(changed.Success, string.Join(", ", changed.AllErrorMessages));
        }
        var live = (await DirectoryAsync(host, directory => directory.GetAsync(Kickoff)))!;

        var promoted = await PromoteAsync(host, Trial, new NhAssistantShadowVariantPromotionRequest(live.Version, ExpectedVariantVersion: 1)
        {
            AsPackageVersion = true,
            UncarriedParts = NhAssistantUncarriedParts.Relink
        });

        Assert.True(promoted.Success, string.Join(", ", promoted.AllErrorMessages));
        Assert.Equal(
            ["handOff:project changed-locally ", "trigger:kickoff-on-change unchanged event"],
            promoted.Data!.Package!.Parts
                .Where(part => part.Outcome == NhAssistantPackagePartOutcomes.KeptLinked)
                .Select(part => part.Part + (part.ChangedLocally ? " changed-locally " : " unchanged ") + string.Join(",", part.LockedFields))
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(promoted.Data.Package.Parts, part => part.Outcome == NhAssistantPackagePartOutcomes.LeftBehind);
        Assert.Equal(["kickoff"], promoted.Data.MovedSkills);

        // They stay as they are, linked to the variant's package version with the hash of their link.
        var link = (await PackagesAsync(host, packages => packages.GetLinkAsync(Kickoff)))!;
        Assert.Equal((NextDesks, 2), (link.PackageId, link.PackageVersion));
        Assert.Equal(
            ["agent", "handOff:project", "metadata:cost-center", "runSettings", "skill:kickoff", "trigger:kickoff-on-change", "trigger:" + TrialTrigger],
            link.Parts.Select(part => part.Part).Order(StringComparer.Ordinal).ToArray());
        var trigger = link.Parts.Single(part => part.Part == "trigger:kickoff-on-change");
        var earlierTrigger = earlier.Parts.Single(part => part.Part == "trigger:kickoff-on-change");
        Assert.Equal((NextDesks, 2, earlierTrigger.Hash, false, Admin), (trigger.PackageId, trigger.PackageVersion, trigger.Hash, trigger.ChangedLocally, trigger.LinkedBy));
        Assert.Equal(["event"], trigger.LockedFields);
        var handOff = link.Parts.Single(part => part.Part == "handOff:project");
        Assert.Equal((earlier.Parts.Single(part => part.Part == "handOff:project").Hash, true), (handOff.Hash, handOff.ChangedLocally));
        var configuration = (await ConfigurationAsync(host, service => service.GetAsync(Kickoff)))!;
        var kept = Assert.Single(configuration.DataTriggers, item => item.TriggerId == "kickoff-on-change");
        Assert.Equal((1, "project.changed", NextDesks), (kept.Version, kept.Event, kept.Package!.PackageId));

        // The package version holds them; the earlier package no longer links the live agent.
        var next = (await PackagesAsync(host, packages => packages.GetPackageAsync(NextDesks)))!;
        Assert.Contains("trigger:kickoff-on-change", Assert.Single(next.Agents).Parts.Select(part => part.Part));
        var desks = (await PackagesAsync(host, packages => packages.GetPackageAsync(Desks)))!;
        Assert.Equal([Intake], desks.AgentIds);
        Assert.DoesNotContain(desks.Agents, agent => agent.AgentId == Kickoff);

        // The lock of the kept part keeps holding.
        var locked = await ConfigurationAsync(host, service => service.SaveTriggerAsync(
            Kickoff,
            "kickoff-on-change",
            new NhAssistantDataTriggerRequest("event", "kickoff", "project.closed", ExpectedVersion: 1),
            Admin));
        Assert.Equal(["event:locked"], Fields(locked));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_variant_with_an_unlinked_part_is_refused_and_the_promotion_without_the_option_is_unchanged(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await ImportAsync(host, AssistantAgentPackageTests.Desks(locked: true), AssistantAgentPackageTests.Options());
        await ImportAsync(host, NextDesksPackage(), TrialOptions());
        var live = (await DirectoryAsync(host, directory => directory.GetAsync(Kickoff)))!;
        var asVersion = new NhAssistantShadowVariantPromotionRequest(live.Version) { AsPackageVersion = true };

        // Without the option a package holds its variant, as before.
        Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await PromoteAsync(host, Trial, new NhAssistantShadowVariantPromotionRequest(live.Version))));
        // The skills are parts of the package version; they cannot move on their own as well.
        Assert.Equal(["moveSkills:not-allowed"], Fields(await PromoteAsync(host, Trial, asVersion with { MoveSkills = true })));
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await PromoteAsync(host, Trial, asVersion with { ExpectedVariantVersion = 4 })));
        // Only a package version leaves parts behind that a link could keep (FB33).
        Assert.Equal(
            ["uncarriedParts:not-allowed"],
            Fields(await PromoteAsync(host, Trial, new NhAssistantShadowVariantPromotionRequest(live.Version) { UncarriedParts = NhAssistantUncarriedParts.Relink })));
        Assert.Equal(["uncarriedParts:invalid"], Fields(await PromoteAsync(host, Trial, asVersion with { UncarriedParts = (NhAssistantUncarriedParts)7 })));

        // A part an administrator unlinked is no longer the package's version.
        Assert.True((await PackagesAsync(host, packages => packages.UnlinkAsync(Trial, "trigger:" + TrialTrigger, Admin))).Success);
        Assert.Equal(NhAssistantErrorCodes.PackageNotLinked, Code(await PromoteAsync(host, Trial, asVersion)));

        // Nothing changed.
        Assert.Equal(live.Version, (await DirectoryAsync(host, directory => directory.GetAsync(Kickoff)))!.Version);
        Assert.NotNull(await DirectoryAsync(host, directory => directory.GetAsync(Trial)));
        Assert.Equal(Desks, (await PackagesAsync(host, packages => packages.GetLinkAsync(Kickoff)))!.PackageId);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentShadowVariantPromoted);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_part_the_import_refuses_refuses_the_promotion_and_an_agent_in_code_keeps_its_run_settings(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        // The variant locks the profile, which an agent in code cannot lock.
        await ImportAsync(host, NextPlannerPackage(1, ["profile"]), new NhAssistantAgentPackageImportOptions());
        var live = (await DirectoryAsync(host, directory => directory.GetAsync(Planner)))!;
        var before = host.Business.Events.Count;

        var refused = await PromoteAsync(host, PlannerTrial, new NhAssistantShadowVariantPromotionRequest(live.Version) { AsPackageVersion = true });

        Assert.Equal(NhAssistantErrorCodes.PackageRefused, Code(refused));
        Assert.Equal(["parts.agent.locks:invalid", "parts:invalid"], Fields(refused));
        Assert.Equal(live.Version, (await DirectoryAsync(host, directory => directory.GetAsync(Planner)))!.Version);
        Assert.NotNull(await DirectoryAsync(host, directory => directory.GetAsync(PlannerTrial)));
        Assert.Equal(before, host.Business.Events.Count);

        // The next version of the variant's package locks the instructions, which it can.
        await ImportAsync(host, NextPlannerPackage(2, ["instructions"]), new NhAssistantAgentPackageImportOptions());
        var variant = (await DirectoryAsync(host, directory => directory.GetAsync(PlannerTrial)))!;
        var promoted = await PromoteAsync(host, PlannerTrial, new NhAssistantShadowVariantPromotionRequest(live.Version, ExpectedVariantVersion: variant.Version) { AsPackageVersion = true });

        Assert.True(promoted.Success, string.Join(", ", promoted.AllErrorMessages));
        var agent = promoted.Data!.Agent;
        Assert.Equal((true, live.DisplayName, "Plans carefully.", "Plan carefully."), (agent.IsOverridden, agent.DisplayName, agent.Description, agent.Instructions));
        var configuration = (await ConfigurationAsync(host, service => service.GetAsync(Planner)))!;
        // An agent in code keeps its run configuration in code; the variant's run settings went with it.
        Assert.Null(configuration.RunSettings);
        Assert.NotNull(configuration.CodeRunSettings);
        Assert.Equal("review", Assert.Single(configuration.DataSkills).SkillId);
        var link = (await PackagesAsync(host, packages => packages.GetLinkAsync(Planner)))!;
        Assert.Equal((NextPlanner, 2), (link.PackageId, link.PackageVersion));
        Assert.Equal(["agent", "skill:review"], link.Parts.Select(part => part.Part).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["instructions"], link.Parts.Single(part => part.Part == "agent").LockedFields);
        Assert.Equal([Planner], (await PackagesAsync(host, packages => packages.GetPackageAsync(NextPlanner)))!.AgentIds);
        Assert.Null(await DirectoryAsync(host, directory => directory.GetAsync(PlannerTrial)));
    }

    [Fact]
    public async Task Only_administrators_promote_a_variant_as_the_packages_version_through_the_endpoint()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, PackagePrincipalFactory>());
                // The endpoints store definitions only; this host runs no background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: Configure,
            provider: AssistantTestProvider.PostgreSql);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var packages = scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>();
            Assert.True((await packages.ImportAsync(AssistantAgentPackageTests.Desks(locked: true), AssistantAgentPackageTests.Options(), Admin)).Success);
            Assert.True((await packages.ImportAsync(NextDesksPackage(), TrialOptions(), Admin)).Success);
        }
        using var admin = app.CreateClient(Admin, admin: true);
        using var reader = app.CreateClient("reader-1");
        var version = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .Single(agent => agent.GetProperty("id").GetString() == Kickoff)
            .GetProperty("version").GetInt32();
        var path = $"/api/assistant/admin/agents/{Trial}/promote";
        // The list of variants names the package a data variant is linked to.
        var listed = Assert.Single((await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{Kickoff}/shadow-variants"))).GetProperty("variants").EnumerateArray());
        Assert.Equal((Trial, NextDesks, 2), (listed.GetProperty("agentId").GetString(), listed.GetProperty("packageId").GetString(), listed.GetProperty("packageVersion").GetInt32()));

        using (var forbidden = await reader.PostAsJsonAsync(path, new { expectedVersion = version, asPackageVersion = true }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        using (var both = await admin.PostAsJsonAsync(path, new { expectedVersion = version, asPackageVersion = true, moveSkills = true }))
        {
            Assert.Equal("not-allowed", (await ReadJsonAsync(both, HttpStatusCode.BadRequest)).GetProperty("errors").GetProperty("moveSkills")[0].GetString());
        }
        // FB33: uncarriedParts is unlink or relink, and relink only for a package version.
        using (var unknown = await admin.PostAsJsonAsync(path, new { expectedVersion = version, asPackageVersion = true, uncarriedParts = "keep" }))
        {
            Assert.Equal("invalid", (await ReadJsonAsync(unknown, HttpStatusCode.BadRequest)).GetProperty("errors").GetProperty("uncarriedParts")[0].GetString());
        }
        using (var notAllowed = await admin.PostAsJsonAsync(path, new { expectedVersion = version, uncarriedParts = "relink" }))
        {
            Assert.Equal("not-allowed", (await ReadJsonAsync(notAllowed, HttpStatusCode.BadRequest)).GetProperty("errors").GetProperty("uncarriedParts")[0].GetString());
        }

        var promoted = await ReadJsonAsync(await admin.PostAsJsonAsync(path, new { expectedVersion = version, expectedVariantVersion = 1, asPackageVersion = true, uncarriedParts = "relink" }));

        Assert.Equal((Kickoff, version + 1), (promoted.GetProperty("agent").GetProperty("id").GetString(), promoted.GetProperty("agent").GetProperty("version").GetInt32()));
        Assert.Equal(NextDesks, promoted.GetProperty("agent").GetProperty("package").GetProperty("packageId").GetString());
        var package = promoted.GetProperty("package");
        Assert.Equal((NextDesks, 2), (package.GetProperty("packageId").GetString(), package.GetProperty("packageVersion").GetInt32()));
        Assert.Contains(package.GetProperty("parts").EnumerateArray(), part => part.GetProperty("part").GetString() == "skill:kickoff"
            && part.GetProperty("outcome").GetString() == NhAssistantPackagePartOutcomes.Changed);
        // The trigger of the earlier version stays linked to the package version.
        Assert.Contains(package.GetProperty("parts").EnumerateArray(), part => part.GetProperty("part").GetString() == "trigger:kickoff-on-change"
            && part.GetProperty("outcome").GetString() == NhAssistantPackagePartOutcomes.KeptLinked);
        Assert.Contains(
            promoted.GetProperty("agent").GetProperty("package").GetProperty("parts").EnumerateArray(),
            part => part.GetProperty("part").GetString() == "trigger:kickoff-on-change");
    }

    // ----- Host and helpers -----

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, PackagePrincipalFactory>());
            },
            assistantBuilder: Configure);
    }

    /// <summary>
    /// The building blocks of the package tests, with shadow variants as data of the agent in code
    /// too.
    /// </summary>
    private static void Configure(NhAssistantBuilder assistant)
    {
        AssistantAgentPackageTests.Configure(assistant);
        assistant.UseShadowVariantsAsData(variants => variants.Of(Planner).OfAgentsCreatedInAdministration());
    }

    /// <summary>
    /// The next version of the kickoff desk, waiting as a shadow variant in a package of its own: other
    /// instructions, run settings, a changed skill, a trigger and metadata, each linked, with locks.
    /// </summary>
    private static NhAssistantAgentPackage NextDesksPackage()
    {
        return new NhAssistantAgentPackage(
            NhAssistantAgentPackageFormat.Name,
            NhAssistantAgentPackageFormat.Version,
            new NhAssistantAgentPackageInfo(NextDesks, 2),
            [
                new NhAssistantPackageAgent(
                    Trial,
                    NhAssistantAgentSources.Admin,
                    new NhAssistantPackageAgentDefinition("Kickoff trial", "Starts projects carefully.", "Explain the plan before you start the project.", ToolSelectors: ["projects.*"], Autonomy: "execute", ShadowOf: Kickoff),
                    new NhAssistantPackageRunSettings(NhAssistantAccountabilities.ServiceAccount, RetryCount: 5),
                    Skills: [new NhAssistantPackageSkill("kickoff", "Starts a project that is ready.", NewInstructions, Tools: ["projects.*"])],
                    Triggers: [new NhAssistantPackageTrigger(TrialTrigger, NhAssistantDataTriggerSources.Event, Skill: "kickoff", Event: "project.changed", Filters: ["not-draft"])],
                    Metadata: new Dictionary<string, JsonElement> { [AssistantAgentPackageTests.CostCenter] = JsonSerializer.SerializeToElement(new { code = "CC-200" }) },
                    Locks: new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["agent"] = ["instructions"],
                        ["skill:kickoff"] = ["instructions"],
                        ["runSettings"] = ["retryCount"]
                    })
            ]);
    }

    /// <summary>
    /// A shadow variant of the agent in code in a package of its own, locking <paramref name="agentLocks"/>.
    /// </summary>
    private static NhAssistantAgentPackage NextPlannerPackage(int version, IReadOnlyList<string> agentLocks)
    {
        return new NhAssistantAgentPackage(
            NhAssistantAgentPackageFormat.Name,
            NhAssistantAgentPackageFormat.Version,
            new NhAssistantAgentPackageInfo(NextPlanner, version),
            [
                new NhAssistantPackageAgent(
                    PlannerTrial,
                    NhAssistantAgentSources.Admin,
                    new NhAssistantPackageAgentDefinition("Planner trial", "Plans carefully.", "Plan carefully.", ToolSelectors: ["projects.*"], Autonomy: "propose", ShadowOf: Planner),
                    new NhAssistantPackageRunSettings(NhAssistantAccountabilities.Agent),
                    Skills: [new NhAssistantPackageSkill("review", "Reviews a plan.", "Review the plan.", Tools: ["projects.*"])],
                    Locks: new Dictionary<string, IReadOnlyList<string>> { ["agent"] = agentLocks })
            ]);
    }

    private static NhAssistantAgentPackageImportOptions TrialOptions()
    {
        return new NhAssistantAgentPackageImportOptions(new Dictionary<string, string> { [Trial] = PackagePrincipalFactory.OtherAccount });
    }

    private static async Task ImportAsync(AssistantTestHost host, NhAssistantAgentPackage package, NhAssistantAgentPackageImportOptions options)
    {
        var imported = await PackagesAsync(host, packages => packages.ImportAsync(package, options, Admin));
        Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));
    }

    private static Task<TaskResult<NhAssistantShadowVariantPromotionDto>> PromoteAsync(
        AssistantTestHost host,
        string variantId,
        NhAssistantShadowVariantPromotionRequest request)
    {
        return DefinitionsAsync(host, definitions => definitions.PromoteShadowVariantAsync(variantId, request, Admin));
    }

    private static async Task<T> PackagesAsync<T>(AssistantTestHost host, Func<INhAssistantAgentPackages, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>());
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

    private static async Task<T> ConfigurationAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {text}");
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
    }
}
