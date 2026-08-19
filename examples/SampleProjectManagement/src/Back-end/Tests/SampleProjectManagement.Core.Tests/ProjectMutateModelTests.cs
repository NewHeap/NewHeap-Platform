using SampleProjectManagement.Core.Models.Mutate;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public class ProjectMutateModelTests
{
    [Fact]
    public void MutateModelDoesNotExposeAuditFields()
    {
        var properties = typeof(ProjectMutateModel)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("CreationDateTime", properties);
        Assert.DoesNotContain("LastModifiedDateTime", properties);
    }
}
