using System.Collections.Concurrent;
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
/// The lifecycle (FB87 phase 2, lane AR): the kind chooses the project's members who work on it
/// (<c>SubjectMembers(Participant)</c>), so the rules name nobody themselves. It resolves by omission
/// (<c>ResolveWhenNotRaised</c>): a round that finds the project no longer due raises nothing, and the
/// watch's open warning about it is resolved. The kind also declares its check
/// (<see cref="ProjectDueTodayAlertCheck"/>), which the sample's task and status consumers run through
/// <c>INhAssistantAlerts.RevalidateAsync</c> after a change committed, so ticking off the last task
/// resolves the warning at once, also one the application raised, without waiting for a round.
/// The delivery (FB87, lane AB): the morning round runs at 07:00 in the sample's time zone and the kind
/// arrives in the 07:30 digest, one notification per person, in the person's own time zone: the time
/// zone of the division they work in (<see cref="SamplePersonTimeZones"/>), so a colleague whose
/// division is in New York gets it at 07:30 there. A person who muted the kind in their notification
/// preferences receives nothing. The digest reaches people through Web Push, the in-app notifications
/// and the sample's stand-in for an e-mail channel (<see cref="SampleAlertOutbox"/>).
/// The administration (FB87 phase 2, lane AD): the kind lets an administrator switch it off and change
/// its default delivery (<c>AdministratorMayChange</c>), which the Alerts section of the sample's
/// assistant administration does through <c>PUT admin/alert-kinds/{id}</c>; the delivery and a person's
/// preferences then follow the administrator's settings, and a kind that is off raises nothing.
/// Call it after <c>AddSampleIntakeReviewer</c>, which registers the project subject type.
/// </summary>
public static class SampleDeadlineAlertsComposition
{
    public const string AgentId = "sample-deadline-watch";
    public const string KindId = "project-due-today";
    public const string TriggerId = "project-deadline-watch";

    /// <summary>The portal's Alerts page, which lists every alert of a person (SPM-392).</summary>
    public const string AlertListUrl = "/management/alerts";

    /// <summary>The front-end translation key of the kind's name.</summary>
    public const string KindNameKey = "nh-assistant.alert-kinds.project-due-today.name";

    /// <summary>The time zone in which a deadline falls on a day, as the sample's divisions keep it.</summary>
    public static TimeZoneInfo TimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    private static readonly ResourceManager Texts = new(typeof(SampleAssistantToolPresenter));

    public static IServiceCollection AddSampleDeadlineAlerts(this IServiceCollection services)
    {
        services.TryAddScoped<SampleDeadlineAlerts>();
        services.TryAddSingleton<SampleAlertOutbox>();
        services.AddNewHeapAssistant(assistant => assistant
            // The digest and working days count in the application's time zone, unless the person's
            // own division is elsewhere. The in-app notification of alerts links to the portal's Alerts page.
            .ConfigureAlerts(alerts =>
            {
                alerts.TimeZone = TimeZone.Id;
                alerts.ListUrl = AlertListUrl;
            })
            .UsePersonTimeZones<SamplePersonTimeZones>()
            .AddAlertChannel<SampleAlertOutboxChannel>()
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
                // The owner, as the project's resolver names its responsible person, the members who
                // work on the project and the people the application names; each one is checked
                // before anything reaches them.
                .Recipients(NhAssistantAlertRecipients.SubjectResponsible
                    .Or(NhAssistantAlertRecipients.SubjectMembers(NhAiSubjectRole.Participant))
                    .Or(NhAssistantAlertRecipients.Explicit))
                // A project due today is warned about once per day, however many rounds raise it.
                .RepeatAfter(TimeSpan.FromDays(1))
                // By default in the morning and afternoon digests on working days; an urgent one comes at
                // once, but nothing between 20:00 and 07:00: a warning raised at night waits for the morning.
                .Delivery(delivery => delivery
                    .DailyDigests(new TimeOnly(7, 30), new TimeOnly(16, 0))
                    .WorkdaysOnly()
                    .ImmediatelyFrom(NhAssistantAlertUrgency.Urgent)
                    .QuietHours(new TimeOnly(20, 0), new TimeOnly(7, 0)))
                // A person mutes the kind, chooses their own digest times, quiet hours and channels, and
                // asks to hear nothing about one project for a while; a required kind would allow none of
                // the muting.
                .PersonMayChange(NhAssistantAlertChoices.All)
                // An administrator may switch the warning off and move its digest; recipients and texts stay code.
                .AdministratorMayChange(NhAssistantAlertAdminChoices.SwitchOff | NhAssistantAlertAdminChoices.Delivery)
                // A lock screen shows the title only, never the open tasks.
                .PushShows(NhAssistantAlertPushContent.TitleOnly)
                // A round that no longer raises the warning about its project resolves the watch's own.
                .ResolveWhenNotRaised()
                // After a task or status change the application revalidates the project's alerts.
                .Revalidate<ProjectDueTodayAlertCheck>())
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
    /// Whether a project is due on <paramref name="day"/> with work left: not completed or archived, its
    /// deadline on that day and tasks still open. The watch's rules and its check ask the same.
    /// </summary>
    public static bool IsDue(ProjectStatus status, DateTimeOffset? deadline, int openTasks, DateOnly day)
    {
        return deadline is { } due
            && openTasks > 0
            && status is not (ProjectStatus.Completed or ProjectStatus.Archived)
            && DayOf(due) == day;
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
    /// The moment the clock in <paramref name="zone"/> shows <paramref name="time"/> on
    /// <paramref name="day"/>, in UTC.
    /// </summary>
    public static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
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
/// late or twice: a project due today with open tasks raises its alert, which the kind delivers to its
/// owner and the members who work on it. Anything else raises nothing, and the kind resolves the watch's
/// open warning about the project by omission (<c>ResolveWhenNotRaised</c>). The summary is the
/// application's own text in each language.
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
                    OpenTasks = item.Tasks.Count(task => !task.IsCompleted)
                })
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        if (project is null || !SampleDeadlineAlertsComposition.IsDue(project.Status, project.Deadline, project.OpenTasks, today))
        {
            // Done, moved or gone: the round raises nothing, so the kind resolves the watch's open
            // warning about the project, whichever day it was raised for.
            return NhAssistantRuleResult.Done(_ => localizer["DeadlineWatchClear"].Value);
        }

        // The kind chooses the owner and the members who work on the project; the rules name nobody.
        return NhAssistantRuleResult.Done(_ => localizer["DeadlineWatchWarned", project.OpenTasks].Value)
            .WithAlerts(SampleDeadlineAlertsComposition.AlertFor(
                projectId,
                project.Key,
                today,
                project.OpenTasks,
                []));
    }
}

/// <summary>
/// SPM-392: the check of the watch's kind (FB87-10), which the platform runs when the application
/// revalidates a project's alerts after a task or status change committed: a warning still applies while
/// the project is due on the warning's day, today, with tasks open; otherwise it is resolved. It reads the
/// project as it is now and changes nothing.
/// </summary>
public sealed class ProjectDueTodayAlertCheck(SampleProjectManagementDbContext dbContext, TimeProvider clock) : INhAssistantAlertCheck
{
    public async ValueTask<NhAssistantAlertValidity> CheckAsync(NhAssistantAlertCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var today = SampleDeadlineAlertsComposition.DayOf(clock.GetUtcNow());
        if (!Guid.TryParse(context.Subject.Id, out var projectId)
            || !context.Parameters.TryGetValue("deadline", out var deadline)
            || !DateOnly.TryParseExact(deadline, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || day != today)
        {
            return NhAssistantAlertValidity.Resolve;
        }

        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(item => item.Id == projectId)
            .Select(item => new { item.Status, item.Deadline, OpenTasks = item.Tasks.Count(task => !task.IsCompleted) })
            .SingleOrDefaultAsync(cancellationToken);
        return project is not null && SampleDeadlineAlertsComposition.IsDue(project.Status, project.Deadline, project.OpenTasks, day)
            ? NhAssistantAlertValidity.StillValid
            : NhAssistantAlertValidity.Resolve;
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

/// <summary>
/// SPM-392: a person's time zone for the digest of alerts, from the profile the platform keeps: the time
/// zone of the division they work in (<c>NhUser.ActiveDivision</c>). A person without one gets the
/// application's time zone for alerts.
/// </summary>
public sealed class SamplePersonTimeZones(SampleProjectManagementDbContext dbContext) : INhAssistantPersonTimeZones
{
    public async ValueTask<string?> FindTimeZoneIdAsync(string actorId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(actorId, out var userId))
        {
            return null;
        }

        var timeZoneId = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.ActiveDivision != null)
            .Select(user => user.ActiveDivision!.TimeZoneId)
            .SingleOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(timeZoneId) ? null : timeZoneId;
    }
}

/// <summary>
/// SPM-392: one notification of alerts as the sample's outbox keeps it: to whom, when on the
/// application's clock, the digest if any, its title and the alerts it names.
/// </summary>
public sealed record SampleAlertOutboxEntry(
    string RecipientActorId,
    DateTimeOffset SentAt,
    Guid? DigestId,
    string Title,
    IReadOnlyList<Guid> AlertIds,
    int MoreCount);

/// <summary>
/// SPM-392: the sample's stand-in for an e-mail channel of alerts, since the sample sends no e-mail: it
/// keeps the latest notifications in memory, at most <see cref="Capacity"/>, as a mail catcher of a
/// development environment would. An application's real channel sends the message; it keeps the texts
/// out of its logs, because they are the application's data.
/// </summary>
public sealed class SampleAlertOutbox
{
    public const int Capacity = 200;

    private readonly ConcurrentQueue<SampleAlertOutboxEntry> _entries = new();

    public IReadOnlyList<SampleAlertOutboxEntry> Entries => _entries.ToArray();

    public void Add(SampleAlertOutboxEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }
}

/// <summary>
/// SPM-392: the application's own channel of alerts (<c>AddAlertChannel</c>) next to Web Push and the
/// in-app notifications. The assistant calls it for every notification that goes out: one alert, or the
/// digest of one person at one digest time, after it checked the person again and applied their
/// choices. A failing channel never stops the others.
/// </summary>
public sealed class SampleAlertOutboxChannel(SampleAlertOutbox outbox, TimeProvider clock) : INhAssistantAlertChannel
{
    public string Id => "e-mail";

    public Task<TaskResult> DeliverAsync(NhAssistantAlertMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        outbox.Add(new SampleAlertOutboxEntry(
            message.RecipientActorId,
            clock.GetUtcNow(),
            message.DigestId,
            message.Title,
            message.Alerts.Select(alert => alert.AlertId).ToArray(),
            message.MoreCount));
        return Task.FromResult(TaskResult.Succeeded());
    }
}
