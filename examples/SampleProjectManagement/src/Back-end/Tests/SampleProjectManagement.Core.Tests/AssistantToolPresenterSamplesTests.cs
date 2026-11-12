using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-347: a unit test of the sample's tool presenter checks what it presents against the assistant's
/// presentation limits with <see cref="NhAssistantPresentationLimits.FindBrokenRule"/>, the check by which
/// the assistant drops a presentation for the generic summary. It presents the status change of a
/// project with the longest key and name the sample stores, in every supported culture, so a presenter
/// that grows a field or a text past a limit fails here instead of showing approvers the generic summary
/// in production. No host and no database: the presenter reads the project through
/// <see cref="IProjectAiReadService"/>.
/// </summary>
public sealed class AssistantToolPresenterSamplesTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("nl-NL")]
    public async Task SPM_347_the_status_change_presentation_keeps_within_the_limits_for_the_longest_project(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var divisionId = Guid.NewGuid();
        var project = new ProjectAiApprovalItem(
            Guid.NewGuid(),
            new string('K', MaxLengthOf(nameof(Project.Key))),
            new string('N', MaxLengthOf(nameof(Project.Name))),
            ProjectStatus.OnHold);
        var presenter = CreatePresenter(new OneProject(divisionId, project));

        var presentation = await PresentInAsync(
            culture,
            () => presenter.PresentApprovalAsync(
                new NhAssistantApprovalPresentationContext(
                    ChangeStatusDescriptor(),
                    new ProjectAiStatusChangeInput(project.Id, ProjectStatus.Archived),
                    InvocationIn(divisionId),
                    culture),
                TestContext.Current.CancellationToken));

        Assert.NotNull(presentation);
        Assert.Contains(project.Name, presentation.Summary, StringComparison.Ordinal);
        // The project, and its status with the status it has now, so the card shows the change.
        Assert.Equal(2, presentation.Fields.Count);
        Assert.Equal(NhAssistantPresentationFieldChange.Changed, presentation.Fields[1].Change);
        Assert.NotEqual(presentation.Fields[1].CurrentValue, presentation.Fields[1].Value);
        // The check the assistant drops a presentation by finds no broken rule.
        Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(presentation));
    }

    [Fact]
    public async Task SPM_347_the_check_names_the_rule_a_grown_presentation_would_be_dropped_by()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var divisionId = Guid.NewGuid();
        var project = new ProjectAiApprovalItem(Guid.NewGuid(), "PRJ-347", "Presenter limits", ProjectStatus.Draft);
        var presenter = CreatePresenter(new OneProject(divisionId, project));
        var presentation = await PresentInAsync(
            culture,
            () => presenter.PresentApprovalAsync(
                new NhAssistantApprovalPresentationContext(
                    ChangeStatusDescriptor(),
                    new ProjectAiStatusChangeInput(project.Id, ProjectStatus.Active),
                    InvocationIn(divisionId),
                    culture),
                TestContext.Current.CancellationToken));
        Assert.NotNull(presentation);

        // A presenter that later showed every task of the project would break the field cap: the test
        // names the rule, as the warning in production would, before an approver reads the fallback.
        var grown = presentation with
        {
            Fields = presentation.Fields
                .Concat(Enumerable.Range(1, NhAssistantPresentationLimits.MaxFields)
                    .Select(index => new NhAssistantPresentationField($"Task {index}", "Open")))
                .ToArray()
        };

        Assert.Equal(NhAssistantPresentationRules.TooManyFields, NhAssistantPresentationLimits.FindBrokenRule(grown));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("nl-NL")]
    public async Task SPM_248_the_result_cards_of_a_project_read_and_a_search_keep_within_the_limits(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var presenter = CreateResultPresenter();
        var project = new ProjectAiProjectItem(
            Guid.NewGuid(),
            new string('K', MaxLengthOf(nameof(Project.Key))),
            new string('N', MaxLengthOf(nameof(Project.Name))),
            ProjectStatus.OnHold,
            new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            null);
        var found = Enumerable.Range(1, 10)
            .Select(index => new ProjectAiSearchItem(Guid.NewGuid(), new string('K', MaxLengthOf(nameof(Project.Key))), new string('N', MaxLengthOf(nameof(Project.Name)))))
            .ToArray();

        var read = await PresentInAsync(culture, () => presenter.PresentResultAsync(
            ResultOf(ProjectOverviewAiTools.GetToolId, project, culture),
            TestContext.Current.CancellationToken));
        var search = await PresentInAsync(culture, () => presenter.PresentResultAsync(
            ResultOf(ProjectAiTools.SearchToolId, found, culture),
            TestContext.Current.CancellationToken));
        var other = await presenter.PresentResultAsync(
            ResultOf(ProjectAiTools.ChangeStatusToolId, project, culture),
            TestContext.Current.CancellationToken);

        // The read names the project, its status and deadline and links to its page.
        Assert.NotNull(read);
        Assert.Equal(project.Name, read.Title);
        Assert.Equal(3, read.Fields.Count);
        Assert.Equal($"/management/intake-reviews?project={project.Id:D}", read.Link!.Url);
        Assert.Null(NhAssistantPresentationLimits.FindBrokenResultRule(read));
        // The search shows the first eight of ten projects and says so.
        Assert.NotNull(search);
        Assert.Equal(NhAssistantPresentationLimits.MaxFields, search.Fields.Count);
        Assert.Contains("10", search.Summary, StringComparison.Ordinal);
        Assert.Equal(SampleAssistantToolResultPresenter.ProjectBoardRoute, search.Link!.Url);
        Assert.Null(NhAssistantPresentationLimits.FindBrokenResultRule(search));
        // A tool the presenter does not present gets no card.
        Assert.Null(other);
    }

    /// <summary>
    /// The longest text the sample stores in a project's property, from its <see cref="StringLengthAttribute"/>,
    /// so a longer column in the model makes this test present the longer text.
    /// </summary>
    private static int MaxLengthOf(string propertyName)
    {
        return typeof(Project).GetProperty(propertyName)!.GetCustomAttribute<StringLengthAttribute>()!.MaximumLength;
    }

    private static SampleAssistantToolResultPresenter CreateResultPresenter()
    {
        var localizers = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance);
        return new SampleAssistantToolResultPresenter(new StringLocalizer<SampleAssistantToolPresenter>(localizers));
    }

    /// <summary>
    /// What the assistant gives a result presenter for a call of <paramref name="toolId"/> that returned
    /// <paramref name="data"/>, as the tool's result serializes it.
    /// </summary>
    private static NhAssistantToolResultPresentationContext ResultOf(string toolId, object data, CultureInfo culture)
    {
        var descriptor = new ProjectAiToolsNhAiCatalog().Descriptors.FirstOrDefault(tool => tool.Id == toolId)
            ?? Assert.Single(new ProjectOverviewAiToolsNhAiCatalog().Descriptors, tool => tool.Id == toolId);
        return new NhAssistantToolResultPresentationContext(
            descriptor,
            System.Text.Json.JsonSerializer.SerializeToElement(data, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            culture);
    }

    private static SampleAssistantToolPresenter CreatePresenter(IProjectAiReadService projects)
    {
        var localizers = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance);
        // The status change reads the project through the read service; only the task completion queries
        // the context, which this test does not present, so it needs no database provider.
        var dbContext = new SampleProjectManagementDbContext(new DbContextOptionsBuilder<SampleProjectManagementDbContext>().Options);
        return new SampleAssistantToolPresenter(
            projects,
            dbContext,
            new StringLocalizer<SampleAssistantToolPresenter>(localizers));
    }

    private static NhAiToolDescriptor ChangeStatusDescriptor()
    {
        return Assert.Single(new ProjectAiToolsNhAiCatalog().Descriptors, tool => tool.Id == ProjectAiTools.ChangeStatusToolId);
    }

    private static NhAiInvocationContext InvocationIn(Guid divisionId)
    {
        return new NhAiInvocationContext(
            "spm-347-user",
            "project-assistance",
            new Dictionary<string, string>
            {
                [ProjectAiTools.DivisionScopeKey] = divisionId.ToString("D")
            });
    }

    /// <summary>
    /// Presents with <paramref name="culture"/> current, as the assistant runs a presenter in each culture
    /// it presents in.
    /// </summary>
    private static async Task<T?> PresentInAsync<T>(
        CultureInfo culture,
        Func<ValueTask<T?>> present)
        where T : class
    {
        var (previousCulture, previousUiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return await present();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private sealed class OneProject(Guid divisionId, ProjectAiApprovalItem project) : IProjectAiReadService
    {
        public Task<ProjectAiApprovalItem?> GetForAiApprovalAsync(
            Guid division,
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(division == divisionId && projectId == project.Id ? project : null);
        }

        public Task<IReadOnlyList<ProjectAiSearchItem>> SearchForAiAsync(
            Guid division,
            string? query,
            int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProjectAiSearchItem>>([]);
        }
    }
}
