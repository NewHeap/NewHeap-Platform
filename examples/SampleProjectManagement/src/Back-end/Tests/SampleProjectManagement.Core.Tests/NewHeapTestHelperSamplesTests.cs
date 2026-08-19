using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Test;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Test;
using NewHeap.Platform.Common.Test.Extensions;
using NSubstitute;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Consumer usages of the reusable NewHeap test-helper packages; library self-tests live in separate non-packable projects.
/// SPM-173–176: examples for the reusable NewHeap test libraries themselves.
/// </summary>
public class NewHeapTestHelperSamplesTests
{
    [Fact]
    public async Task TestingContextBuildsAndDisposesAValidatedServiceProvider()
    {
        await using var context = new ProjectTestingContext();

        await context.BuildAsync();

        var clock = context.GetRequiredService<SampleClock>();
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero), clock.UtcNow);
        using var scope = context.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ScopedSample>());
    }

    [Fact]
    public async Task DbContextTestingContextAutoRegistersRepositoriesForDbSets()
    {
        await using var context = new NhDbContextTestingContext<SampleProjectManagementDbContext>();
        await context.BuildAsync();
        using var scope = context.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Project>>();
        var project = Project("TST", "Test helpers");

        repository.Add(project);
        await repository.SaveChangesAsync();
        repository.ClearTracking();

        var persisted = await repository.FindAsync(project.Id);
        Assert.Equal("TST", persisted?.Key);
        Assert.Equal("[Project]", repository.TableName);
    }

    [Fact]
    public void TaskResultAssertionsKeepSuccessAndErrorExamplesReadable()
    {
        var success = TaskResult.Succeeded().AsSuccess();
        var error = TaskResult.Failed("project", "Project is invalid").AsError();
        var typed = TaskResult<Project>.Succeeded(Project("OK", "Valid")).AsSuccess();

        Assert.True(success.Success);
        Assert.False(error.Success);
        Assert.Equal("OK", typed.Data?.Key);
    }

    [Fact]
    public async Task SubstitutePredicateExtensionsEvaluateAgainstRealSampleData()
    {
        var projects = new[]
        {
            Project("NHP", "Platform"),
            Project("WEB", "Website")
        };
        var port = Substitute.For<IProjectPredicatePort>();
        port.AnyAsync(Arg.Any<Expression<Func<Project, bool>>>()).ReturnsAny(projects);
        port.FirstAsync(Arg.Any<Expression<Func<Project, bool>>>()).ReturnsFirstOrDefault(projects);
        port.CountAsync(Arg.Any<Expression<Func<Project, bool>>>()).ReturnsCount(projects);

        Assert.True(await port.AnyAsync(project => project.Key == "NHP"));
        Assert.Equal("WEB", (await port.FirstAsync(project => project.Name == "Website"))?.Key);
        Assert.Equal(2, await port.CountAsync(project => project.Name.Length > 0));
    }

    private static Project Project(string key, string name) => new()
    {
        Id = Guid.NewGuid(),
        DivisionId = Guid.NewGuid(),
        Key = key,
        Name = name,
        Status = ProjectStatus.Active
    };

    private sealed class ProjectTestingContext : NhTestingContext
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(new SampleClock(
                new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero)));
            services.AddScoped<ScopedSample>();
        }
    }

    private sealed record SampleClock(DateTimeOffset UtcNow);

    private sealed class ScopedSample;

    public interface IProjectPredicatePort
    {
        Task<bool> AnyAsync(Expression<Func<Project, bool>> predicate);

        Task<Project?> FirstAsync(Expression<Func<Project, bool>> predicate);

        Task<int> CountAsync(Expression<Func<Project, bool>> predicate);
    }
}
