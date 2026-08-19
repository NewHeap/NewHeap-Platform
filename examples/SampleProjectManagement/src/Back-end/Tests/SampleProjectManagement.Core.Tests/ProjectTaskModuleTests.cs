using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.Common.Attributes;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public class ProjectTaskModuleTests
{
    [Fact]
    public void MutateModelDoesNotExposeAuditFields()
    {
        var properties = typeof(ProjectTaskMutateModel)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("CreationDateTime", properties);
        Assert.DoesNotContain("LastModifiedDateTime", properties);
    }

    [Fact]
    public void ViewModelIdIsFilterable()
    {
        var idProperty = typeof(ProjectTaskViewModel).GetProperty(nameof(ProjectTaskViewModel.Id));

        Assert.NotNull(idProperty);
        Assert.NotNull(idProperty!.GetCustomAttributes(typeof(FilterableAttribute), true).SingleOrDefault());
    }

    [Fact]
    public void ProjectTaskHasRequiredProjectRelationship()
    {
        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
            .UseInMemoryDatabase("SampleProjectManagementModelTest")
            .Options;
        using var dbContext = new SampleProjectManagementDbContext(options);

        var taskEntity = dbContext.Model.FindEntityType(typeof(ProjectTask));
        var foreignKey = taskEntity!.GetForeignKeys().Single(x =>
            x.PrincipalEntityType.ClrType == typeof(Project));

        Assert.True(foreignKey.IsRequired);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void ProjectStatusValuesMatchFrontendContract()
    {
        Assert.Equal(0, (int)ProjectStatus.Draft);
        Assert.Equal(1, (int)ProjectStatus.Active);
        Assert.Equal(2, (int)ProjectStatus.OnHold);
        Assert.Equal(3, (int)ProjectStatus.Completed);
        Assert.Equal(4, (int)ProjectStatus.Archived);
    }
}
