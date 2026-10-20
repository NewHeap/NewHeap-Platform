using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.Tests.Relational;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class RepositoryProviderBoundaryTests
{
    /// <summary>
    /// The provider-neutral package (no provider) restores neither provider; each provider package
    /// restores only its own. Only the SQL Server case names SQL Server, so <c>test:affected</c> runs
    /// the others with the PostgreSQL tests.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public void RestoredDependenciesRespectProviderBoundaries(RelationalTestProvider? provider)
    {
        var (suffix, forbidden) = provider switch
        {
            null => (string.Empty, new[] { "SqlServer", "SqlClient", "Npgsql", "PostgreSql" }),
            RelationalTestProvider.SqlServer => (".SqlServer", new[] { "Npgsql", "PostgreSql" }),
            _ => (".PostgreSql", new[] { "SqlServer", "SqlClient" })
        };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "release", "manifest.json")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var project = "NewHeap.Platform.AspNet.Common" + suffix;
        var assetsPath = Path.Combine(root.FullName, "src", "Back-end", "Libraries", project, "obj", "project.assets.json");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            Assert.DoesNotContain(forbidden, name => library.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public void IdentityFactoryRetainsTheSelectedProviderAndOptions(RelationalTestProvider provider)
    {
        var factory = new InternalNhIdentityDbContextFactory<
            IdentityContext, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole,
            NhDivisionRoleClaim, NhUser, NhUserRole, NhLog, NhLogMessageArgument, NhLogFile,
            NhLogMessageTranslated>(options =>
            {
                if (provider == RelationalTestProvider.SqlServer)
                {
                    options.UseNewHeapSqlServer("Server=localhost;Database=sample;Integrated Security=true",
                        provider => provider.CommandTimeout(37));
                }
                else
                {
                    options.UseNewHeapPostgreSql("Host=localhost;Database=sample;Username=postgres",
                        provider => provider.CommandTimeout(37));
                }
            });

        using var context = factory.CreateDbContext();
        Assert.Equal(provider == RelationalTestProvider.SqlServer ? "Microsoft.EntityFrameworkCore.SqlServer" : "Npgsql.EntityFrameworkCore.PostgreSQL",
            context.Database.ProviderName);
        Assert.Equal(37, context.Database.GetCommandTimeout());
        Assert.Equal(context.Database.ProviderName, NhRepositoryProvider.GetRequired(context).ProviderName);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task NativeEfRegistrationWithoutNewHeapFailsBeforeReadingInput(RelationalTestProvider provider)
    {
        var options = new DbContextOptionsBuilder<IdentityContext>();
        if (provider == RelationalTestProvider.SqlServer)
        {
            options.UseSqlServer("Server=localhost;Database=sample;Integrated Security=true");
        }
        else
        {
            options.UseNpgsql("Host=localhost;Database=sample;Username=postgres");
        }

        using var context = new IdentityContext(options.Options);
        using var services = new ServiceCollection().BuildServiceProvider();
        var repository = new Repository<NhDivision>(context, services);
        var enumerated = false;
        IEnumerable<NhDivision> Rows()
        {
            enumerated = true;
            yield return new NhDivision();
        }

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            repository.ExecuteUpsertAsync(Rows(), division => division.Id));
        Assert.Contains(provider == RelationalTestProvider.SqlServer ? "UseNewHeapSqlServer" : "UseNewHeapPostgreSql", error.Message);
        Assert.False(enumerated);
    }

    public sealed class IdentityContext(DbContextOptions<IdentityContext> options) : NhIdentityDbContext(options);
}
