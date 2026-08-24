using NewHeap.Platform.Mapping;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// SPM-054: creates a project and its first task atomically with the explicit
/// ITransaction API. Project and task services join the existing transaction;
/// their events therefore join the same CAP outbox transaction as well.
/// </summary>
public sealed class ProjectSetupService
{
    private readonly IRepository<Project> _projectRepository;
    private readonly ProjectService _projectService;
    private readonly ProjectTaskService _projectTaskService;
    private readonly IMapper _mapper;

    public ProjectSetupService(
        IRepository<Project> projectRepository,
        ProjectService projectService,
        ProjectTaskService projectTaskService,
        IMapper mapper)
    {
        _projectRepository = projectRepository;
        _projectService = projectService;
        _projectTaskService = projectTaskService;
        _mapper = mapper;
    }

    public async Task<TaskResult<ProjectCompositeViewModel>> CreateWithInitialTaskAsync(
        ProjectWithInitialTaskMutateModel mutateModel,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _projectRepository.StartTransactionAsync(cancellationToken);
        var result = new TaskResult<ProjectCompositeViewModel>();

        try
        {
            var projectResult = await _projectService.CreateAsync(
                mutateModel.Project,
                committedByUserId,
                cancellationToken: cancellationToken);
            projectResult.ApplyTo(result);
            if (!projectResult.Success || projectResult.Data is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return result;
            }

            var taskResult = await _projectTaskService.CreateAsync(
                new ProjectTaskMutateModel
                {
                    ProjectId = projectResult.Data.Id,
                    Title = mutateModel.InitialTaskTitle,
                    IsCompleted = false
                },
                committedByUserId,
                cancellationToken: cancellationToken);
            taskResult.ApplyTo(result);
            if (!taskResult.Success || taskResult.Data is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return result;
            }

            await transaction.CommitAsync(cancellationToken);
            result.Data = new ProjectCompositeViewModel
            {
                Project = _mapper.Map<ProjectViewModel>(projectResult.Data),
                Tasks = [_mapper.Map<ProjectTaskViewModel>(taskResult.Data)]
            };
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
