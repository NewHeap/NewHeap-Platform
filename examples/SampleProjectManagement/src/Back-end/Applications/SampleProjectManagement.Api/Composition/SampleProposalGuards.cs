using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AI;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-282: closes the intake reviewer's proposal to complete a task once someone completed the
/// task another way, typically by ticking it off by hand. The proposal is indexed by its task, so
/// the update of the task revalidates it wherever it waits. The explanation names who completed
/// the task, in each reader's language. Registered for the proposal stage too, it keeps the reviewer
/// from proposing a task that is already completed: the reviewer reads the explanation instead.
/// </summary>
public sealed class ProjectTaskProposalGuard(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAiProposalGuard
{
    public const string KeyType = "project-task";
    public const string CompletedCode = "project-task-completed";
    public const string DeletedCode = "project-task-deleted";

    public static NhAiProposalKey Key(Guid taskId)
    {
        return new NhAiProposalKey(KeyType, taskId.ToString("D"));
    }

    public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>(
            Read(context) is { } input ? [Key(input.TaskId)] : []);
    }

    public async ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        if (Read(context) is not { } input)
        {
            return NhAiPreconditionResult.Satisfied;
        }

        var task = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(item => item.Id == input.TaskId)
            .Select(item => new { item.IsCompleted, item.CompletedAt, item.CompletedByUserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (task is null)
        {
            return NhAiPreconditionResult.NotApplicable(DeletedCode, _ => localizer["StaleTaskDeleted"].Value);
        }
        if (!task.IsCompleted)
        {
            return NhAiPreconditionResult.Satisfied;
        }

        var completedBy = task.CompletedByUserId is { } userId
            ? await dbContext.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.UserName)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var completedAt = task.CompletedAt ?? DateTimeOffset.UtcNow;
        // The platform calls the explanation once per supported culture, with that culture current.
        // Naming who did the work lets clients show it without parsing the explanation.
        return NhAiPreconditionResult.AlreadyDone(CompletedCode, culture => completedBy is null
            ? localizer["StaleTaskCompleted", completedAt.ToString("d", culture)].Value
            : localizer["StaleTaskCompletedBy", completedBy, completedAt.ToString("d", culture)].Value) with
        {
            ByActorId = task.CompletedByUserId?.ToString("D")
        };
    }

    private static ProjectTaskCompleteInput? Read(NhAiProposalGuardContext context)
    {
        return context.ToolId == ProjectTaskAiTools.CompleteToolId
            ? context.Arguments.Deserialize<ProjectTaskCompleteInput>(JsonSerializerOptions.Web)
            : null;
    }
}

/// <summary>
/// SPM-282: closes a proposal to change a project's status once the project already has that
/// status, for example because a person set it by hand. Status changes revalidate the project's
/// open proposals as a subject, so this guard names no objects.
/// </summary>
public sealed class ProjectStatusProposalGuard(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAiProposalGuard
{
    public const string StatusSetCode = "project-status-already-set";

    public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>([]);
    }

    public async ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.ToolId != ProjectAiTools.ChangeStatusToolId
            || context.Arguments.Deserialize<ProjectAiStatusChangeInput>(JsonSerializerOptions.Web) is not { } input)
        {
            return NhAiPreconditionResult.Satisfied;
        }

        var current = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == input.ProjectId)
            .Select(project => (DAL.Entities.ProjectStatus?)project.Status)
            .SingleOrDefaultAsync(cancellationToken);
        return current == input.Status
            ? NhAiPreconditionResult.AlreadyDone(
                StatusSetCode,
                _ => localizer["StaleProjectStatusSet", localizer[$"ProjectStatus{input.Status}"].Value].Value)
            : NhAiPreconditionResult.Satisfied;
    }
}

/// <summary>
/// SPM-339: names what an equipment order changes, the order of one item for one project, so a newer
/// order of the same item for the same project (another quantity) is a newer proposal for the same
/// objects. The intake policy's duplicate rule then keeps one open order per item: a newer one replaces
/// the open one, live and in shadow, as an agent whose arguments change on every run would otherwise
/// pile up orders. The guard closes nothing: the order desk decides what an order still needs.
/// </summary>
public sealed class ProjectEquipmentProposalGuard : INhAiProposalGuard
{
    public const string KeyType = "project-equipment";

    public static NhAiProposalKey Key(Guid projectId, string item)
    {
        return new NhAiProposalKey(KeyType, projectId.ToString("D") + "/" + item.Trim().ToLowerInvariant());
    }

    public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>(
            Read(context) is { Item.Length: > 0 and <= ProjectAiEquipmentTools.MaxItemLength } input
                ? [Key(input.ProjectId, input.Item)]
                : []);
    }

    public ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(NhAiPreconditionResult.Satisfied);
    }

    private static ProjectAiEquipmentOrderInput? Read(NhAiProposalGuardContext context)
    {
        return context.ToolId == ProjectAiEquipmentTools.OrderEquipmentToolId
            ? context.Arguments.Deserialize<ProjectAiEquipmentOrderInput>(JsonSerializerOptions.Web)
            : null;
    }
}
