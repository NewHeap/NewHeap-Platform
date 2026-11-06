using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL.Entities;
using System.ComponentModel;
using System.Linq.Expressions;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// Query policy for the public catalog, short projections and the executable
/// collection-expression resolver and nested-filter samples.
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

    /// <summary>
    /// The public catalog for anonymous readers: active and completed projects as the lightweight
    /// <see cref="ProjectShortViewModel"/>, ordered by name. A caller may page and search the
    /// searchable key and name; the search, the count and the page run in the database, and only the
    /// page is mapped. Filter and ordering input is not offered to anonymous callers.
    /// </summary>
    public async Task<SimpleCollectionResultModel<ProjectShortViewModel>> GetPublicCatalogAsync(
        ISearchableBaseCollectionRequestModel requestModel,
        CancellationToken cancellationToken = default)
    {
        var request = new CollectionRequestModel
        {
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage,
            Search = requestModel.Search
        };

        var result = await _collectionProcessingService.GetCollectionResultModelAsync<Project, ProjectShortViewModel>(
            request,
            GetPublicCatalogQuery(),
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken,
            (project => project.Name, ListSortDirection.Ascending));

        return new SimpleCollectionResultModel<ProjectShortViewModel>
        {
            Items = result.Items,
            TotalCount = result.TotalCount,
            ResultCount = result.ResultCount,
            Page = result.Page,
            ItemsPerPage = result.ItemsPerPage
        };
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
        // SPM-033 demonstrates key inference: the public key maps to the inferred
        // path Tasks{any}.Title, which runs as an any filter over the
        // ICollection<ProjectTask> navigation.
        var request = new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 10,
            Filter =
            [
                new FilterCollectionRequestModel
                {
                    Key = "open-task-title",
                    Operator = "==",
                    Value = taskTitle.Trim()
                }
            ]
        };

        var result = await _collectionProcessingService.GetCollectionResultModelAsync<Project, ProjectViewModel>(
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
            GeneratedExpression = $"WithFilterable accepted the selector for {taskTitle.Trim()}.",
            MatchCount = result.TotalCount
        };
    }

    /// <summary>
    /// SPM-034: filters and orders projects through the owner navigation and the task collection with
    /// the view-model attributes. Every segment of a filter key must be <c>[Filterable]</c> on
    /// <see cref="ProjectNestedFilterViewModel"/> or the nested view model it reaches, so a key that
    /// names an entity member the views do not expose fails with a safe <c>filter</c> error instead
    /// of becoming a predicate. Every segment of an order key must be <c>[Orderable]</c> the same way;
    /// any other order key, such as one that continues behind <c>creationDateTime</c>, is ignored and
    /// the projects keep the default order by name.
    /// </summary>
    public async Task<TaskResult<CollectionResultModel<ProjectNestedFilterViewModel>>> GetByNestedFiltersAsync(
        CollectionRequestModel request,
        CancellationToken cancellationToken = default)
    {
        var query = _repository.GetAll()
            .Include(project => project.OwnerUser)
            .Include(project => project.Tasks);

        try
        {
            var result = await _collectionProcessingService.GetCollectionResultModelAsync<Project, ProjectNestedFilterViewModel>(
                request,
                query,
                resultQueryableFunc: null,
                cancellationToken: cancellationToken,
                (project => project.Name, ListSortDirection.Ascending));

            return TaskResult<CollectionResultModel<ProjectNestedFilterViewModel>>.Succeeded(result);
        }
        catch (InvalidFilterCollectionResultException)
        {
            return TaskResult<CollectionResultModel<ProjectNestedFilterViewModel>>.Failed(
                "filter",
                "The filter field or its value is not valid for this collection.");
        }
    }
}
