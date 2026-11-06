using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.Core.Utilities;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using System.Net;
using System.Text.Json;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-007 and SPM-397: the anonymous public catalog over HTTP, with the API's mapping profile and on
/// PostgreSQL as the sample runs. It answers only active and completed projects, as the lightweight
/// short model, and searches and pages in the database; the search ignores case. The catalog once
/// failed on every request because its mapping was missing, which no reflection test could see:
/// only a request that maps a page proves it.
/// </summary>
public sealed class PublicProjectCatalogSamplesTests(SamplePostgreSqlServer postgreSql)
{
    private static readonly Guid DivisionId = Guid.Parse("7c1d4f53-3b55-4e47-9b0e-0d6c1f6a2a01");

    [Fact]
    public async Task TheAnonymousCatalogSearchesAndPagesActiveAndCompletedProjects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var application = BuildApplication(await postgreSql.CreateMigratedDatabaseAsync("public_catalog"));
        await SeedAsync(application.Services, cancellationToken);
        await application.StartAsync(cancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(application.Urls.Single()) };

        // Without a search: the active and completed projects by name, never a draft, a project on
        // hold or an archived one.
        var catalog = await GetPageAsync(client, "/public/projects?page=1&itemsPerPage=10", cancellationToken);
        Assert.Equal(["Harbor Bridge", "New HARBOR terminal", "River Walk"], catalog.Names);
        Assert.Equal(3, catalog.TotalCount);

        // A mixed-case search with paging: the second of the two matches, counted in the database.
        var searched = await GetPageAsync(client, "/public/projects?page=2&itemsPerPage=1&search=hArBoR", cancellationToken);
        Assert.Equal(["New HARBOR terminal"], searched.Names);
        Assert.Equal(2, searched.TotalCount);
        Assert.Equal(2, searched.Page);
        Assert.Equal(1, searched.ItemsPerPage);

        // The search covers the key too, and every item is the short model: id, key and name only.
        var byKey = await GetPageAsync(client, "/public/projects?page=1&itemsPerPage=8&search=rw-", cancellationToken);
        Assert.Equal(["River Walk"], byKey.Names);
        Assert.Equal(["id", "key", "name"], byKey.ItemProperties);
    }

    private static async Task<CatalogPage> GetPageAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} answered {(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var items = Property(root, "items").EnumerateArray().ToList();
        return new CatalogPage(
            Property(root, "page").GetInt32(),
            Property(root, "itemsPerPage").GetInt32(),
            Property(root, "totalCount").GetInt64(),
            items.Select(item => Property(item, "name").GetString() ?? "").ToList(),
            items.SelectMany(item => item.EnumerateObject().Select(property => property.Name.ToLowerInvariant()))
                .Distinct()
                .Order()
                .ToList());
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new InvalidOperationException($"The response has no '{name}'.");
    }

    private static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        context.Set<NhDivision>().Add(new NhDivision { Id = DivisionId, Name = "Public catalog division", TimeZoneId = "Europe/Amsterdam" });
        context.Set<Project>().AddRange(
            NewProject("HB-1", "Harbor Bridge", ProjectStatus.Active),
            NewProject("NHT-2", "New HARBOR terminal", ProjectStatus.Completed),
            NewProject("RW-3", "River Walk", ProjectStatus.Active),
            NewProject("HD-4", "Harbor draft", ProjectStatus.Draft),
            NewProject("HO-5", "Harbor on hold", ProjectStatus.OnHold),
            NewProject("HA-6", "Harbor archive", ProjectStatus.Archived));
        await context.SaveChangesAsync(cancellationToken);
    }

    private static Project NewProject(string key, string name, ProjectStatus status)
    {
        return new Project
        {
            Id = Guid.NewGuid(),
            DivisionId = DivisionId,
            Key = key,
            Name = name,
            Status = status
        };
    }

    /// <summary>
    /// The parts of the API's composition in Program.cs that the catalog uses: the platform with the
    /// sample's mapping profile on PostgreSQL, the catalog's repository and query service from
    /// AddSampleProjectManagementCore, and the API's controllers.
    /// </summary>
    private static WebApplication BuildApplication(string connectionString)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://sample-project-management.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] =
                "sample-project-management-public-catalog-signing-key",
        });

        builder.Services
            .AddNewHeapPlatformAspNetCommon<
                NhUser,
                NhUserRole,
                NhDivision,
                NhDivisionUser,
                NhDivisionRole,
                NhDivisionUserRole,
                NhDivisionRoleClaim,
                NhLog,
                NhLogMessageArgument,
                NhLogFile,
                NhLogMessageTranslated,
                NhDbLogService,
                SampleProjectManagementDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions
                    .Builder(builder.Configuration)
                    .UseEndpointRoutingOnly()
                    .ConfigureAutoMapper(options => options.AddMaps(typeof(AutomapperProfileConfiguration).Assembly))
                    .Build())
            .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql(connectionString))
            .WithIdentity()
            .WithDbLogService(_ => { });

        builder.Services.AddScopedNhDbRepository<Project>();
        builder.Services.AddScoped<ProjectCollectionSampleService>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(PublicProjectCatalogController).Assembly);

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapControllers();
        return application;
    }

    private sealed record CatalogPage(int Page, int ItemsPerPage, long TotalCount, List<string> Names, List<string> ItemProperties);
}
