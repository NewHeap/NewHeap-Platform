using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL.Entities;
using System.ComponentModel;
using System.Linq.Expressions;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// Query policy for the public catalog, short projections and the executable
/// collection-expression resolver sample.
/// </summary>
public sealed class ProjectCollectionSampleService
{
    public static readonly Expression<Func<Project, ProjectShortViewModel>> ShortProjection =
        project => new ProjectShortViewModel
        {
            Id = project.Id,
            Key = project.Key,
            Name = project.Name
        };

    private readonly IRepository<Project> _repository;
    private readonly ICollectionProcessingService _collectionProcessingService;

    public ProjectCollectionSampleService(
        IRepository<Project> repository,
        ICollectionProcessingService collectionProcessingService)
    {
        _repository = repository;
        _collectionProcessingService = collectionProcessingService;
    }

    public IQueryable<Project> GetPublicCatalogQuery()
    {
        return _repository.GetAll()
            .Where(project =>
                project.Status == ProjectStatus.Active ||
                project.Status == ProjectStatus.Completed)
            .OrderBy(project => project.Name);
    }

    public IQueryable<Project> GetShortQuery()
    {
        return _repository.GetAll().OrderBy(project => project.Name);
    }

    public Task<SimpleCollectionResultModel<ProjectShortViewModel>> GetShortAsync()
    {
        var request = new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 50
        };

        return _collectionProcessingService.GetProjectedSimpleCollectionResultModelAsync(
            request,
            GetShortQuery(),
            ShortProjection,
            resultQueryableFunc: null,
            asNoTracking: false,
            cancellationToken: default,
            (view => (object)view.Name, ListSortDirection.Ascending));
    }

    public async Task<CollectionExpressionSampleViewModel> ResolveOpenTaskTitleExpressionAsync(
        string taskTitle,
        CancellationToken cancellationToken = default)
    {
        // SPM-033 demonstrates key inference. The request path it produces is
        // later consumed by collection processing; executing a concrete filter
        // is deliberately covered by the collection-filter cases instead.
        var request = new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 10
        };

        await _collectionProcessingService.GetCollectionResultModelAsync<Project, ProjectViewModel>(
            request,
            _repository.GetAll(),
            options => options.WithFilterable(
                "open-task-title",
                project => project.Tasks
                    .Select(task => task.Title)),
            cancellationToken: cancellationToken);

        return new CollectionExpressionSampleViewModel
        {
            InputKey = "open-task-title",
            ResolvedPath = "Tasks{any}.Title",
            GeneratedExpression = $"WithFilterable accepted the selector for {taskTitle.Trim()}."
        };
    }

}
