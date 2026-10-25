using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The installed package (LS32) and an agent's parts without an export (LS33):
/// <see cref="INhAssistantAgentPackages.GetPackageAsync"/> returns the stored package with its current
/// links, also after every agent was unlinked, and <see cref="INhAssistantAgentPackages.ReadAsync"/>
/// returns the parts and hashes an export builds, without the export's audit; on both relational
/// providers.
/// </summary>
public sealed class AssistantAgentPackageReadTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Intake = AssistantAgentPackageTests.Intake;
    private const string Kickoff = AssistantAgentPackageTests.Kickoff;
    private const string PackageId = AssistantAgentPackageTests.PackageId;
    private const string Admin = AssistantAgentPackageTests.Admin;

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_installed_package_stays_known_with_its_links_after_an_unlink(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var imported = await PackagesAsync(host, packages => packages.ImportAsync(
            AssistantAgentPackageTests.Desks(locked: true),
            AssistantAgentPackageTests.Options(),
            Admin));
        Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));

        var installed = (await PackagesAsync(host, packages => packages.GetPackageAsync(PackageId)))!;

        Assert.Equal(
            (PackageId, 1, NhAssistantAgentPackageFormat.Version, imported.Data!.Hash, Admin),
            (installed.PackageId, installed.Version, installed.FormatVersion, installed.Hash, installed.ImportedBy));
        Assert.Equal([Intake, Kickoff], installed.AgentIds.Order(StringComparer.Ordinal));
        Assert.Equal([Intake, Kickoff], installed.Agents.Select(agent => agent.AgentId));
        var kickoff = installed.Agents.Single(agent => agent.AgentId == Kickoff);
        Assert.Equal(["agent", "handOff:project", "runSettings", "skill:kickoff", "trigger:kickoff-on-change"], kickoff.Parts.Select(part => part.Part));
        Assert.All(installed.Agents.SelectMany(agent => agent.Parts), part => Assert.Equal((PackageId, 1, false), (part.PackageId, part.PackageVersion, part.ChangedLocally)));
        Assert.Equal(["instructions"], kickoff.Parts.Single(part => part.Part == "agent").LockedFields);
        // The links of an agent are the ones the administration shows next to it.
        var link = (await PackagesAsync(host, packages => packages.GetLinkAsync(Kickoff)))!;
        Assert.Equal(link.Parts.Select(part => (part.Part, part.Hash)), kickoff.Parts.Select(part => (part.Part, part.Hash)));

        // A part changed on the host shows as changed locally.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
            var skill = Assert.Single((await configuration.GetAsync(Kickoff))!.DataSkills);
            var saved = await configuration.SaveSkillAsync(
                Kickoff,
                "kickoff",
                new NhAssistantDataSkillRequest("Starts a project that is ready to start.", skill.Instructions, ["projects.*"], ExpectedVersion: skill.Version),
                Admin);
            Assert.True(saved.Success, string.Join(", ", saved.AllErrorMessages));
        }

        var changed = (await PackagesAsync(host, packages => packages.GetPackageAsync(PackageId)))!;
        Assert.True(changed.Agents.Single(agent => agent.AgentId == Kickoff).Parts.Single(part => part.Part == "skill:kickoff").ChangedLocally);

        // Unlinked as a whole, the agents no longer belong to the package, which stays known.
        Assert.True((await PackagesAsync(host, packages => packages.UnlinkAsync(Intake, null, Admin))).Success);
        Assert.True((await PackagesAsync(host, packages => packages.UnlinkAsync(Kickoff, null, Admin))).Success);
        Assert.Null(await PackagesAsync(host, packages => packages.GetLinkAsync(Kickoff)));
        var unlinked = (await PackagesAsync(host, packages => packages.GetPackageAsync(PackageId)))!;
        Assert.Equal((1, imported.Data.Hash), (unlinked.Version, unlinked.Hash));
        Assert.Empty(unlinked.AgentIds);
        Assert.Empty(unlinked.Agents);

        // A package the host never imported, or an id that is no package id, is unknown.
        Assert.Null(await PackagesAsync(host, packages => packages.GetPackageAsync("other-desks")));
        Assert.Null(await PackagesAsync(host, packages => packages.GetPackageAsync("Project-Desks")));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_read_returns_the_parts_and_hashes_of_an_export_without_its_audit(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        Assert.True((await PackagesAsync(host, packages => packages.ImportAsync(
            AssistantAgentPackageTests.Desks(locked: true),
            AssistantAgentPackageTests.Options(),
            Admin))).Success);
        var installed = (await PackagesAsync(host, packages => packages.GetPackageAsync(PackageId)))!;

        var read = await PackagesAsync(host, packages => packages.ReadAsync([Kickoff, Intake]));

        Assert.True(read.Success, string.Join(", ", read.AllErrorMessages));
        Assert.Equal([Kickoff, Intake], read.Data!.Select(agent => agent.Id));
        Assert.All(read.Data!, agent => Assert.Null(agent.Locks));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentPackageExported);
        // The hashes compare with the links: nothing changed since the import.
        foreach (var agent in installed.Agents)
        {
            var parts = read.Data!.Single(item => item.Id == agent.AgentId).Hashes!;
            Assert.All(agent.Parts, part => Assert.Equal(part.Hash, parts[part.Part]));
        }

        var exported = await PackagesAsync(host, packages => packages.ExportAsync(
            [Kickoff, Intake],
            new NhAssistantAgentPackageExportOptions(PackageId),
            Admin));

        Assert.True(exported.Success, string.Join(", ", exported.AllErrorMessages));
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentPackageExported);
        Assert.Equal(Canonical(exported.Data!.Package.Agents), Canonical(read.Data!));
        for (var index = 0; index < read.Data!.Count; index++)
        {
            Assert.Equal(exported.Data.Package.Agents[index].Hashes, read.Data[index].Hashes);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_read_refuses_the_agent_ids_an_export_refuses(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);

        Assert.Equal(["agentIds:required"], Fields(await PackagesAsync(host, packages => packages.ReadAsync([]))));
        Assert.Equal(["agentIds:invalid"], Fields(await PackagesAsync(host, packages => packages.ReadAsync([AssistantAgentPackageTests.Planner, AssistantAgentPackageTests.Planner]))));
        Assert.Equal(["agentIds:invalid"], Fields(await PackagesAsync(host, packages => packages.ReadAsync(["Project Planner"]))));
        Assert.Equal(["agentIds:too-long"], Fields(await PackagesAsync(host, packages => packages.ReadAsync(
            Enumerable.Range(0, NhAssistantAgentPackageFormat.MaxAgents + 1).Select(index => "agent-" + index).ToArray()))));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await PackagesAsync(host, packages => packages.ReadAsync(["unknown-agent"]))));

        // An agent in code reads as the overlay an export carries.
        var planner = await PackagesAsync(host, packages => packages.ReadAsync([AssistantAgentPackageTests.Planner]));
        Assert.Equal(NhAssistantAgentSources.Code, Assert.Single(planner.Data!).Source);
        Assert.Empty(host.Business.Events);
    }

    /// <summary>
    /// The canonical JSON of agents as a package carries them, without locks.
    /// </summary>
    private static string Canonical(IReadOnlyList<NhAssistantPackageAgent> agents)
    {
        return NhAssistantAgentPackageFormat.Write(new NhAssistantAgentPackage(
            NhAssistantAgentPackageFormat.Name,
            NhAssistantAgentPackageFormat.Version,
            new NhAssistantAgentPackageInfo(PackageId, 1),
            agents.Select(agent => agent with { Locks = null }).ToArray()));
    }

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
            assistantBuilder: AssistantAgentPackageTests.Configure);
    }

    private static Task<T> PackagesAsync<T>(AssistantTestHost host, Func<INhAssistantAgentPackages, Task<T>> action)
    {
        return AssistantAgentPackageTests.PackagesAsync(host, action);
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
