using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using NewHeap.Platform.Common.Utilities;
using Newtonsoft.Json.Linq;
using NSubstitute;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using System.Reflection;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public class ProjectPartialUpdateTests
{
    [Fact]
    public void SetPropertyChangesOnlyTheSelectedMutateModelProperty()
    {
        var model = new ProjectMutateModel
        {
            DivisionId = Guid.NewGuid(),
            Key = "NHP",
            Name = "NewHeap samples",
            Description = "Original description",
            Status = ProjectStatus.Draft
        };

        var setters = new NhSetPropertyCalls<ProjectMutateModel>()
            .SetProperty(project => project.Status, ProjectStatus.Active);

        setters.Apply(model);

        Assert.Equal(ProjectStatus.Active, model.Status);
        Assert.Equal("NHP", model.Key);
        Assert.Equal("NewHeap samples", model.Name);
        Assert.Equal("Original description", model.Description);
    }

    [Fact]
    public void ProjectControllerExposesExecutablePartialUpdateContract()
    {
        var action = typeof(ProjectController).GetMethod(nameof(ProjectController.UpdatePartial));

        Assert.NotNull(action);
        Assert.Equal(
            "{id:guid}",
            action.GetCustomAttribute<HttpPatchAttribute>()?.Template);
        Assert.Equal(
            typeof(JObject),
            action.GetParameters().Single(parameter =>
                parameter.GetCustomAttribute<FromBodyAttribute>() is not null).ParameterType);
        Assert.Contains(
            action.GetCustomAttributes<ProducesResponseTypeAttribute>(),
            attribute => attribute.StatusCode == StatusCodes.Status204NoContent);
        Assert.Contains(
            action.GetCustomAttributes<ProducesResponseTypeAttribute>(),
            attribute => attribute.StatusCode == StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ProjectPartialUpdateNormalizesBeforeValidation()
    {
        await using var dbContext = new SampleProjectManagementDbContext(
            new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            DivisionId = Guid.NewGuid(),
            Key = "NHP",
            Name = "NewHeap samples",
            Description = "Before",
            Status = ProjectStatus.Active
        };
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();

        var repository = Substitute.For<IRepository<Project>>();
        repository.Context.Returns(dbContext);
        repository.GetAll().Returns(dbContext.Projects);
        repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        repository.StartOrGetTransactionScopeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<INhDbTransactionScope>()));

        var mapper = Substitute.For<IMapper>();
        mapper.Map<ProjectMutateModel>(Arg.Any<object>())
            .Returns(call => ToMutateModel((Project)call.Arg<object>()));
        mapper.Map(Arg.Any<ProjectMutateModel>(), Arg.Any<Project>())
            .Returns(call => ApplyMutateModel(
                call.Arg<ProjectMutateModel>(),
                call.Arg<Project>()));
        var publisher = Substitute.For<INhEventPublisher>();
        var service = new ProjectService(
            repository,
            new NhDbLogService(
                Options.Create(new DbLogServiceSettings()),
                Substitute.For<IRepository<NhLog>>(),
                Substitute.For<IHttpContextAccessor>(),
                Substitute.For<IStringLocalizer<NhDbLogService>>(),
                Options.Create(new NewHeapAspNetCommonSettings())),
            new LogHelperService(Substitute.For<IStringLocalizer<SharedDataAnnotationRecources>>()),
            mapper,
            Substitute.For<IStringLocalizer<ProjectService>>(),
            new ValidationService(serviceProvider),
            publisher);
        var normalizedDescription = new string('x', 2000);

        var result = await service.UpdatePartialAsync(
            project.Id,
            calls => calls.SetProperty(
                model => model.Description,
                $"  {normalizedDescription}  "),
            options: new BaseDbEntityServiceOperationOptions
            {
                DbLoggingDisabled = true
            });

        Assert.True(result.Success);
        Assert.Equal(normalizedDescription, result.Data?.Description);
        await publisher.Received(1).PublishAsync(Arg.Is<ProjectUpdatedEvent>(@event =>
            @event.ProjectId == project.Id && @event.ChangeSet == "partial"));
    }

    private static ProjectMutateModel ToMutateModel(Project project)
    {
        return new ProjectMutateModel
        {
            DivisionId = project.DivisionId,
            OwnerUserId = project.OwnerUserId,
            Key = project.Key,
            Name = project.Name,
            Description = project.Description,
            Status = project.Status,
            Deadline = project.Deadline
        };
    }

    private static Project ApplyMutateModel(ProjectMutateModel model, Project project)
    {
        project.DivisionId = model.DivisionId;
        project.OwnerUserId = model.OwnerUserId;
        project.Key = model.Key;
        project.Name = model.Name;
        project.Description = model.Description;
        project.Status = model.Status;
        project.Deadline = model.Deadline;
        return project;
    }
}
