using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-392: alerts (FB87). The deadline watch is an agent whose rules warn people about a project that
/// is due today while tasks are still open: a warning, not a proposal, so nothing waits for approval
/// and nothing changes. Its alert kind <see cref="KindId"/> is declared in code with its name, urgency,
/// texts per language, recipients, repeat interval and default delivery. Every working-day morning a
/// schedule starts the watch on the projects due today; its rules raise one alert per project and
/// deadline day for the project's owner, its responsible person, and for the members who work on it.
/// The same project and day raised again on the next round is the same alert, updated in place, so
/// people are not warned twice. Once the tasks are done, or the deadline moved, the rules resolve it,
/// and the alert expires at the end of its day anyway. The application's own code warns more people
/// through <see cref="SampleDeadlineAlerts"/>; the platform checks every recipient, so a person without
/// a role on the project receives nothing. The sample's tasks have no due date or responsible person of
/// their own, so the watch alerts about the project's deadline and its owner.
/// Call it after <c>AddSampleIntakeReviewer</c>, which registers the project subject type.
/// </summary>
public static class SampleDeadlineAlertsComposition
{
    public const string AgentId = "sample-deadline-watch";
    public const string KindId = "project-due-today";
    public const string TriggerId = "project-deadline-watch";

    /// <summary>The front-end translation key of the kind's name.</summary>
    public const string KindNameKey = "nh-assistant.alert-kinds.project-due-today.name";

    /// <summary>The time zone in which a deadline falls on a day, as the sample's divisions keep it.</summary>
    public static TimeZoneInfo TimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    private static readonly ResourceManager Texts = new(typeof(SampleAssistantToolPresenter));

    public static IServiceCollection AddSampleDeadlineAlerts(this IServiceCollection services)
    {
        services.TryAddScoped<SampleDeadlineAlerts>();
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-deadline-watch.name",
                DescriptionKey: "nh-assistant.agents.sample-deadline-watch.description",
                // The model answers people who ask in the project's thread; the rules run every headless run.
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectDeadlineWatchInstructions,
                ToolSelectors: ["projects.search"],
                Autonomy: NhAiAutonomyLevel.Observe,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .AddAlertKind(KindId, kind => kind
                .DisplayName(KindNameKey)
                .Urgency(NhAssistantAlertUrgency.Warning)
                // The platform writes the texts in each reader's own language from the parameters.
                .Title((culture, parameters) => Format("AlertProjectDueTodayTitle", culture, parameters["project"]))
                .Text((culture, parameters) => Format(
                    "AlertProjectDueTodayText",
                    culture,
                    DateOnly.ParseExact(parameters["deadline"], "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("D", culture),
                    parameters["openTasks"]))
                // The owner, as the project's resolver names its responsible person, and the people the
                // rules or the application name; each one is checked before anything reaches them.
                .Recipients(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit))
                // A project due today is warned about once per day, however many rounds raise it.
                .RepeatAfter(TimeSpan.FromDays(1))
                // By default in the morning digest on working days; an urgent one comes at once.
                .Delivery(delivery => delivery
                    .DailyDigest(new TimeOnly(7, 30))
                    .WorkdaysOnly()
                    .ImmediatelyFrom(NhAssistantAlertUrgency.Urgent))
                .PersonMayChange(NhAssistantAlertChoices.All)
                // A lock screen shows the title only, never the open tasks.
                .PushShows(NhAssistantAlertPushContent.TitleOnly))
            .ConfigureAgent(AgentId, agent => agent
                .WorksOn(ProjectSubjects.Type)
                .HideFromChat()
                .UseRuleHandler<ProjectDeadlineAlertRules>()
                // The only kind the watch's rules may raise; another fails their run.
                .Alerts(KindId))
            .AddScheduledTrigger(TriggerId, schedule => schedule
                .StartAgent(AgentId)
                .Cron("0 7 * * 1-5", TimeZone)
                .Subjects(ProjectsDueToday)
                .Message(tick => $"Morning round: project {tick.Subject.Id} may be due today.")
                .MaxSubjectsPerTick(100)
                .OnBusy(NhAssistantTriggerConflict.Ignore)));
        return services;
    }

    /// <summary>
    /// The day in the sample's time zone of <paramref name="moment"/>.
    /// </summary>
    public static DateOnly DayOf(DateTimeOffset moment)
    {
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, TimeZone).DateTime);
    }

    /// <summary>
    /// The start of <paramref name="day"/> in the sample's time zone, in UTC.
    /// </summary>
    public static DateTimeOffset StartOf(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>
    /// The alert about <paramref name="projectKey"/> due on <paramref name="day"/>: one per project and
    /// deadline day, so a moved deadline is another alert, and it expires when the day ends.
    /// </summary>
    public static NhAssistantAlert AlertFor(
        Guid projectId,
        string projectKey,
        DateOnly day,
        int openTasks,
        IReadOnlyList<string> recipients)
    {
        return new NhAssistantAlert(KindId, ProjectSubjects.For(projectId))
        {
            Key = "deadline:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Parameters = new Dictionary<string, string>
            {
                ["project"] = projectKey,
                ["deadline"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["openTasks"] = openTasks.ToString(CultureInfo.InvariantCulture)
            },
            Recipients = recipients,
            ExpiresAt = StartOf(day.AddDays(1))
        };
    }

    /// <summary>
    /// The projects that are due today and not completed or archived: the subjects of the morning round.
    /// The day is captured in UTC, so the query stays server-side on SQL Server and PostgreSQL alike.
    /// </summary>
    private static async IAsyncEnumerable<NhAiSubjectRef> ProjectsDueToday(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var today = DayOf(services.GetRequiredService<TimeProvider>().GetUtcNow());
        var from = StartOf(today);
        var until = StartOf(today.AddDays(1));
        var dbContext = services.GetRequiredService<SampleProjectManagementDbContext>();
        var projectIds = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Status != ProjectStatus.Completed
                && project.Status != ProjectStatus.Archived
                && project.Deadline != null
                && project.Deadline >= from
                && project.Deadline < until)
            .OrderBy(project => project.Deadline)
            .ThenBy(project => project.Id)
            .Select(project => project.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var projectId in projectIds)
        {
            yield return ProjectSubjects.For(projectId);
        }
    }

    private static string Format(string name, CultureInfo culture, params object[] values)
    {
        var text = Texts.GetString(name, culture) ?? throw new InvalidOperationException($"The sample text {name} is missing.");
        return string.Format(culture, text, values);
    }
}

/// <summary>
/// SPM-392: the deadline watch's rules. They read the project as it is now, because a round may come
/// late or twice: a project due today with open tasks raises its alert for the people who work on it,
/// next to its owner; anything else resolves the project's open alerts of the kind, whatever their key.
/// The summary is the application's own text in each language.
/// </summary>
public sealed class ProjectDeadlineAlertRules(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer,
    TimeProvider clock) : INhAssistantAgentRuleHandler
{
    public async Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var subject = context.Subject.Subject;
        var today = SampleDeadlineAlertsComposition.DayOf(clock.GetUtcNow());
        var project = Guid.TryParse(subject.Id, out var projectId)
            ? await dbContext.Projects
                .AsNoTracking()
                .Where(item => item.Id == projectId)
                .Select(item => new
                {
                    item.Key,
                    item.Status,
                    item.Deadline,
                    OpenTasks = item.Tasks.Count(task => !task.IsCompleted),
                    Workers = item.Members
                        .Where(member => member.Role >= ProjectMemberRole.Participant)
                        .OrderBy(member => member.UserId)
                        .Select(member => member.UserId)
                        .ToList()
                })
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var due = project is { Deadline: { } deadline, OpenTasks: > 0 }
            && project.Status is not (ProjectStatus.Completed or ProjectStatus.Archived)
            && SampleDeadlineAlertsComposition.DayOf(deadline) == today;
        if (!due)
        {
            // Done, moved or gone: the warning no longer applies, whichever day it was raised for.
            return NhAssistantRuleResult.Done(_ => localizer["DeadlineWatchClear"].Value)
                .ResolveAlerts(new NhAssistantAlertKey(SampleDeadlineAlertsComposition.KindId, subject, key: null));
        }

        return NhAssistantRuleResult.Done(_ => localizer["DeadlineWatchWarned", project!.OpenTasks].Value)
            .WithAlerts(SampleDeadlineAlertsComposition.AlertFor(
                projectId,
                project!.Key,
                today,
                project.OpenTasks,
                project.Workers.Select(userId => userId.ToString("D")).ToArray()));
    }
}

/// <summary>
/// SPM-392: the application's own code warns about a project due today without the watch, for example
/// when a project manager asks to warn colleagues, and resolves the warning when the project is done.
/// It raises the watch's kind through <see cref="INhAssistantAlerts"/>, so the same project and day stay
/// one alert. The platform checks every person named here exactly as it checks the rules' recipients.
/// </summary>
public sealed class SampleDeadlineAlerts(
    INhAssistantAlerts alerts,
    SampleProjectManagementDbContext dbContext,
    TimeProvider clock)
{
    /// <summary>The project is not due today with open tasks, so there is nothing to warn about.</summary>
    public const string NotDueToday = "sample-project-not-due-today";

    public async Task<TaskResult<NhAssistantAlertReceipt>> WarnAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> people,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(people);
        var today = SampleDeadlineAlertsComposition.DayOf(clock.GetUtcNow());
        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(item => item.Id == projectId)
            .Select(item => new { item.Key, item.Deadline, OpenTasks = item.Tasks.Count(task => !task.IsCompleted) })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is not { Deadline: { } deadline, OpenTasks: > 0 } || SampleDeadlineAlertsComposition.DayOf(deadline) != today)
        {
            return TaskResult<NhAssistantAlertReceipt>.Failed(NotDueToday, "sample.project-not-due-today");
        }

        return await alerts.RaiseAsync(
            SampleDeadlineAlertsComposition.AlertFor(
                projectId,
                project.Key,
                today,
                project.OpenTasks,
                people.Select(userId => userId.ToString("D")).ToArray()),
            cancellationToken);
    }

    public Task<TaskResult<int>> ResolveAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return alerts.ResolveAsync(
            new NhAssistantAlertKey(SampleDeadlineAlertsComposition.KindId, ProjectSubjects.For(projectId), key: null),
            cancellationToken);
    }
}
