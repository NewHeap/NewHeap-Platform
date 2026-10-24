using System.Text.Json;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The agent package format (PK3 of the agent packages plan): its own versioned types, read strictly,
/// so nothing a host does not know is dropped unnoticed, and a SHA-256 of each part's canonical JSON.
/// </summary>
public sealed class AssistantAgentPackageFormatTests
{
    private const string Minimal =
        """
        {
          "format": "newheap.assistant.agent-package",
          "formatVersion": 1,
          "package": { "id": "project-desks", "version": 1 },
          "agents": [
            {
              "id": "project-intake-desk",
              "source": "admin",
              "definition": { "displayName": "Intake desk", "description": "Takes in projects.", "instructions": "Look at new projects.", "toolSelectors": ["projects.*"], "autonomy": "execute" },
              "skills": [ { "id": "triage", "description": "Sorts new projects.", "instructions": "Sort them." } ],
              "locks": { "skill:triage": ["instructions"] }
            }
          ]
        }
        """;

    [Fact]
    public void A_package_reads_strictly_and_writes_without_nulls()
    {
        var read = NhAssistantAgentPackageFormat.Read(Minimal);

        Assert.True(read.Success);
        var package = read.Data!;
        Assert.Equal((NhAssistantAgentPackageFormat.Name, 1, "project-desks", 1), (package.Format, package.FormatVersion, package.Package.Id, package.Package.Version));
        var agent = Assert.Single(package.Agents);
        Assert.Equal(["instructions"], agent.Locks!["skill:triage"]);
        var written = NhAssistantAgentPackageFormat.Write(package);
        Assert.DoesNotContain("null", written, StringComparison.Ordinal);
        Assert.Equal(NhAssistantAgentPackageFormat.HashOf(package), NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageFormat.Read(written).Data!));
    }

    [Theory]
    [InlineData("""{"format":"newheap.assistant.agent-package","formatVersion":2,"package":{"id":"p","version":1},"agents":[]}""")]
    [InlineData("""{"format":"another.format","formatVersion":1,"package":{"id":"p","version":1},"agents":[]}""")]
    [InlineData("""{"formatVersion":1,"package":{"id":"p","version":1},"agents":[]}""")]
    [InlineData("""{"format":"newheap.assistant.agent-package","formatVersion":1,"package":{"id":"p","version":1},"agents":[],"signature":"x"}""")]
    [InlineData("""{"format":"newheap.assistant.agent-package","formatVersion":1,"package":{"id":"p","version":1,"owner":"x"},"agents":[]}""")]
    [InlineData("""{"format":"newheap.assistant.agent-package","formatVersion":1,"package":{"id":"p","version":1},"agents":[{"id":"a","source":"admin","history":[]}]}""")]
    [InlineData("""{"format":"newheap.assistant.agent-package","formatVersion":1,"package":{"id":"p","version":1},"agents":[{"id":"a","source":"admin","runSettings":{"accountability":"agent","serviceAccountActorId":"svc-1"}}]}""")]
    [InlineData("""{"format":"newheap.assistant.agent-package","formatVersion":1,"package":{"version":1},"agents":[]}""")]
    [InlineData("""{"format":"newheap.assistant.agent-package","formatVersion":1,"package":{"id":"p","version":"1"},"agents":[]}""")]
    [InlineData("""{"Format":"newheap.assistant.agent-package","formatVersion":1,"package":{"id":"p","version":1},"agents":[]}""")]
    [InlineData("""[1, 2]""")]
    [InlineData("""{ not json""")]
    public void An_unknown_property_a_missing_one_or_a_newer_version_is_refused(string json)
    {
        var read = NhAssistantAgentPackageFormat.Read(json);

        Assert.False(read.Success);
        Assert.Equal(NhAssistantErrorCodes.PackageFormatUnsupported, Code(read));
    }

    [Fact]
    public void A_package_above_one_megabyte_or_with_more_than_twenty_agents_is_too_large()
    {
        var agents = Enumerable.Range(1, 21)
            .Select(index => new NhAssistantPackageAgent("agent-" + index, NhAssistantAgentSources.Admin))
            .ToArray();
        var crowded = new NhAssistantAgentPackage(NhAssistantAgentPackageFormat.Name, 1, new NhAssistantAgentPackageInfo("crowded", 1), agents);
        var large = Minimal.Replace("Look at new projects.", new string('x', NhAssistantAgentPackageFormat.MaxBytes), StringComparison.Ordinal);

        Assert.Equal(NhAssistantErrorCodes.PackageTooLarge, Code(NhAssistantAgentPackageFormat.Read(NhAssistantAgentPackageFormat.Write(crowded))));
        Assert.Equal(NhAssistantErrorCodes.PackageTooLarge, Code(NhAssistantAgentPackageFormat.Check(crowded)));
        Assert.Equal(NhAssistantErrorCodes.PackageTooLarge, Code(NhAssistantAgentPackageFormat.Read(large)));
    }

    [Fact]
    public void The_hash_of_a_part_is_the_sha_256_of_its_canonical_json()
    {
        var skill = new NhAssistantPackageSkill("triage", "Sorts new projects.", "Sort them.", Tools: ["projects.search"]);
        var reordered = JsonDocument.Parse("""{ "tools": ["projects.search"], "instructions": "Sort them.", "description": "Sorts new projects.", "id": "triage", "acceptsFiles": false, "profile": null }""").RootElement;

        var hash = NhAssistantAgentPackageFormat.HashOf(skill);

        Assert.Matches("^[0-9a-f]{64}$", hash);
        // Property order, white space and null values do not change the hash; a value does.
        Assert.Equal(hash, NhAssistantAgentPackageFormat.HashOf(reordered));
        Assert.NotEqual(hash, NhAssistantAgentPackageFormat.HashOf(skill with { Instructions = "Sort them now." }));
        Assert.Equal(
            "{\"acceptsFiles\":false,\"description\":\"Sorts new projects.\",\"id\":\"triage\",\"instructions\":\"Sort them.\",\"tools\":[\"projects.search\"]}",
            System.Text.Encoding.UTF8.GetString(NhAssistantAgentPackageFormat.Canonical(reordered)));
    }

    [Fact]
    public void Part_keys_and_lockable_fields_are_fixed_per_kind()
    {
        Assert.True(NhAssistantPackagePartKinds.TryParse("skill:triage", out var kind, out var key));
        Assert.Equal((NhAssistantPackagePartKinds.Skill, "triage"), (kind, key));
        Assert.True(NhAssistantPackagePartKinds.TryParse("runSettings", out kind, out key));
        Assert.Equal((NhAssistantPackagePartKinds.RunSettings, string.Empty), (kind, key));
        Assert.False(NhAssistantPackagePartKinds.TryParse("skill:", out _, out _));
        Assert.False(NhAssistantPackagePartKinds.TryParse("skill:Triage", out _, out _));
        Assert.False(NhAssistantPackagePartKinds.TryParse("history:triage", out _, out _));
        Assert.Equal("handOff:project", NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.HandOff, "project"));
        Assert.Contains("instructions", NhAssistantPackageLockFields.Of(NhAssistantPackagePartKinds.Skill));
        Assert.DoesNotContain("serviceAccountActorId", NhAssistantPackageLockFields.Of(NhAssistantPackagePartKinds.RunSettings));
        Assert.Empty(NhAssistantPackageLockFields.Of("history"));
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }
}
