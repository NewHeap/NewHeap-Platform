using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using Newtonsoft.Json.Linq;
using NSubstitute;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.Core.Utilities;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using System.Collections;
using System.Collections.ObjectModel;
using System.Security.Claims;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class BackendLibraryPartialSamplesTests
{
    [Fact]
    public void PublicAndCompositeControllersUseTheConcreteNewHeapBases()
    {
        Assert.True(typeof(PublicNhBaseController)
            .IsAssignableFrom(typeof(PublicProjectCatalogController)));
        Assert.True(typeof(CompositeDbEntityProtectedNhBaseController<
                Project,
                ProjectMutateModel,
                Project,
                ProjectCompositeViewModel,
                ProjectCompositeService,
                ProjectCollectionRequestModel>)
            .IsAssignableFrom(typeof(ProjectCompositeController)));
        Assert.True(typeof(CompositeBaseDbEntityService<
                Project,
                ProjectMutateModel,
                Project,
                ProjectCompositeService>)
            .IsAssignableFrom(typeof(ProjectCompositeService)));
    }

    [Fact]
    public void CompositeMappingIncludesTheProjectAndItsTasks()
    {
        var mapper = new Mapper(new MapperConfiguration(configuration =>
            configuration.AddProfile<AutomapperProfileConfiguration>()));
        var project = NewProject();
        project.Tasks.Add(new ProjectTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Composite task"
        });

        var result = mapper.Map<ProjectCompositeViewModel>(project);

        Assert.Equal(project.Id, result.Project.Id);
        Assert.Equal("Composite task", Assert.Single(result.Tasks).Title);
    }

    [Fact]
    public void AdvancedMappingUsesNullSafeMapFromValidationDependencyInjectionConstructionAndActions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProjectMappingLabelFormatter>();
        services.AddTransient<ProjectDisplayNameResolver>();
        services.AddTransient<ProjectReferenceConverter>();
        services.AddTransient<ProjectMappingEnrichmentAction>();
        services.AddAutoMapper(configuration =>
            configuration.AddProfile<ProjectMappingFeatureProfile>());

        using var provider = services.BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfigurationProvider>();
        configuration.AssertConfigurationIsValid();
        var mapper = provider.GetRequiredService<IMapper>();
        var project = NewProject();
        project.Description = "Mapped through the NewHeap profile";

        var summary = mapper.Map<ProjectMappingSummaryViewModel>(project);
        var reference = mapper.Map<ProjectReferenceValue>(project);
        var detail = mapper.Map<ProjectMappingDetailViewModel>(project);

        Assert.Equal(project.Key, summary.Key);
        Assert.Equal($"{project.Key} · {project.Name}", summary.DisplayName);
        Assert.Equal(project.Description, summary.Description);
        Assert.Null(summary.OwnerUser);
        Assert.Equal(nameof(ProjectMappingEnrichmentAction), summary.EnrichedBy);
        Assert.Equal($"{project.Key}:{project.Id:N}", reference.Value);
        Assert.Equal($"{project.Key} · {project.Name}", detail.DisplayName);
        Assert.Equal(project.Description, detail.Description);
        Assert.Equal(project.Status.ToString(), detail.Metadata["status"]);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(detail.Metadata);
    }

    [Fact]
    public void DictionaryLikeJsonObjectsUseTheirGenericDictionaryEntries()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var source = JObject.Parse("""
            {
              "name": "sample project",
              "enabled": true
            }
            """);

        var result = mapper.Map<JObject>(source);

        Assert.True(JToken.DeepEquals(source, result));
        Assert.Equal("sample project", result.Value<string>("name"));
        Assert.True(result.Value<bool>("enabled"));
    }

    [Fact]
    public void GenericCollectionsMaterializeCompatibleListAndSetDestinations()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();

        var list = mapper.Map<ArrayList>(new[] { 1, 2 });
        var set = mapper.Map<IReadOnlySet<int>>(new[] { 1, 1, 2 });
        var readOnlyList = mapper.Map<ReadOnlyCollection<int>>(new[] { 1, 2 });
        var keyValue = mapper.Map<KeyValuePair<string, int>>(
            new KeyValuePair<int, string>(1, "42"));

        Assert.Equal([1, 2], list.Cast<int>());
        Assert.Equal([1, 2], set.Order());
        Assert.Equal([1, 2], readOnlyList);
        Assert.Equal(new KeyValuePair<string, int>("1", 42), keyValue);
    }

    [Fact]
    public void DuplicateProfileCompositionUsesTheLastMapAndReportsBothProfilesDuringValidation()
    {
        var configuration = new MapperConfiguration(options =>
        {
            options.AddProfile<FirstProjectSummaryProfile>();
            options.AddProfile<LastProjectSummaryProfile>();
        });
        var project = NewProject();

        var result = configuration.CreateMapper().Map<ProjectMappingSummaryViewModel>(project);
        var exception = Assert.Throws<MappingConfigurationException>(
            configuration.AssertConfigurationIsValid);

        Assert.Equal($"last:{project.Name}", result.DisplayName);
        Assert.Contains(nameof(FirstProjectSummaryProfile), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(LastProjectSummaryProfile), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeAwareMappingUpdatesScalarsWithoutReplacingNavigationProperties()
    {
        var mapper = new Mapper(new MapperConfiguration(configuration =>
            configuration.AddProfile<AutomapperProfileConfiguration>()));
        var project = NewProject();
        var division = new NhDivision { Id = project.DivisionId, Name = "Delivery" };
        var tasks = project.Tasks;
        tasks.Add(new ProjectTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Retained navigation"
        });
        project.Division = division;
        project.Description = "Before mapping";

        var result = mapper.Map(
            new ProjectMutateModel
            {
                DivisionId = project.DivisionId,
                OwnerUserId = project.OwnerUserId,
                Key = project.Key,
                Name = project.Name,
                Description = "After mapping",
                Status = project.Status,
                Deadline = project.Deadline
            },
            project);

        Assert.Same(project, result);
        Assert.Equal("After mapping", result.Description);
        Assert.Same(division, result.Division);
        Assert.Same(tasks, result.Tasks);
        Assert.Equal("Retained navigation", Assert.Single(result.Tasks).Title);
    }

    [Fact]
    public async Task ShortProjectionAndCollectionResolverExecuteTheRealExtensions()
    {
        await using var dbContext = new SampleProjectManagementDbContext(
            new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var project = NewProject();
        project.Tasks.Add(new ProjectTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Resolver task"
        });
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();

        var mapper = new Mapper(new MapperConfiguration(configuration =>
            configuration.AddProfile<AutomapperProfileConfiguration>()));
        var processor = new CollectionProcessingService(mapper);
        var repository = Substitute.For<IRepository<Project>>();
        repository.Context.Returns(dbContext);
        repository.GetAll().Returns(dbContext.Projects);
        var service = new ProjectCollectionSampleService(repository, processor);

        var shortResult = await service.GetShortAsync();
        var expressionResult = await service.ResolveOpenTaskTitleExpressionAsync("Resolver task");

        Assert.Equal(project.Id, Assert.Single(shortResult.Items).Id);
        Assert.Equal("Tasks{any}.Title", expressionResult.ResolvedPath);
        Assert.True(expressionResult.IsSupported);
        Assert.Null(expressionResult.Limitation);
        Assert.Contains("WithFilterable accepted", expressionResult.GeneratedExpression, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitTransactionCommitsBothWritesOrRollsBackBoth(bool failTask)
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var steps = new List<string>();
        var projectRepository = Substitute.For<IRepository<Project>>();
        var taskRepository = Substitute.For<IRepository<ProjectTask>>();
        var transaction = Substitute.For<ITransaction>();
        transaction.DisposeAsync().Returns(ValueTask.CompletedTask);
        transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            steps.Add("commit");
            return Task.CompletedTask;
        });
        transaction.RollbackAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            steps.Add("rollback");
            return Task.CompletedTask;
        });
        projectRepository.StartTransactionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(transaction));

        var mapper = Substitute.For<IMapper>();
        var project = NewProject();
        var task = new ProjectTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Initial task"
        };
        mapper.Map<ProjectViewModel>(Arg.Any<object>())
            .Returns(new ProjectViewModel { Id = project.Id, Key = project.Key, Name = project.Name });
        mapper.Map<ProjectTaskViewModel>(Arg.Any<object>())
            .Returns(new ProjectTaskViewModel { Id = task.Id, ProjectId = project.Id, Title = task.Title });

        var projectService = Substitute.For<ProjectService>(
            projectRepository,
            CreateDbLogService(),
            CreateLogHelper(),
            mapper,
            Substitute.For<IStringLocalizer<ProjectService>>(),
            new ValidationService(serviceProvider),
            Substitute.For<INhEventPublisher>());
        projectService.CreateAsync(
                Arg.Any<ProjectMutateModel>(),
                Arg.Any<Guid?>(),
                Arg.Any<Action<Project>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<BaseDbEntityServiceOperationOptions?>())
            .Returns(_ =>
            {
                steps.Add("project");
                return Task.FromResult(TaskResult<Project?>.Succeeded(project));
            });

        var taskService = Substitute.For<ProjectTaskService>(
            taskRepository,
            CreateDbLogService(),
            CreateLogHelper(),
            mapper,
            Substitute.For<IStringLocalizer<ProjectTaskService>>(),
            new ValidationService(serviceProvider),
            Substitute.For<INhEventPublisher>());
        taskService.CreateAsync(
                Arg.Any<ProjectTaskMutateModel>(),
                Arg.Any<Guid?>(),
                Arg.Any<Action<ProjectTask>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<BaseDbEntityServiceOperationOptions?>())
            .Returns(_ =>
            {
                steps.Add("task");
                if (!failTask)
                {
                    return Task.FromResult(TaskResult<ProjectTask?>.Succeeded(task));
                }

                var failed = new TaskResult<ProjectTask?>();
                failed.AddError(nameof(ProjectTaskMutateModel.Title), "Task sample failed");
                return Task.FromResult(failed);
            });

        var service = new ProjectSetupService(projectRepository, projectService, taskService, mapper);
        var result = await service.CreateWithInitialTaskAsync(new ProjectWithInitialTaskMutateModel
        {
            Project = new ProjectMutateModel
            {
                DivisionId = project.DivisionId,
                Key = project.Key,
                Name = project.Name
            },
            InitialTaskTitle = task.Title
        });

        if (failTask)
        {
            Assert.False(result.Success);
            Assert.Equal(["project", "task", "rollback"], steps);
            await transaction.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
            await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.True(result.Success);
            Assert.Equal(project.Id, result.Data?.Project.Id);
            Assert.Equal(["project", "task", "commit"], steps);
            await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
            await transaction.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public void MatchOneAttributeAllowsPermissionOrRoleAndRejectsNeither()
    {
        var service = new ProjectAuthorizationSampleService();
        var editor = Principal(new Claim(NhPlatformClaimTypes.Permission, "app.project.manage"));
        var administrator = Principal(new Claim(ClaimTypes.Role, "administrator"));
        var viewer = Principal(new Claim(NhPlatformClaimTypes.Permission, "app.project.view"));

        Assert.True(service.EvaluateEditorAccess(editor).Allowed);
        Assert.True(service.EvaluateEditorAccess(administrator).Allowed);
        Assert.False(service.EvaluateEditorAccess(viewer).Allowed);
        Assert.Equal(2, service.EvaluateEditorAccess(viewer).RequiredOneOf.Count);
    }

    private static Project NewProject()
    {
        return new Project
        {
            Id = Guid.NewGuid(),
            DivisionId = Guid.NewGuid(),
            Key = "STEP-2",
            Name = "Backend partial samples",
            Status = ProjectStatus.Active
        };
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "sample"));
    }

    private static NhDbLogService CreateDbLogService()
    {
        return new NhDbLogService(
            Options.Create(new DbLogServiceSettings()),
            Substitute.For<IRepository<NhLog>>(),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IStringLocalizer<NhDbLogService>>(),
            Options.Create(new NewHeapAspNetCommonSettings()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NhDbLogService>.Instance);
    }

    private static LogHelperService CreateLogHelper()
    {
        return new LogHelperService(
            Substitute.For<IStringLocalizer<SharedDataAnnotationRecources>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LogHelperService>.Instance);
    }

    private sealed class FirstProjectSummaryProfile : Profile
    {
        public FirstProjectSummaryProfile()
        {
            CreateMap<Project, ProjectMappingSummaryViewModel>()
                .ConstructUsing(project => new ProjectMappingSummaryViewModel(project.Key))
                .ForMember(
                    destination => destination.DisplayName,
                    options => options.MapFrom(project => $"first:{project.Name}"))
                .ForMember(destination => destination.OwnerUser, options => options.Ignore())
                .ForMember(destination => destination.EnrichedBy, options => options.Ignore());
        }
    }

    private sealed class LastProjectSummaryProfile : Profile
    {
        public LastProjectSummaryProfile()
        {
            CreateMap<Project, ProjectMappingSummaryViewModel>()
                .ConstructUsing(project => new ProjectMappingSummaryViewModel(project.Key))
                .ForMember(
                    destination => destination.DisplayName,
                    options => options.MapFrom(project => $"last:{project.Name}"))
                .ForMember(destination => destination.OwnerUser, options => options.Ignore())
                .ForMember(destination => destination.EnrichedBy, options => options.Ignore());
        }
    }
}
