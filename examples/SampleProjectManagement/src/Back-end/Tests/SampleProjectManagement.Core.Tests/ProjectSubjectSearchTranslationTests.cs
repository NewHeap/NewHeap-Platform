using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-301: <see cref="AssistantAgentRunSamplesTests"/> runs the project search on PostgreSQL; here
/// both providers translate the same query without a database. The class needs no host, so the
/// SQL Server case runs on its own without starting the agent-run host or a container.
/// </summary>
public sealed class ProjectSubjectSearchTranslationTests
{
    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void The_project_search_stays_one_case_insensitive_server_side_query_on_both_providers(DatabaseProvider provider)
    {
        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>();
        if (provider == DatabaseProvider.SqlServer)
        {
            options.UseSqlServer("Server=localhost;Database=TranslationOnly;Integrated Security=true");
        }
        else
        {
            options.UseNpgsql("Host=localhost;Database=translation_only");
        }

        using var dbContext = new SampleProjectManagementDbContext(options.Options);
        var sql = ProjectSubjectAccessResolver.Find(dbContext.Projects, Guid.NewGuid(), "Find", 20).ToQueryString();

        Assert.Contains("lower(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(provider == DatabaseProvider.SqlServer ? "TOP" : "LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }
}
