using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-329: the operations process reads agents as they are in effect and changes their definitions
/// from code. SPM-330: an operator promotes a shadow variant of the planner onto the planner. SPM-313:
/// the departments of agents are searchable values of their own (<c>IndexByEach</c>).
/// </summary>
public sealed class AssistantAgentDefinitionSamplesTests(AssistantAgentRunSampleHost host)
    : IClassFixture<AssistantAgentRunSampleHost>
{
    private const string Planner = SampleProjectPlannerComposition.AgentId;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_329_the_operations_process_reads_agents_as_in_effect_and_changes_them_from_code()
    {
        const string reporter = "spm-329-cost-reporter";
        var operatorId = host.OwnerId;
        try
        {
            // Every agent that is no shadow variant, as in effect, with its cost center.
            var overview = await DefinitionsAsync(service => service.OverviewAsync(CancellationToken.None));
            var planner = Assert.Single(overview, agent => agent.AgentId == Planner);
            Assert.Equal(
                (NhAssistantAgentSources.Code, "nh-assistant.agents.sample-project-planner.name", NhAssistantAgentRunStates.Active, false, "cc-4100"),
                (planner.Source, planner.DisplayName, planner.RunState, planner.IsOverridden, planner.CostCenter));
            Assert.DoesNotContain(overview, agent => agent.AgentId == SampleProjectPlannerVariantComposition.AgentId);
            var before = (await DirectoryAsync(directory => directory.GetAsync(Planner)))!;
            Assert.Equal(ProjectAiAssets.ProjectPlannerInstructions.Content, before.Instructions);

            // A new description of the planner is an override of its code at the version read.
            var described = await DefinitionsAsync(service => service.DescribeAsync(Planner, "Plans the projects of the portfolio.", operatorId, CancellationToken.None));
            Assert.True(described.Success, string.Join("; ", described.AllErrorMessages));
            Assert.Equal((before.Version + 1, true, "Plans the projects of the portfolio."), (described.Data!.Version, described.Data.IsOverridden, described.Data.Description));
            Assert.Equal(before.Instructions, described.Data.Instructions);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var stale = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentDefinitions>().UpdateAsync(
                    Planner,
                    new NhAssistantAgentDefinitionRequest(before.DisplayName, "Plans late.", before.Instructions!, before.ToolSelectors, before.Autonomy, before.RequiredPolicy),
                    before.Version,
                    operatorId.ToString("D"));
                Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(stale));
            }

            // Reset, the code decides again.
            var reset = await DefinitionsAsync(service => service.ResetAsync(Planner, operatorId, CancellationToken.None));
            Assert.Equal((false, before.Description), (reset.Data!.IsOverridden, reset.Data.Description));

            // An agent created at runtime changes the same way and keeps its metadata.
            var created = await ConfigureAsync(configuration => configuration.CreateAgentAsync(
                new NhAssistantAgentRequest(
                    reporter,
                    "Cost reporter",
                    "Reports what the projects of a cost center cost.",
                    "Report the costs of the projects briefly.",
                    [ProjectAiTools.ChangeStatusToolId],
                    NhAiAutonomyLevel.Propose,
                    OfferedInChat: false)
                {
                    Metadata = new Dictionary<string, JsonElement>
                    {
                        [SampleAgentCostCenterComposition.Entry] = JsonSerializer.SerializeToElement(new { code = "CC-4200" })
                    }
                },
                operatorId.ToString("D")));
            Assert.True(created.Success, string.Join("; ", created.AllErrorMessages));
            var changed = await DefinitionsAsync(service => service.DescribeAsync(reporter, "Reports the costs of each project.", operatorId, CancellationToken.None));
            Assert.Equal((2, "Reports the costs of each project."), (changed.Data!.Version, changed.Data.Description));
            Assert.Equal("cc-4200", (await DefinitionsAsync(service => service.OverviewAsync(CancellationToken.None))).Single(agent => agent.AgentId == reporter).CostCenter);

            // An agent in code is never retired; one created at runtime is.
            Assert.Equal(NhAssistantAdminErrorCodes.CodeAgentNotDeletable, Code(await DefinitionsAsync(service => service.RetireAsync(Planner, operatorId, CancellationToken.None))));
            Assert.True((await DefinitionsAsync(service => service.RetireAsync(reporter, operatorId, CancellationToken.None))).Success);
            Assert.Null(await DirectoryAsync(directory => directory.GetAsync(reporter)));

            // The same audit as the administration.
            var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>().Events;
            Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentUpdated && evt.ObjectId == Planner);
            Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentReset && evt.ObjectId == Planner);
            Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentDeleted && evt.ObjectId == reporter);
        }
        finally
        {
            await DefinitionsAsync(service => service.ResetAsync(Planner, operatorId, CancellationToken.None));
            await DefinitionsAsync(service => service.RetireAsync(reporter, operatorId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task SPM_330_an_operator_promotes_the_planners_trial_onto_the_planner_with_its_skills()
    {
        const string trial = "spm-330-planner-trial";
        const string review = "progress-review";
        var operatorId = host.OwnerId;
        using var administrator = host.CreateClient(operatorId.ToString(), admin: true);
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        try
        {
            // The trial starts from the planner's definition, tries a description and a skill of its own
            // in shadow, and is evaluated next to the planner as in SPM-319.
            var live = (await DirectoryAsync(directory => directory.GetAsync(Planner)))!;
            using (var created = await administrator.PostAsJsonAsync("/api/assistant/admin/agents", new
            {
                id = trial,
                displayName = "Planner trial",
                description = "Plans projects and reviews their progress.",
                instructions = "Plan the project and review its progress.",
                toolSelectors = live.ToolSelectors,
                mcpServerIds = Array.Empty<string>(),
                requiredPolicy = SampleAssistantComposition.AccessPolicy,
                autonomy = "execute",
                worksOnSubjectTypes = new[] { ProjectSubjects.Type },
                worksOnAllSubjects = false,
                shadowOf = Planner
            }, Json))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }
            var settings = await ConfigureAsync(configuration => configuration.SaveRunSettingsAsync(
                trial,
                new NhAssistantAgentRunSettingsRequest(
                    NhAssistantAccountabilities.ServiceAccount,
                    host.OperationsAccountId.ToString("D"),
                    ApprovalPolicy: SampleIntakeReviewerComposition.ApprovalPolicyId,
                    RuleHandler: SampleProjectPlannerComposition.ProgressNoteRules),
                operatorId.ToString("D")));
            Assert.True(settings.Success, string.Join("; ", settings.AllErrorMessages));
            var skill = await ConfigureAsync(configuration => configuration.SaveSkillAsync(
                trial,
                review,
                new NhAssistantDataSkillRequest("Reviews the progress of a project.", "Review the progress of the project briefly.", [ProjectAiTools.ChangeStatusToolId]),
                operatorId.ToString("D")));
            Assert.True(skill.Success, string.Join("; ", skill.AllErrorMessages));

            // Only administrators promote through the administration.
            using (var refused = await colleague.PostAsJsonAsync($"/api/assistant/admin/agents/{trial}/promote", new { expectedVersion = live.Version }, Json))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            // The operator promotes it at the versions evaluated: the planner takes its definition as its
            // next version, keeps its name, and gets the trial's skill, which starts in shadow.
            var promoted = await DefinitionsAsync(service => service.PromoteAsync(trial, operatorId, CancellationToken.None));
            Assert.True(promoted.Success, string.Join("; ", promoted.AllErrorMessages));
            var planner = promoted.Data!.Agent;
            Assert.Equal(
                (live.Version + 1, true, live.DisplayName, "Plans projects and reviews their progress.", "Plan the project and review its progress.", false),
                (planner.Version, planner.IsOverridden, planner.DisplayName, planner.Description, planner.Instructions, planner.InShadow));
            Assert.Equal([review], promoted.Data.MovedSkills);
            var state = (await ControlAsync(control => control.GetAsync(Planner)))!;
            Assert.True(state.Skills.Single(item => item.SkillId == review).Shadow.Shadow);

            // HD13: the trial never goes live; it is gone, and the planner's variant in code stays.
            Assert.Null(await DirectoryAsync(directory => directory.GetAsync(trial)));
            var variants = (await administrator.GetFromJsonAsync<NhAssistantShadowVariantsDto>($"/api/assistant/admin/agents/{Planner}/shadow-variants", Json))!;
            Assert.Equal([SampleProjectPlannerVariantComposition.AgentId], variants.Variants.Select(variant => variant.AgentId));
            Assert.Equal(NhAssistantErrorCodes.NotShadowVariant, Code(await DefinitionsAsync(service => service.PromoteAsync(Planner, operatorId, CancellationToken.None))));
            Assert.Contains(host.Services.GetRequiredService<SampleAssistantAuditLog>().Events, evt =>
                evt.Kind == NhAssistantAuditEventKind.AdminAgentShadowVariantPromoted && evt.ObjectId == trial && evt.LiveAgentId == Planner);
        }
        finally
        {
            await ConfigureAsync(configuration => configuration.RemoveSkillAsync(Planner, review, null, operatorId.ToString("D")));
            await DefinitionsAsync(service => service.ResetAsync(Planner, operatorId, CancellationToken.None));
            await ConfigureAsync(configuration => configuration.RemoveRunSettingsAsync(trial, null, operatorId.ToString("D")));
            await DefinitionsAsync(service => service.RetireAsync(trial, operatorId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task SPM_313_agents_are_found_by_any_department_they_work_for()
    {
        const string entry = SampleAgentDepartmentsComposition.Entry;
        const string desk = "spm-313-finance-desk";
        var operatorId = host.OwnerId.ToString("D");
        try
        {
            // The planner works for planning and operations in code; the codes are searchable values.
            var catalog = await ConfigureAsync(configuration => Task.FromResult(configuration.GetCatalog()));
            Assert.True(catalog.AgentMetadata.Single(item => item.Name == entry).IndexedByEach);
            Assert.Contains(Planner, await FoundAsync(entry, "Operations"));
            Assert.Contains(Planner, await FoundAsync(entry, "planning"));

            // A code that is no department is refused; an agent created at runtime gets its own codes.
            using (var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true))
            using (var refused = await administrator.PostAsJsonAsync("/api/assistant/admin/agents", new
            {
                id = desk,
                displayName = "Finance desk",
                description = "Answers questions about project budgets.",
                instructions = "Answer questions about project budgets briefly.",
                toolSelectors = new[] { ProjectAiTools.ChangeStatusToolId },
                autonomy = "propose",
                metadata = new Dictionary<string, object> { [entry] = new { codes = new[] { "Finance Office" } } }
            }, Json))
            {
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                var errors = (await refused.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("errors");
                Assert.Equal("unknown-department", errors.GetProperty("metadata.departments.codes")[0].GetString());
            }
            Assert.True((await ConfigureAsync(configuration => configuration.CreateAgentAsync(DeskRequest(desk, "finance", "operations"), operatorId))).Success);
            Assert.Equal([desk], await FoundAsync(entry, "FINANCE"));
            Assert.Equal([Planner, desk], (await FoundAsync(entry, "operations")).Order(StringComparer.Ordinal).Where(id => id == Planner || id == desk));
            var values = (await MetadataAsync(metadata => metadata.GetAgentAsync(desk)))!.Metadata.Single(item => item.Name == entry);
            Assert.Equal(["finance", "operations"], values.IndexValues);
        }
        finally
        {
            await DefinitionsAsync(service => service.RetireAsync(desk, host.OwnerId, CancellationToken.None));
        }
    }

    private static NhAssistantAgentRequest DeskRequest(string agentId, params string[] departments)
    {
        return new NhAssistantAgentRequest(
            agentId,
            "Finance desk",
            "Answers questions about project budgets.",
            "Answer questions about project budgets briefly.",
            [ProjectAiTools.ChangeStatusToolId],
            NhAiAutonomyLevel.Propose,
            OfferedInChat: false)
        {
            Metadata = new Dictionary<string, JsonElement>
            {
                [SampleAgentDepartmentsComposition.Entry] = JsonSerializer.SerializeToElement(new SampleAgentDepartments(departments), Json)
            }
        };
    }

    private async Task<IReadOnlyList<string>> FoundAsync(string entry, string value)
    {
        var found = await MetadataAsync(metadata => metadata.ListAgentsAsync(entry, value));
        return found.Select(agent => agent.AgentId).ToArray();
    }

    private async Task<T> DefinitionsAsync<T>(Func<SampleAgentDefinitionService, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleAgentDefinitionService>());
    }

    private async Task<T> DirectoryAsync<T>(Func<INhAssistantAgentDirectory, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentDirectory>());
    }

    private async Task<T> ConfigureAsync<T>(Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private async Task<T> MetadataAsync<T>(Func<INhAssistantAgentMetadata, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentMetadata>());
    }

    private async Task<T> ControlAsync<T>(Func<INhAssistantAgentControl, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>());
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }
}
