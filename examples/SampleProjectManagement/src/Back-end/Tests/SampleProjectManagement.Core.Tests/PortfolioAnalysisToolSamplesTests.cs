using Hangfire;
using Hangfire.InMemory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Jobs;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-222: the durable work behind the long-running MCP tool. <see cref="SampleProjectPortfolioAnalyses"/>
/// starts the portfolio analysis of SPM-083 as a background operation of the person in the active
/// division, finds it again by the person's request id, lets nobody else read it and waits until it
/// finished, through Hangfire on the migrated sample database.
/// </summary>
public sealed class PortfolioAnalysisToolSamplesTests(SamplePostgreSqlServer postgreSql)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task SPM_222_the_tool_starts_the_portfolio_analysis_once_per_request_id_and_waits_until_it_finished()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgreSql.CreateMigratedDatabaseAsync("portfolio_analysis_tool");
        using var hangfireStorage = new InMemoryStorage();
        using var host = BuildHost(connectionString, hangfireStorage);
        await host.StartAsync(cancellationToken);
        try
        {
            var (ownerId, divisionId) = await SeedAsync(host.Services);
            var actorId = ownerId.ToString("D");
            await using var scope = host.Services.CreateAsyncScope();
            var analyses = scope.ServiceProvider.GetRequiredService<IProjectPortfolioAnalyses>();

            var started = await analyses.StartAsync(divisionId, actorId, "request-1", cancellationToken);
            var again = await analyses.StartAsync(divisionId, actorId, "request-1", cancellationToken);

            Assert.True(started.Success);
            Assert.Equal(started.Data!.AnalysisId, again.Data!.AnalysisId);
            var otherPerson = await analyses.GetAsync(divisionId, Guid.NewGuid().ToString("D"), started.Data.AnalysisId, cancellationToken);
            Assert.Equal(ProjectPortfolioAnalysisAiTools.AnalysisNotFoundCode, otherPerson.GetResultItems().Single().Name);
            var otherDivision = await analyses.GetAsync(Guid.NewGuid(), actorId, started.Data.AnalysisId, cancellationToken);
            Assert.False(otherDivision.Success);

            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            patience.CancelAfter(Patience);
            var finished = await analyses.WaitAsync(divisionId, actorId, started.Data.AnalysisId, patience.Token);

            Assert.True(finished.Success);
            Assert.Equal(ProjectPortfolioAnalysisStatuses.Succeeded, finished.Data!.Status);
            Assert.True(finished.Data.Finished);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static IHost BuildHost(string connectionString, InMemoryStorage hangfireStorage)
    {
        // A worker host without a web server, with the operations of the portfolio analysis as Program.cs
        // registers them.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<SampleProjectManagementDbContext>(options =>
            options.UseNewHeapPostgreSql(connectionString));
        builder.Services.AddScoped<IRepository<Project>>(provider =>
            new Repository<Project>(provider.GetRequiredService<SampleProjectManagementDbContext>(), provider));
        builder.Services.AddSingleton(new ProjectAnalysisArchive(TimeSpan.Zero));
        builder.Services.AddNewHeapHangfireProcessing(
            hangfire => hangfire.UseStorage(hangfireStorage),
            backgroundJobServerOptions: server =>
            {
                server.ServerName = "portfolio-analysis-tool-" + Guid.NewGuid().ToString("N");
                server.WorkerCount = 2;
                server.SchedulePollingInterval = TimeSpan.FromMilliseconds(100);
            });
        builder.Services.AddNewHeapBackgroundOperations<SampleProjectManagementDbContext>(operations =>
        {
            operations.Options.DispatchInterval = TimeSpan.FromMilliseconds(200);
            operations.Options.LiveUpdatesEnabled = false;
            operations.Options.UserNotificationProjectionEnabled = false;
            operations.Options.CleanupEnabled = false;
            operations.Add<ProjectPortfolioAnalysisRequest, ProjectPortfolioAnalysisOperation>(
                SampleProjectPortfolioAnalyses.OperationType);
            operations.Add<ProjectAnalysisChildRequest, ProjectAnalysisChildOperation>(
                "sample-project-analysis-child");
        });
        builder.Services.AddScoped<IProjectPortfolioAnalyses, SampleProjectPortfolioAnalyses>();
        return builder.Build();
    }

    private static async Task<(Guid OwnerId, Guid DivisionId)> SeedAsync(IServiceProvider services)
    {
        var ownerId = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        dbContext.Users.Add(new NhUser
        {
            Id = ownerId,
            UserName = $"analyst-{ownerId:N}@example.test",
            NormalizedUserName = $"ANALYST-{ownerId:N}@EXAMPLE.TEST"
        });
        dbContext.Set<NhDivision>().Add(new NhDivision { Id = divisionId, Name = "Portfolio analysis" });
        await dbContext.SaveChangesAsync();
        return (ownerId, divisionId);
    }
}
