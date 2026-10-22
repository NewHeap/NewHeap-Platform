using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-309: the test infrastructure that keeps the sample's relational tests fast. The whole run
/// shares one PostgreSQL container (<see cref="SamplePostgreSqlServer"/>); every host gets a
/// database of its own, copied from a template that is migrated once
/// (<see cref="SamplePostgreSqlServer.CreateMigratedDatabaseAsync"/>), so test classes run in
/// parallel without seeing each other's rows; and a host's clock (<see cref="SampleTestClock"/>)
/// moves forward instead of a test waiting for a deadline.
/// </summary>
public sealed class FastTestInfrastructureSamplesTests(SamplePostgreSqlServer postgreSql)
{
    [Fact]
    public async Task MigratedDatabasesArriveMigratedAndDoNotShareRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // The databases of two hosts: the first call migrates the template, both are copies of it.
        var firstConnectionString = await postgreSql.CreateMigratedDatabaseAsync("fast_tests_first");
        var secondConnectionString = await postgreSql.CreateMigratedDatabaseAsync("fast_tests_second");
        var project = NewProject();

        await using (var first = CreateDbContext(firstConnectionString))
        {
            // The copy is ready to use: the test applies no migration.
            Assert.Empty(await first.Database.GetPendingMigrationsAsync(cancellationToken));

            first.Set<NhDivision>().Add(new NhDivision { Id = project.DivisionId, Name = "Fast tests" });
            first.Projects.Add(project);
            await first.SaveChangesAsync(cancellationToken);
        }

        await using (var second = CreateDbContext(secondConnectionString))
        {
            Assert.Empty(await second.Database.GetPendingMigrationsAsync(cancellationToken));

            // The other host's database holds none of the rows that the first host wrote.
            Assert.False(await second.Projects.AnyAsync(item => item.Id == project.Id, cancellationToken));
            Assert.False(await second.Set<NhDivision>().AnyAsync(item => item.Id == project.DivisionId, cancellationToken));
        }

        await using (var first = CreateDbContext(firstConnectionString))
        {
            Assert.True(await first.Projects.AnyAsync(item => item.Id == project.Id, cancellationToken));
        }
    }

    [Fact]
    public void MovingTheTestClockReleasesADeadlineWithoutWaiting()
    {
        var clock = new SampleTestClock();
        var services = new ServiceCollection();
        // A library registers the system clock when the application registers none ...
        services.TryAddSingleton(TimeProvider.System);
        // ... and a sample host replaces it with its test clock, which the NewHeap libraries then
        // read as the application's clock.
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        using var provider = services.BuildServiceProvider();
        var applicationClock = provider.GetRequiredService<TimeProvider>();
        Assert.Same(clock, applicationClock);

        // A deadline on the application's clock, such as a debounce, a NotBefore or an approval's
        // lifetime.
        var lifetime = TimeSpan.FromMinutes(15);
        var deadline = applicationClock.GetUtcNow() + lifetime;
        var systemStart = TimeProvider.System.GetUtcNow();
        Assert.True(applicationClock.GetUtcNow() < deadline);

        clock.Advance(lifetime);

        Assert.True(applicationClock.GetUtcNow() >= deadline);
        Assert.Equal(lifetime, clock.Offset);
        // The test did not wait for it: the system clock, which leases, heartbeats and audit
        // timestamps keep, is still far from the deadline.
        Assert.True(TimeProvider.System.GetUtcNow() < systemStart + lifetime);
        // The clock never moves back, so a deadline that passed stays passed.
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromMinutes(-1)));
    }

    private static SampleProjectManagementDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new SampleProjectManagementDbContext(options);
    }

    private static Project NewProject()
    {
        return new Project
        {
            Id = Guid.NewGuid(),
            DivisionId = Guid.NewGuid(),
            Key = "FAST",
            Name = "Fast relational tests",
            Status = ProjectStatus.Active
        };
    }
}
