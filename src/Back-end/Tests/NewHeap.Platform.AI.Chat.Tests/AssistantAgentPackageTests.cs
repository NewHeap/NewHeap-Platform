using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Agent packages (S7-2 of the agent runs adoption plan): agents with their configuration as data
/// exported as one versioned package and imported on another host through the save validators, in one
/// transaction, with locks every save path enforces until an administrator unlinks a part.
/// </summary>
public sealed class AssistantAgentPackageTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    internal const string Intake = "project-intake-desk";
    internal const string Kickoff = "project-kickoff-desk";
    internal const string Trial = "project-kickoff-trial";
    internal const string Planner = "project-planner";
    internal const string PackageId = "project-desks";
    internal const string Admin = "admin-1";
    internal const string CostCenter = "cost-center";
    internal const string Group = "project-desks";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_exported_package_imports_on_another_host_to_the_same_configuration(AssistantTestProvider provider)
    {
        await using var source = await CreateHostAsync(provider);
        await SeedDesksAsync(source);
        var locks = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            [Kickoff] = new Dictionary<string, IReadOnlyList<string>>
            {
                ["agent"] = ["instructions"],
                ["skill:kickoff"] = ["instructions"]
            }
        };

        var exported = await PackagesAsync(source, packages => packages.ExportAsync(
            [Intake, Kickoff],
            new NhAssistantAgentPackageExportOptions(PackageId, Description: "The project desks.", Locks: locks),
            Admin));

        Assert.True(exported.Success, string.Join(", ", exported.AllErrorMessages));
        var package = exported.Data!.Package;
        Assert.Equal((PackageId, 1, NhAssistantAgentPackageFormat.Name), (package.Package.Id, package.Package.Version, package.Format));
        Assert.Empty(exported.Data.Warnings);
        var kickoff = package.Agents.Single(agent => agent.Id == Kickoff);
        Assert.Equal(["agent", "handOff:project", "runSettings", "skill:kickoff", "trigger:kickoff-on-change"], kickoff.Hashes!.Keys);
        Assert.Equal(["instructions"], kickoff.Locks!["skill:kickoff"]);
        Assert.Equal("service-account", kickoff.RunSettings!.Accountability);
        // The technical account never travels; neither do history, run state or MCP servers.
        Assert.DoesNotContain(PackagePrincipalFactory.Account, NhAssistantAgentPackageFormat.Write(package), StringComparison.Ordinal);
        var intake = package.Agents.Single(agent => agent.Id == Intake);
        Assert.Equal([Group], intake.Groups);
        Assert.Equal("CC-100", intake.Metadata![CostCenter].GetProperty("code").GetString());
        Assert.Contains(source.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentPackageExported
                                                      && evt.PackageId == PackageId
                                                      && evt.PackageVersion == 1
                                                      && evt.PackageHash == exported.Data.Hash
                                                      && evt.PackageAgentCount == 2);

        // A reference to an agent outside the selection is a warning (PK18).
        var alone = await PackagesAsync(source, packages => packages.ExportAsync([Kickoff], new NhAssistantAgentPackageExportOptions(), Admin));
        var warning = Assert.Single(alone.Data!.Warnings);
        Assert.Equal((Kickoff, "handOff:project", NhAssistantAgentPackageWarnings.HandOffSource, Intake), (warning.AgentId, warning.Part, warning.Code, warning.ReferencedAgentId));

        // The JSON travels; the other host reads it strictly and imports it as the save validators allow.
        await using var target = await CreateHostAsync(provider);
        var read = NhAssistantAgentPackageFormat.Read(NhAssistantAgentPackageFormat.Write(package));
        Assert.True(read.Success);
        var options = new NhAssistantAgentPackageImportOptions(new Dictionary<string, string> { [Kickoff] = PackagePrincipalFactory.Account });
        var preview = await PackagesAsync(target, packages => packages.PreviewImportAsync(read.Data!, options));
        Assert.True(preview.Data!.Importable, Describe(preview.Data));
        Assert.Null(preview.Data.InstalledVersion);
        Assert.All(preview.Data.Parts, part => Assert.Equal(NhAssistantPackagePartOutcomes.Created, part.Outcome));

        var imported = await PackagesAsync(target, packages => packages.ImportAsync(read.Data!, options, Admin));

        Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));
        Assert.Equal(exported.Data.Hash, imported.Data!.Hash);
        var configuration = (await ConfigurationAsync(target, service => service.GetAsync(Kickoff)))!;
        Assert.True(configuration.RunSettings!.InEffect);
        Assert.Equal(PackagePrincipalFactory.Account, configuration.RunSettings.ServiceAccountActorId);
        var skill = Assert.Single(configuration.DataSkills);
        Assert.True(skill.InEffect, skill.Problem);
        Assert.Equal(["instructions"], skill.Package!.LockedFields);
        Assert.Equal((PackageId, 1, false), (skill.Package.PackageId, skill.Package.PackageVersion, skill.Package.ChangedLocally));
        Assert.True(Assert.Single(configuration.DataTriggers).InEffect);
        Assert.Contains(State(target).AgentRuns[Kickoff].HandOffs, handOff => handOff.IsData && handOff.Sources.Contains(Intake));
        Assert.Single(State(target).AgentRuns[Intake].Consultations, consultation => consultation.IsData);

        // Exported again, the other host's package has the same parts.
        var again = await PackagesAsync(target, packages => packages.ExportAsync([Intake, Kickoff], new NhAssistantAgentPackageExportOptions(Locks: locks), Admin));
        Assert.Equal((PackageId, 2), (again.Data!.Package.Package.Id, again.Data.Package.Package.Version));
        foreach (var agent in package.Agents)
        {
            Assert.Equal(agent.Hashes, again.Data.Package.Agents.Single(item => item.Id == agent.Id).Hashes);
        }

        // The same version again changes nothing.
        var same = await PackagesAsync(target, packages => packages.PreviewImportAsync(read.Data!, options));
        Assert.Equal(1, same.Data!.InstalledVersion);
        Assert.All(same.Data.Parts, part => Assert.Equal(NhAssistantPackagePartOutcomes.Unchanged, part.Outcome));

        // Every part is audited as its save, then the package; content never reaches the audit.
        var events = target.Business.Events;
        Assert.Equal(2, events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentCreated));
        Assert.Contains(events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSkillDefinitionChanged && evt.AgentId == Kickoff && evt.ResultCode == "created");
        Assert.Contains(events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentHandOffDefinitionChanged && evt.AgentId == Kickoff);
        Assert.Contains(events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentConsultationDefinitionChanged && evt.AgentId == Intake);
        var importedEvent = Assert.Single(events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentPackageImported);
        Assert.Equal((PackageId, PackageId, (int?)1, exported.Data.Hash, (int?)2, "created"), (importedEvent.ObjectId, importedEvent.PackageId, importedEvent.PackageVersion, importedEvent.PackageHash, importedEvent.PackageAgentCount, importedEvent.ResultCode));
        Assert.DoesNotContain(events, evt => evt.ResultCode?.Contains("Start the project", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_part_the_validators_refuse_refuses_the_whole_package_and_nothing_is_imported(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var package = Desks(
            intake: agent => agent with
            {
                Consultations = [agent.Consultations![0] with { AskerPolicy = "unknown-policy" }],
                Groups = ["unknown-group"],
                Metadata = new Dictionary<string, JsonElement>(agent.Metadata!) { ["unknown-entry"] = JsonSerializer.SerializeToElement(1) }
            },
            kickoff: agent => agent with
            {
                Triggers =
                [
                    agent.Triggers![0] with { Event = "project.renamed" },
                    new NhAssistantPackageTrigger("kickoff-on-close", "event", Skill: "kickoff", Event: "project.changed", Filters: ["unknown-filter"])
                ],
                HandOffs = [agent.HandOffs![0] with { Sources = ["gone-desk"] }]
            },
            more:
            [
                new NhAssistantPackageAgent(
                    "project-review-desk",
                    NhAssistantAgentSources.Admin,
                    new NhAssistantPackageAgentDefinition("Review desk", "Reviews projects.", "Review the project.", ToolSelectors: ["projects.*"], Autonomy: "execute"),
                    new NhAssistantPackageRunSettings(NhAssistantAccountabilities.Agent, ApprovalPolicy: "unknown-policy"))
            ]);

        var preview = (await PackagesAsync(host, packages => packages.PreviewImportAsync(package, Options()))).Data!;

        Assert.False(preview.Importable);
        Assert.Equal(NhAssistantFieldErrors.NotFound, Error(preview, "project-review-desk", "runSettings", "approvalPolicy"));
        Assert.Equal(NhAssistantFieldErrors.NotFound, Error(preview, Intake, "consultation:project", "askerPolicy"));
        Assert.Equal(NhAssistantFieldErrors.NotFound, Error(preview, Intake, "agent", "groups"));
        Assert.Equal(NhAssistantAdminErrorCodes.MetadataEntryNotFound, Part(preview, Intake, "metadata:unknown-entry").Code);
        Assert.Equal(NhAssistantFieldErrors.NotFound, Error(preview, Kickoff, "trigger:kickoff-on-change", "event"));
        Assert.Equal(NhAssistantErrorCodes.TriggerFilterNotFound, Part(preview, Kickoff, "trigger:kickoff-on-close").Code);
        Assert.Equal(NhAssistantFieldErrors.NotFound, Error(preview, Kickoff, "trigger:kickoff-on-close", "filters"));
        Assert.Equal(NhAssistantFieldErrors.NotFound, Error(preview, Kickoff, "handOff:project", "sources"));
        Assert.Equal(NhAssistantPackagePartOutcomes.Created, Part(preview, Kickoff, "skill:kickoff").Outcome);

        var imported = await PackagesAsync(host, packages => packages.ImportAsync(package, Options(), Admin));

        Assert.Equal(NhAssistantErrorCodes.PackageRefused, Code(imported));
        var errors = NhAssistantResults.FieldErrorsOf(imported);
        Assert.Equal([NhAssistantFieldErrors.NotFound], errors[Kickoff + "/trigger:kickoff-on-change/event"]);
        Assert.Equal([NhAssistantAdminErrorCodes.MetadataEntryNotFound], errors[Intake + "/metadata:unknown-entry"]);
        Assert.Null(await FindAgentAsync(host, Intake));
        Assert.Null(await FindAgentAsync(host, Kickoff));
        Assert.Null(await host.Services.GetRequiredService<NhAssistantAgentPackageStore>().FindPackageAsync(PackageId, CancellationToken.None));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind is NhAssistantAuditEventKind.AdminAgentCreated or NhAssistantAuditEventKind.AdminAgentPackageImported);

        // Another agent with one of the package's ids refuses the whole package (PK5).
        await ConfigurationAsync(host, service => service.CreateAgentAsync(
            new NhAssistantAgentRequest(Intake, "Local intake", "Another intake.", "Look at it.", ["projects.*"], NhAiAutonomyLevel.Execute),
            Admin));
        var taken = (await PackagesAsync(host, packages => packages.PreviewImportAsync(Desks(), Options()))).Data!;
        Assert.Equal(NhAssistantAdminErrorCodes.AgentExists, Part(taken, Intake, "agent").Code);
        Assert.Equal(NhAssistantErrorCodes.PackageRefused, Code(await PackagesAsync(host, packages => packages.ImportAsync(Desks(), Options(), Admin))));
        Assert.Null(await FindAgentAsync(host, Kickoff));

        // The transaction over the stores keeps nothing when a write fails halfway.
        var store = host.Services.GetRequiredService<NhAssistantAgentPackageStore>();
        var outcome = await store.ImportAsync(
            async (context, cancellationToken) =>
            {
                Assert.True(await NhAssistantAdminStore.AddAgentAsync(context, NewAgentRow(Kickoff), [], null, cancellationToken));
                return false;
            },
            new AssistantAgentPackage { Id = PackageId, Version = 1, FormatVersion = 1, Hash = new string('0', 64), ImportedBy = Admin },
            [Kickoff],
            [],
            [],
            null,
            CancellationToken.None);
        Assert.Equal(NhAssistantStoreOutcome.Conflict, outcome);
        Assert.Null(await FindAgentAsync(host, Kickoff));
        Assert.Null(await store.FindPackageAsync(PackageId, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Agents_of_one_package_refer_to_each_other_cycles_included(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        // The desks hand work to each other, and a shadow variant of the kickoff desk travels with it.
        var package = Desks(
            intake: agent => agent with { HandOffs = [new NhAssistantPackageHandOff("project", [Kickoff], "Takes projects back in.")] },
            more:
            [
                new NhAssistantPackageAgent(
                    Trial,
                    NhAssistantAgentSources.Admin,
                    new NhAssistantPackageAgentDefinition("Kickoff trial", "Tries the kickoff.", "Try to start the project.", ToolSelectors: ["projects.*"], Autonomy: "execute", ShadowOf: Kickoff),
                    new NhAssistantPackageRunSettings(NhAssistantAccountabilities.Agent))
            ]);

        var preview = (await PackagesAsync(host, packages => packages.PreviewImportAsync(package, Options()))).Data!;
        Assert.True(preview.Importable, Describe(preview));

        var imported = await PackagesAsync(host, packages => packages.ImportAsync(package, Options(), Admin));

        Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));
        Assert.Contains(State(host).AgentRuns[Kickoff].HandOffs, handOff => handOff.IsData && handOff.Sources.Contains(Intake));
        Assert.Contains(State(host).AgentRuns[Intake].HandOffs, handOff => handOff.IsData && handOff.Sources.Contains(Kickoff));
        Assert.Equal(Kickoff, State(host).AgentRuns[Trial].ShadowOfAgentId);

        // A reference to an agent outside the package must exist on the host.
        var outside = Desks(kickoff: agent => agent with { HandOffs = [agent.HandOffs![0] with { Sources = [Intake, "gone-desk"] }] }, version: 2);
        var refused = (await PackagesAsync(host, packages => packages.PreviewImportAsync(outside, Options()))).Data!;
        Assert.Equal(NhAssistantFieldErrors.NotFound, Error(refused, Kickoff, "handOff:project", "sources"));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_import_names_the_technical_account_and_it_must_be_able_to_sign_in(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);

        var missing = (await PackagesAsync(host, packages => packages.PreviewImportAsync(Desks(), new NhAssistantAgentPackageImportOptions()))).Data!;
        var locked = (await PackagesAsync(host, packages => packages.PreviewImportAsync(Desks(), Options(PackagePrincipalFactory.CannotSignIn)))).Data!;

        Assert.Equal(NhAssistantFieldErrors.Required, Error(missing, Kickoff, "runSettings", "serviceAccountActorId"));
        Assert.Equal(NhAssistantErrorCodes.ServiceAccountNotFound, Part(locked, Kickoff, "runSettings").Code);
        Assert.True((await PackagesAsync(host, packages => packages.ImportAsync(Desks(), Options(), Admin))).Success);
        Assert.Equal(PackagePrincipalFactory.Account, (await host.Services.GetRequiredService<NhAssistantRunConfigurationStore>().FindRunSettingsAsync(Kickoff, CancellationToken.None))!.ServiceAccountActorId);

        // An upgrade that names no account keeps the one the host has.
        var upgrade = Desks(version: 2, kickoff: agent => agent with { RunSettings = agent.RunSettings! with { RetryCount = 5 } });
        var upgraded = await PackagesAsync(host, packages => packages.ImportAsync(upgrade, new NhAssistantAgentPackageImportOptions(), Admin));
        Assert.True(upgraded.Success, string.Join(", ", upgraded.AllErrorMessages));
        var settings = (await host.Services.GetRequiredService<NhAssistantRunConfigurationStore>().FindRunSettingsAsync(Kickoff, CancellationToken.None))!;
        Assert.Equal((PackagePrincipalFactory.Account, 5, 2), (settings.ServiceAccountActorId, settings.RetryCount, settings.Version));
        Assert.Equal(NhAssistantPackagePartOutcomes.Changed, upgraded.Data!.Parts.Single(part => part.AgentId == Kickoff && part.Part == "runSettings").Outcome);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_upgrade_in_shadow_makes_candidates_that_a_promote_puts_live(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        Assert.True((await PackagesAsync(host, packages => packages.ImportAsync(Desks(locked: true), Options(), Admin))).Success);
        var upgrade = Desks(
            version: 2,
            locked: true,
            kickoff: agent => agent with
            {
                Skills = [agent.Skills![0] with { Instructions = "Start the project once its plan and budget are approved." }],
                Triggers = [agent.Triggers![0] with { DebounceSeconds = 60 }]
            });
        var shadow = Options() with { Apply = NhAssistantSkillApplyModes.Shadow };

        var preview = (await PackagesAsync(host, packages => packages.PreviewImportAsync(upgrade, shadow))).Data!;
        Assert.Equal(NhAssistantPackagePartOutcomes.Changed, Part(preview, Kickoff, "skill:kickoff").Outcome);
        Assert.Equal(NhAssistantPackagePartOutcomes.Unchanged, Part(preview, Kickoff, "handOff:project").Outcome);

        var imported = await PackagesAsync(host, packages => packages.ImportAsync(upgrade, shadow, Admin));

        Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));
        Assert.Equal(2, imported.Data!.Parts.Single(part => part.Part == "skill:kickoff").CandidateVersion);
        var configuration = (await ConfigurationAsync(host, service => service.GetAsync(Kickoff)))!;
        var skill = Assert.Single(configuration.DataSkills);
        Assert.Equal((1, 2), (skill.Version, skill.Candidate!.Version));
        Assert.Equal(2, Assert.Single(configuration.DataTriggers).Candidate!.Version);
        // The candidate is the package's version, not a local change.
        Assert.False(skill.Package!.ChangedLocally);

        // A promote puts the package's instructions live, though they are locked; the hand-off stays.
        var promoted = await ConfigurationAsync(host, service => service.PromoteSkillCandidateAsync(Kickoff, "kickoff", 2, Admin));
        Assert.True(promoted.Success, string.Join(", ", promoted.AllErrorMessages));
        Assert.Equal("Start the project once its plan and budget are approved.", promoted.Data!.Instructions);
        Assert.Contains(State(host).AgentRuns[Kickoff].HandOffs, handOff => handOff.IsData);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSkillDefinitionChanged && evt.ResultCode == "candidate");
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentPackageImported && evt.ResultCode == "upgraded");
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Every_save_path_refuses_a_locked_field_and_operations_stay_free(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var package = Desks(
            intake: agent => agent with
            {
                Locks = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["consultation:project"] = ["description"],
                    ["metadata:cost-center"] = ["value"]
                }
            },
            kickoff: agent => agent with
            {
                Locks = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["agent"] = ["instructions"],
                    ["runSettings"] = ["*"],
                    ["skill:kickoff"] = ["instructions"],
                    ["trigger:kickoff-on-change"] = ["event"],
                    ["handOff:project"] = ["sources"]
                }
            });
        Assert.True((await PackagesAsync(host, packages => packages.ImportAsync(package, Options(), Admin))).Success);

        // The agent's definition: a locked field is refused, an unlocked one changes.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var administration = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
            var input = new NhAssistantAgentInput(Kickoff, "Kickoff desk", "Starts projects.", "Start every project at once.", ["projects.*"], [], null, NhAiAutonomyLevel.Execute);
            Assert.Equal("instructions", Locked(await administration.UpdateAsync(input, 1, Admin, CancellationToken.None)));
            Assert.True((await administration.UpdateAsync(input with { Instructions = "Explain why you start the project.", Description = "Starts ready projects." }, 1, Admin, CancellationToken.None)).Success);
            Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await administration.DeleteAsync(Kickoff, Admin, CancellationToken.None)));
            var intakeInput = new NhAssistantAgentInput(Intake, "Intake desk", "Takes in new projects.", "Look at every new project.", ["projects.*", "assistant.handoff.*"], [], null, NhAiAutonomyLevel.Execute)
            {
                Metadata = new Dictionary<string, JsonElement> { [CostCenter] = JsonSerializer.SerializeToElement(new { code = "CC-999" }) }
            };
            Assert.Equal("metadata." + CostCenter, Locked(await administration.UpdateAsync(intakeInput, 1, Admin, CancellationToken.None)));
        }

        // Run settings, skills, triggers and consultations.
        Assert.Equal("retryCount", Locked(await ConfigurationAsync(host, service => service.SaveRunSettingsAsync(
            Kickoff,
            new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.ServiceAccount, PackagePrincipalFactory.Account, RetryCount: 1, ExpectedVersion: 1),
            Admin))));
        Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await ConfigurationAsync(host, service => service.RemoveRunSettingsAsync(Kickoff, 1, Admin))));
        var skill = new NhAssistantDataSkillRequest("Starts a project that is ready.", "Start any project.", ["projects.*"], ExpectedVersion: 1);
        Assert.Equal("instructions", Locked(await ConfigurationAsync(host, service => service.SaveSkillAsync(Kickoff, "kickoff", skill, Admin))));
        Assert.Equal("instructions", Locked(await ConfigurationAsync(host, service => service.SaveSkillAsync(Kickoff, "kickoff", skill with { Apply = NhAssistantSkillApplyModes.Shadow }, Admin))));
        var unlocked = await ConfigurationAsync(host, service => service.SaveSkillAsync(
            Kickoff,
            "kickoff",
            skill with { Instructions = "Start the project when the plan is approved.", Description = "Starts a project that is ready to start." },
            Admin));
        Assert.True(unlocked.Success, string.Join(", ", unlocked.AllErrorMessages));
        // A candidate that bypassed the save cannot be promoted over the lock.
        var store = host.Services.GetRequiredService<NhAssistantRunConfigurationStore>();
        var candidate = (await store.AddSkillVersionAsync(
            SkillRow(Kickoff, "kickoff", "Start any project at all."),
            2,
            CancellationToken.None,
            candidate: true)).Value!;
        Assert.Equal("instructions", Locked(await ConfigurationAsync(host, service => service.PromoteSkillCandidateAsync(Kickoff, "kickoff", candidate.Version, Admin))));
        Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await ConfigurationAsync(host, service => service.RemoveSkillAsync(Kickoff, "kickoff", null, Admin))));
        Assert.Equal("event", Locked(await ConfigurationAsync(host, service => service.SaveTriggerAsync(
            Kickoff,
            "kickoff-on-change",
            new NhAssistantDataTriggerRequest("event", "kickoff", "project.closed", ExpectedVersion: 1),
            Admin))));
        Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await ConfigurationAsync(host, service => service.RemoveTriggerAsync(Kickoff, "kickoff-on-change", null, Admin))));
        Assert.Equal("description", Locked(await ConfigurationAsync(host, service => service.SaveConsultationAsync(
            Intake,
            "project",
            new NhAssistantDataConsultationRequest("Knows everything.", ExpectedVersion: 1),
            Admin))));
        Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await ConfigurationAsync(host, service => service.RemoveConsultationAsync(Intake, "project", null, Admin))));

        // Metadata and hand-offs.
        Assert.Equal("value", Locked(await ConfigurationAsync(host, service => service.SaveMetadataAsync(
            Intake,
            CostCenter,
            new NhAssistantAgentMetadataRequest(JsonSerializer.SerializeToElement(new { code = "CC-999" }), 1),
            Admin))));
        Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await ConfigurationAsync(host, service => service.RemoveMetadataAsync(Intake, CostCenter, 1, Admin))));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var handOffs = scope.ServiceProvider.GetRequiredService<INhAssistantDataHandOffs>();
            Assert.Equal("sources", Locked(await handOffs.SaveAsync(Kickoff, "project", new NhAssistantDataHandOffRequest([Intake, Planner], ExpectedVersion: 1), Admin)));
            Assert.True((await handOffs.SaveAsync(Kickoff, "project", new NhAssistantDataHandOffRequest([Intake], "Starts ready projects.", ExpectedVersion: 1), Admin)).Success);
            Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await handOffs.RemoveAsync(Kickoff, "project", null, Admin)));

            // Operations stay free: shadow modes, states, groups and the technical account.
            var control = scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>();
            Assert.True((await control.SetSkillShadowAsync(Kickoff, "kickoff", false, Admin)).Success);
            Assert.True((await control.PauseAsync(Kickoff, Admin, "Holiday")).Success);
            Assert.True((await control.SetServiceAccountAsync(Kickoff, PackagePrincipalFactory.OtherAccount, Admin)).Success);
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().SetTriggerShadowAsync(Kickoff, "kickoff-on-change", false, Admin)).Success);
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().SetConsultationStateAsync(Intake, "project", false, Admin)).Success);
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentGroups>().SetMembershipAsync(Intake, [], Admin)).Success);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_unlinked_part_stays_local_on_an_upgrade_until_an_import_relinks_it(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        Assert.True((await PackagesAsync(host, packages => packages.ImportAsync(Desks(locked: true), Options(), Admin))).Success);

        Assert.Equal("part", Assert.Single(NhAssistantResults.FieldErrorsOf(await PackagesAsync(host, packages => packages.UnlinkAsync(Kickoff, "history:kickoff", Admin)))).Key);
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await PackagesAsync(host, packages => packages.UnlinkAsync("gone-desk", null, Admin))));
        var unlinked = await PackagesAsync(host, packages => packages.UnlinkAsync(Kickoff, "skill:kickoff", Admin, "Our own wording"));
        Assert.Equal(PackageId, unlinked.Data!.PackageId);
        Assert.Equal(["skill:kickoff"], unlinked.Data.Parts);
        Assert.Equal(NhAssistantErrorCodes.PackageNotLinked, Code(await PackagesAsync(host, packages => packages.UnlinkAsync(Kickoff, "skill:kickoff", Admin))));
        var local = await ConfigurationAsync(host, service => service.SaveSkillAsync(
            Kickoff,
            "kickoff",
            new NhAssistantDataSkillRequest("Starts a project that is ready.", "Start projects our way.", ["projects.*"], ExpectedVersion: 1),
            Admin));
        Assert.True(local.Success, string.Join(", ", local.AllErrorMessages));

        // The next version keeps the local skill, and leaves the trigger it no longer contains.
        var upgrade = Desks(
            version: 2,
            locked: true,
            kickoff: agent => agent with
            {
                Skills = [agent.Skills![0] with { Instructions = "Start the project the package's way." }],
                Triggers = null
            });
        var preview = (await PackagesAsync(host, packages => packages.PreviewImportAsync(upgrade, Options()))).Data!;
        Assert.Equal(NhAssistantPackagePartOutcomes.KeptLocal, Part(preview, Kickoff, "skill:kickoff").Outcome);
        Assert.Equal(NhAssistantPackagePartOutcomes.LeftBehind, Part(preview, Kickoff, "trigger:kickoff-on-change").Outcome);
        Assert.True((await PackagesAsync(host, packages => packages.ImportAsync(upgrade, Options(), Admin))).Success);
        var configuration = (await ConfigurationAsync(host, service => service.GetAsync(Kickoff)))!;
        Assert.Equal("Start projects our way.", Assert.Single(configuration.DataSkills).Instructions);
        Assert.Null(configuration.DataSkills[0].Package);
        Assert.Null(Assert.Single(configuration.DataTriggers).Package);

        // Relinked, the skill takes the package's version again with its lock.
        var relinked = await PackagesAsync(host, packages => packages.ImportAsync(
            upgrade with { Package = upgrade.Package with { Version = 3 } },
            Options() with { Relink = [new NhAssistantPackagePartReference(Kickoff, "skill:kickoff")] },
            Admin));
        Assert.Equal(NhAssistantPackagePartOutcomes.Changed, relinked.Data!.Parts.Single(part => part.Part == "skill:kickoff").Outcome);
        var skill = Assert.Single((await ConfigurationAsync(host, service => service.GetAsync(Kickoff)))!.DataSkills);
        Assert.Equal(("Start the project the package's way.", "instructions"), (skill.Instructions, Assert.Single(skill.Package!.LockedFields)));

        // Unlinked as a whole, the agent is no longer the package's: an upgrade must relink it.
        var whole = await PackagesAsync(host, packages => packages.UnlinkAsync(Kickoff, null, Admin));
        Assert.Contains("skill:kickoff", whole.Data!.Parts);
        Assert.Null((await PackagesAsync(host, packages => packages.GetLinkAsync(Kickoff))));
        var next = upgrade with { Package = upgrade.Package with { Version = 4 } };
        Assert.Equal(NhAssistantAdminErrorCodes.AgentExists, Part((await PackagesAsync(host, packages => packages.PreviewImportAsync(next, Options()))).Data!, Kickoff, "agent").Code);
        Assert.True((await PackagesAsync(host, packages => packages.PreviewImportAsync(next, Options() with { Relink = [new NhAssistantPackagePartReference(Kickoff)] }))).Data!.Importable);
        var audited = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentPackageUnlinked).ToArray();
        Assert.Contains(audited, evt => evt.AgentId == Kickoff && evt.PackagePart == "skill:kickoff" && evt.ResultCode == "unlinked");
        Assert.Contains(audited, evt => evt.AgentId == Kickoff && evt.PackagePart == "trigger:kickoff-on-change" && evt.ResultCode == "left-behind");
        Assert.Contains(audited, evt => evt.AgentId == Kickoff && evt.PackagePart == "*" && evt.PackageId == PackageId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_in_code_travels_as_an_overlay_of_its_data(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var overlay = new NhAssistantPackageAgent(
            Planner,
            NhAssistantAgentSources.Code,
            new NhAssistantPackageAgentDefinition(Description: "Plans projects and their reviews."),
            Skills: [new NhAssistantPackageSkill("review-notes", "Writes review notes.", "Note what the review found.", Tools: ["projects.*"])],
            Metadata: new Dictionary<string, JsonElement> { [CostCenter] = JsonSerializer.SerializeToElement(new { code = "CC-200" }) },
            Locks: new Dictionary<string, IReadOnlyList<string>> { ["agent"] = ["description"] });
        var package = new NhAssistantAgentPackage(NhAssistantAgentPackageFormat.Name, 1, new NhAssistantAgentPackageInfo("planner-overlay", 1), [overlay]);

        var imported = await PackagesAsync(host, packages => packages.ImportAsync(package, new NhAssistantAgentPackageImportOptions(), Admin));

        Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));
        var row = (await host.Services.GetRequiredService<NhAssistantAdminStore>().FindAgentAsync(Planner, CancellationToken.None))!;
        Assert.Equal(("Plans projects and their reviews.", true, NhAssistantAgentSources.Code), (row.Description, row.IsOverridden, row.Source));
        Assert.True(Assert.Single((await ConfigurationAsync(host, service => service.GetAsync(Planner)))!.DataSkills).InEffect);
        var metadata = (await ConfigurationAsync(host, service => service.GetMetadataAsync(Planner))).Data!.Single(entry => entry.Name == CostCenter);
        Assert.Equal(("override", "CC-200"), (metadata.Source, metadata.Value!.Value.GetProperty("code").GetString()));
        Assert.Equal("planner-overlay", metadata.Package!.PackageId);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            // A reset would change the locked description back to the code's.
            Assert.Equal(NhAssistantErrorCodes.PackageLocked, Code(await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().ResetAsync(Planner, Admin, CancellationToken.None)));
        }

        // The import requires the same agent in code, and an agent in code has no run settings as data.
        var missing = package with { Agents = [overlay with { Id = "project-missing-planner" }] };
        var withSettings = package with { Agents = [overlay with { RunSettings = new NhAssistantPackageRunSettings(NhAssistantAccountabilities.Agent) }] };
        var asAdmin = package with { Agents = [overlay with { Source = NhAssistantAgentSources.Admin, Definition = new NhAssistantPackageAgentDefinition("Planner", "Plans.", "Plan.", ToolSelectors: ["projects.*"], Autonomy: "execute") }] };
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Part((await PackagesAsync(host, packages => packages.PreviewImportAsync(missing, new NhAssistantAgentPackageImportOptions()))).Data!, "project-missing-planner", "agent").Code);
        Assert.Equal(NhAssistantFieldErrors.NotAllowed, Error((await PackagesAsync(host, packages => packages.PreviewImportAsync(withSettings, new NhAssistantAgentPackageImportOptions()))).Data!, Planner, "agent", "runSettings"));
        Assert.Equal(NhAssistantAdminErrorCodes.AgentExists, Part((await PackagesAsync(host, packages => packages.PreviewImportAsync(asAdmin, new NhAssistantAgentPackageImportOptions()))).Data!, Planner, "agent").Code);
    }

    /// <summary>
    /// The two desks of the sample's SPM-318 as a package a tool could write: the intake desk with run
    /// settings, a consultation, metadata and a group; the kickoff desk, which answers to a technical
    /// account, with a skill, a trigger and the hand-off it accepts from the intake desk.
    /// </summary>
    internal static NhAssistantAgentPackage Desks(
        int version = 1,
        bool locked = false,
        Func<NhAssistantPackageAgent, NhAssistantPackageAgent>? intake = null,
        Func<NhAssistantPackageAgent, NhAssistantPackageAgent>? kickoff = null,
        IReadOnlyList<NhAssistantPackageAgent>? more = null)
    {
        var intakeAgent = new NhAssistantPackageAgent(
            Intake,
            NhAssistantAgentSources.Admin,
            new NhAssistantPackageAgentDefinition("Intake desk", "Takes in new projects.", "Look at every new project.", ToolSelectors: ["projects.*", "assistant.handoff.*"], Autonomy: "execute"),
            new NhAssistantPackageRunSettings(NhAssistantAccountabilities.Agent),
            Consultations: [new NhAssistantPackageConsultation("project", "Knows the intake of projects.")],
            Metadata: new Dictionary<string, JsonElement> { [CostCenter] = JsonSerializer.SerializeToElement(new { code = "CC-100" }) },
            Groups: [Group]);
        var kickoffAgent = new NhAssistantPackageAgent(
            Kickoff,
            NhAssistantAgentSources.Admin,
            new NhAssistantPackageAgentDefinition("Kickoff desk", "Starts projects.", "Explain why you start the project.", ToolSelectors: ["projects.*"], Autonomy: "execute"),
            new NhAssistantPackageRunSettings(NhAssistantAccountabilities.ServiceAccount),
            Skills: [new NhAssistantPackageSkill("kickoff", "Starts a project that is ready.", "Start the project when the plan is approved.", Tools: ["projects.*"])],
            Triggers: [new NhAssistantPackageTrigger("kickoff-on-change", NhAssistantDataTriggerSources.Event, Skill: "kickoff", Event: "project.changed", Filters: ["not-draft"])],
            HandOffs: [new NhAssistantPackageHandOff("project", [Intake], "Starts projects that are ready.")],
            Locks: locked ? new Dictionary<string, IReadOnlyList<string>> { ["agent"] = ["instructions"], ["skill:kickoff"] = ["instructions"] } : null);
        return new NhAssistantAgentPackage(
            NhAssistantAgentPackageFormat.Name,
            NhAssistantAgentPackageFormat.Version,
            new NhAssistantAgentPackageInfo(PackageId, version),
            [intake?.Invoke(intakeAgent) ?? intakeAgent, kickoff?.Invoke(kickoffAgent) ?? kickoffAgent, .. more ?? []]);
    }

    internal static NhAssistantAgentPackageImportOptions Options(string account = PackagePrincipalFactory.Account)
    {
        return new NhAssistantAgentPackageImportOptions(new Dictionary<string, string> { [Kickoff] = account });
    }

    internal async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
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
    /// The building blocks the package names: a subject type, an approval policy, two trigger events, an
    /// entry of agent metadata, a group, an agent in code and the ceilings of consultations, hand-offs
    /// and shadow variants as data.
    /// </summary>
    internal static void Configure(NhAssistantBuilder assistant)
    {
        assistant
            .AddSubjectType<TestSubjectResolver>()
            .AddApprovalPolicy("project-review", policy => policy
                .Mode(NhAiApprovalMode.Deferred)
                .Lifetime(TimeSpan.FromDays(3))
                .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
            .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                .Description("A project changed.")
                .Subject(change => new NhAiSubjectRef("project", change.ProjectId.ToString("N")))
                .Message(change => change.Change)
                .Dedupe(change => change.EventId.ToString("N"))
                .Filter("not-draft", "Skips drafts.", (change, _) => !change.Change.StartsWith("draft", StringComparison.Ordinal)))
            .AddTriggerEvent<ProjectChanged>("project.closed", evt => evt
                .Description("A project closed.")
                .Subject(change => new NhAiSubjectRef("project", change.ProjectId.ToString("N")))
                .Message(change => change.Change)
                .Dedupe(change => change.EventId.ToString("N")))
            .AddAgentMetadata<PackageCostCenter>(CostCenter, entry => entry.Editable())
            .AddAgentGroup(Group, group => group.Named(new() { ["en"] = "Project desks" }, fallbackLanguage: "en"))
            .AddAgent(AssistantTestData.Agent(Planner))
            .ConfigureAgent(Planner, agent => agent
                .Grant("projects.*")
                .UseApprovalPolicy("project-review"))
            .UseConsultationsAsData(data => data.AllowRoutes(NhAssistantConsultationRoutes.AskerDecides))
            .UseHandOffsAsData(handOffs => handOffs
                .Allow("project", rule => rule
                    .ToAgentsCreatedInAdministration()
                    .FromAgentsCreatedInAdministration()
                    .From(Planner)))
            .UseShadowVariantsAsData(variants => variants.OfAgentsCreatedInAdministration());
    }

    /// <summary>
    /// The desks of <see cref="Desks"/> made by hand in the administration, as a host has them before it
    /// exports them.
    /// </summary>
    private static async Task SeedDesksAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
        Assert.True((await configuration.CreateAgentAsync(
            new NhAssistantAgentRequest(Intake, "Intake desk", "Takes in new projects.", "Look at every new project.", ["projects.*", "assistant.handoff.*"], NhAiAutonomyLevel.Execute)
            {
                Metadata = new Dictionary<string, JsonElement> { [CostCenter] = JsonSerializer.SerializeToElement(new { code = "CC-100" }) }
            },
            Admin)).Success);
        Assert.True((await configuration.CreateAgentAsync(
            new NhAssistantAgentRequest(Kickoff, "Kickoff desk", "Starts projects.", "Explain why you start the project.", ["projects.*"], NhAiAutonomyLevel.Execute),
            Admin)).Success);
        Assert.True((await configuration.SaveRunSettingsAsync(Intake, new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent), Admin)).Success);
        Assert.True((await configuration.SaveRunSettingsAsync(Kickoff, new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.ServiceAccount, PackagePrincipalFactory.Account), Admin)).Success);
        Assert.True((await configuration.SaveSkillAsync(Kickoff, "kickoff", new NhAssistantDataSkillRequest("Starts a project that is ready.", "Start the project when the plan is approved.", ["projects.*"]), Admin)).Success);
        Assert.True((await configuration.SaveTriggerAsync(Kickoff, "kickoff-on-change", new NhAssistantDataTriggerRequest("event", "kickoff", "project.changed", Filters: ["not-draft"]), Admin)).Success);
        Assert.True((await configuration.SaveConsultationAsync(Intake, "project", new NhAssistantDataConsultationRequest("Knows the intake of projects."), Admin)).Success);
        Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantDataHandOffs>().SaveAsync(Kickoff, "project", new NhAssistantDataHandOffRequest([Intake], "Starts projects that are ready."), Admin)).Success);
        Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentGroups>().SetMembershipAsync(Intake, [Group], Admin)).Success);
    }

    internal static async Task<T> PackagesAsync<T>(AssistantTestHost host, Func<INhAssistantAgentPackages, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>());
    }

    private static async Task<T> ConfigurationAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static async Task<AssistantAgent?> FindAgentAsync(AssistantTestHost host, string agentId)
    {
        return await host.Services.GetRequiredService<NhAssistantAdminStore>().FindAgentAsync(agentId, CancellationToken.None);
    }

    private static NhAssistantRegistrationState State(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRegistrationState>();
    }

    private static AssistantAgent NewAgentRow(string agentId)
    {
        return new AssistantAgent
        {
            Id = agentId,
            Source = NhAssistantAgentSources.Admin,
            DisplayName = "Kickoff desk",
            Description = "Starts projects.",
            ProfileName = "project-chat",
            Instructions = "Start.",
            InstructionsAssetId = agentId + "-instructions",
            InstructionsHash = NhAssistantAgentCatalog.ComputeHash("Start."),
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = Admin
        };
    }

    private static AssistantAgentSkillDefinition SkillRow(string agentId, string skillId, string instructions)
    {
        return new AssistantAgentSkillDefinition
        {
            AgentId = agentId,
            SkillId = skillId,
            Description = "Starts a project that is ready to start.",
            Instructions = instructions,
            InstructionsHash = NhAssistantAgentCatalog.ComputeHash(instructions),
            ToolSelectorsJson = "[\"projects.*\"]",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = Admin
        };
    }

    private static NhAssistantAgentPackagePartDto Part(NhAssistantAgentPackagePreviewDto preview, string agentId, string part)
    {
        return preview.Parts.Single(item => item.AgentId == agentId && item.Part == part);
    }

    /// <summary>
    /// The one error of a refused part's field.
    /// </summary>
    private static string Error(NhAssistantAgentPackagePreviewDto preview, string agentId, string part, string field)
    {
        var planned = Part(preview, agentId, part);
        Assert.Equal(NhAssistantPackagePartOutcomes.Refused, planned.Outcome);
        Assert.True(planned.Errors?.ContainsKey(field) == true, Describe(preview));
        return Assert.Single(planned.Errors![field]);
    }

    private static string Describe(NhAssistantAgentPackagePreviewDto preview)
    {
        return string.Join(
            "; ",
            preview.Parts.Select(part => $"{part.AgentId}/{part.Part}: {part.Outcome} {part.Code} {string.Join(",", (part.Errors ?? new Dictionary<string, string[]>()).Select(error => error.Key + "=" + string.Join("|", error.Value)))}"));
    }

    /// <summary>
    /// The one field a refused change names as locked.
    /// </summary>
    private static string Locked(TaskResult result)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(result));
        var field = Assert.Single(NhAssistantResults.FieldErrorsOf(result));
        Assert.Equal([NhAssistantFieldErrors.Locked], field.Value);
        return field.Key;
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }
}

/// <summary>
/// A neutral entry of agent metadata that the administration may also override on agents in code.
/// </summary>
public sealed record PackageCostCenter(string Code);

/// <summary>
/// The accounts of the package tests: two technical accounts may sign in, every other account may not.
/// </summary>
public sealed class PackagePrincipalFactory : INhAiPrincipalFactory
{
    public const string Account = "service-1";
    public const string OtherAccount = "service-2";
    public const string CannotSignIn = "service-locked";

    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<ClaimsPrincipal?>(actorId is Account or OtherAccount or AssistantAgentPackageTests.Admin
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"))
            : null);
    }
}
