using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Extensions;
using NewHeap.Platform.Common.Utilities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable examples for the small Common helpers that are easy to overlook in
/// an application flow. Every test is intentionally written as a usage example.
/// </summary>
public class CommonUtilitySamplesTests
{
    [Fact]
    public async Task WhereIfAndIncludeIfOnlyChangeTheQueryWhenEnabled()
    {
        var options = new DbContextOptionsBuilder<UtilitySampleDbContext>()
            .UseInMemoryDatabase($"conditional-query-{Guid.NewGuid()}")
            .Options;

        await using var dbContext = new UtilitySampleDbContext(options);
        dbContext.Projects.AddRange(
            new UtilityProject { Id = 1, Name = "Platform", Tasks = [new UtilityTask { Id = 10, Title = "Document" }] },
            new UtilityProject { Id = 2, Name = "Website" });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var allProjects = await dbContext.Projects
            .WhereIf(false, project => project.Name == "Platform")
            .ToListAsync();
        var platform = await dbContext.Projects
            .WhereIf(true, project => project.Name == "Platform")
            .IncludeIf(true, project => project.Tasks)
            .SingleAsync();

        Assert.Equal(2, allProjects.Count);
        Assert.Single(platform.Tasks);
    }

    [Fact]
    public void ExpressionHelpersComposeDynamicAndOrPredicates()
    {
        Expression<Func<UtilityProject, bool>> active = project => project.IsActive;
        Expression<Func<UtilityProject, bool>> namedPlatform = project => project.Name == "Platform";

        var strictMatch = NhExpressionExtensions.True<UtilityProject>()
            .And(active)
            .And(namedPlatform)
            .Compile();
        var broadMatch = NhExpressionExtensions.False<UtilityProject>()
            .Or(active)
            .Or(namedPlatform)
            .Compile();

        Assert.True(strictMatch(new UtilityProject { IsActive = true, Name = "Platform" }));
        Assert.False(strictMatch(new UtilityProject { IsActive = false, Name = "Platform" }));
        Assert.True(broadMatch(new UtilityProject { IsActive = false, Name = "Platform" }));
    }

    [Fact]
    public async Task DictionaryGetOrAddCallsSyncAndAsyncFactoriesOnlyOnce()
    {
        var cache = new Dictionary<string, int>();
        var syncFactoryCalls = 0;
        var asyncFactoryCalls = 0;

        var first = cache.GetOrAdd("project-count", () => ++syncFactoryCalls);
        var cached = cache.GetOrAdd("project-count", () => ++syncFactoryCalls);
        var asyncFirst = await cache.GetOrAddAsync("task-count", async cancellationToken =>
        {
            await Task.Delay(1, cancellationToken);
            return ++asyncFactoryCalls;
        });
        var asyncCached = await cache.GetOrAddAsync("task-count", _ => Task.FromResult(++asyncFactoryCalls));

        Assert.Equal((1, 1), (first, cached));
        Assert.Equal((1, 1), (asyncFirst, asyncCached));
        Assert.Equal((1, 1), (syncFactoryCalls, asyncFactoryCalls));
    }

    [Fact]
    public void PageSkipTakeNormalizesInputForListsAndQueries()
    {
        var numbers = Enumerable.Range(1, 8).ToList();

        Assert.Equal([4, 5, 6], numbers.PageSkipTake(page: 2, itemsPerPage: 3));
        Assert.Equal([1, 2], numbers.AsQueryable().PageSkipTake(page: 0, itemsPerPage: 2));
        Assert.Empty(numbers.PageSkipTake(page: 1, itemsPerPage: -1));
    }

    [Fact]
    public void StringAndAttributeHelpersMakeBoundariesExplicit()
    {
        var model = new UtilityMutateModel { Code = "TOO-LONG" };

        Assert.Equal("TOO-L", model.StringGuidelineMaxLength(item => item.Code));
        Assert.Equal(5, model.TryGetAttribute<UtilityMutateModel, StringLengthAttribute>(item => item.Code)?.MaximumLength);
        Assert.Equal("abc", "abcdef".SafeMaxStringLength(3));
        Assert.True("1".ToBoolean());
        Assert.False("not-a-boolean".ToBoolean());
        Assert.Equal("Project", "<strong>Project</strong>".StripHTML());
        Assert.Contains(Environment.NewLine, "{\"project\":true}".FormatJson());
    }

    [Fact]
    public void TypeHelpersDescribeInstantiationGenericsAndPropertyPaths()
    {
        Assert.True(typeof(UtilityProject).CanBeInstantiated());
        Assert.False(typeof(AbstractUtilityProject).CanBeInstantiated());
        Assert.Equal(0, typeof(int).GetDefaultValueOfType());
        Assert.True(typeof(List<>).IsGenericTypeOf(typeof(List<string>)));
        Assert.True(typeof(List<string>).IsGenericInterfaceImplemented(typeof(IEnumerable<>)));
        Assert.True(typeof(DateOnly).IsSimpleType());
        Assert.Contains(typeof(UtilityProject).TraversePropertiesInOrder(BindingFlags.Public | BindingFlags.Instance),
            item => item.prop.Name == nameof(UtilityProject.Name));
        Assert.Contains(typeof(DerivedUtilityProject).GetBaseClasses(), type => type == typeof(UtilityProject));
    }

    [Fact]
    public void EfModelHelpersReturnQuotedTableAndColumnNames()
    {
        var options = new DbContextOptionsBuilder<UtilitySampleDbContext>()
            .UseInMemoryDatabase($"model-metadata-{Guid.NewGuid()}")
            .Options;
        using var dbContext = new UtilitySampleDbContext(options);

        Assert.Equal("[pm].[UtilityProjects]", dbContext.Model.Table<UtilityProject>());
        Assert.Equal("[pm].[UtilityProjects].[project_name]", dbContext.Model.Column<UtilityProject>(project => project.Name));
        Assert.Equal("[project_name]", dbContext.Model.Column<UtilityProject>(project => project.Name, prefixTable: false));
    }

    [Fact]
    public async Task HashCultureAndStopwatchHelpersHaveDeterministicObservableResults()
    {
        const string value = "Sample Project Management";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value));

        var stringHash = HashUtils.GetMD5Hash(value);
        var streamHash = await HashUtils.GetMD5Hash(stream);
        var deterministicHash = HashUtils.GetDeterministicHashCode(value);
        string? formattedBudget = null;
        await GlobalizationUtils.TaskWithCultureAsync(
            CultureInfo.GetCultureInfo("nl-NL"),
            () =>
            {
                formattedBudget = 1234.5m.ToString("N1");
                return Task.CompletedTask;
            });
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(2);
        var elapsed = stopwatch.StopElapsed();

        Assert.Equal(stringHash, streamHash);
        Assert.Equal(HashUtils.GetDeterministicHashCode(value), deterministicHash);
        Assert.Equal("1.234,5", formattedBudget);
        Assert.False(stopwatch.IsRunning);
        Assert.True(elapsed > TimeSpan.Zero);
    }

    private sealed class UtilitySampleDbContext(DbContextOptions<UtilitySampleDbContext> options)
        : DbContext(options)
    {
        public DbSet<UtilityProject> Projects => Set<UtilityProject>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UtilityProject>(entity =>
            {
                entity.ToTable("UtilityProjects", "pm");
                entity.HasKey(project => project.Id);
                entity.Property(project => project.Name).HasColumnName("project_name");
                entity.HasMany(project => project.Tasks)
                    .WithOne()
                    .HasForeignKey(task => task.ProjectId);
            });
            modelBuilder.Entity<UtilityTask>().HasKey(task => task.Id);
        }
    }

    private class UtilityProject
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
        public ICollection<UtilityTask> Tasks { get; set; } = [];
    }

    private sealed class DerivedUtilityProject : UtilityProject;
    private abstract class AbstractUtilityProject;

    private sealed class UtilityTask
    {
        public int Id { get; set; }
        public int ProjectId { get; set; }
        public string Title { get; set; } = "";
    }

    private sealed class UtilityMutateModel
    {
        [StringLength(5)]
        public string Code { get; set; } = "";
    }
}
