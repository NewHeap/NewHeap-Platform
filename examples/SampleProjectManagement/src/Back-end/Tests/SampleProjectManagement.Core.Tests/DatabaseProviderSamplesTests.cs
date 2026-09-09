using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.SqlServer;
using SampleProjectManagement.DAL;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class DatabaseProviderSamplesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompositionSelectsOneProviderAndPreservesNativeOptions(bool sqlServer)
    {
        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>();
        if (sqlServer)
        {
            options.UseNewHeapSqlServer("Server=localhost;Database=sample;Integrated Security=true",
                provider => provider.CommandTimeout(45));
        }
        else
        {
            options.UseNewHeapPostgreSql("Host=localhost;Database=sample;Username=postgres",
                provider => provider.CommandTimeout(45));
        }

        using var context = new SampleProjectManagementDbContext(options.Options);
        Assert.Equal(sqlServer ? "Microsoft.EntityFrameworkCore.SqlServer" : "Npgsql.EntityFrameworkCore.PostgreSQL",
            context.Database.ProviderName);
        Assert.Equal(45, context.Database.GetCommandTimeout());
    }
}
