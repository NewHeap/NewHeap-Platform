using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Resolvers;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class ProjectFieldSelectionSamplesTests
{
    [Fact]
    public async Task ProjectTableUsesRolesAndResourcePolicyAndPreservesDivisionScoping()
    {
        var division = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, SampleAuthorizationDefaults.ManagerRole),
            new Claim("sample.managed-division", division.ToString())
        ], "sample"));
        using var services = new ServiceCollection().AddLogging().AddAuthorization(options =>
            options.AddPolicy("app.active-division.project.manage", policy => policy.RequireAssertion(context =>
                context.Resource is Guid activeDivision && context.User.HasClaim("sample.managed-division", activeDivision.ToString()))))
            .BuildServiceProvider();
        var jsonOptions = new MvcNewtonsoftJsonOptions();
        new MvcNewtonsoftJsonOptionsWrapper().Configure(jsonOptions);
        var mapper = new Mapper(new MapperConfiguration(_ => { }));
        var fields = new NhFieldSelectionService(new CollectionProcessingService(mapper),
            services.GetRequiredService<IAuthorizationService>(), Options.Create(jsonOptions));
        var catalog = await fields.DescribeAsync<ProjectTableViewModel>(user, division);
        Assert.Contains(catalog.Fields, field => field.Name == "ownerUserId");
        Assert.Contains(catalog.Fields, field => field.Name == "description");
        var outsideDivision = await fields.DescribeAsync<ProjectTableViewModel>(user, Guid.NewGuid());
        Assert.DoesNotContain(outsideDivision.Fields, field => field.Name == "description");

        var projects = new[]
        {
            new Project { Id = Guid.NewGuid(), DivisionId = division, Name = "Selected project", Key = "SELECT", Description = "Allowed detail" },
            new Project { Id = Guid.NewGuid(), DivisionId = Guid.NewGuid(), Name = "Other division", Key = "OTHER" }
        }.AsQueryable();
        var context = new DefaultHttpContext { User = user };
        context.Request.QueryString = new QueryString("?fields=id,name,description");
        var result = await fields.GetCollectionAsync(context, new CollectionRequestModel(),
            projects.Where(project => project.DivisionId == division), ProjectTableViewModel.Projection, division);
        Assert.True(result.Success);
        var row = Assert.Single(result.Data!.Items);
        Assert.Equal("Selected project", row["name"]);
        Assert.Equal("Allowed detail", row["description"]);
        Assert.Equal(["id", "name", "description"], result.Data.Selection.ReturnedFields);
        Assert.DoesNotContain("ownerUserId", row.Keys);

        context.Request.QueryString = QueryString.Empty;
        Assert.False(NhFieldSelectionService.HasSelection(context.Request));
    }
}
