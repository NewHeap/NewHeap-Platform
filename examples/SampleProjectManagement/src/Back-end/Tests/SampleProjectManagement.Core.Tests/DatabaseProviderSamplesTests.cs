using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.SqlServer;
using SampleProjectManagement.DAL;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class DatabaseProviderSamplesTests
{
    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void CompositionSelectsOneProviderAndPreservesNativeOptions(DatabaseProvider provider)
    {
        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>();
        if (provider == DatabaseProvider.SqlServer)
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
        Assert.Equal(provider == DatabaseProvider.SqlServer ? "Microsoft.EntityFrameworkCore.SqlServer" : "Npgsql.EntityFrameworkCore.PostgreSQL",
            context.Database.ProviderName);
        Assert.Equal(45, context.Database.GetCommandTimeout());
    }
}
