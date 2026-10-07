using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SampleProjectManagement.Api.Controllers;
using System.Reflection;
using Xunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.OpenApiSchemaTransformers;
using Scalar.AspNetCore;
using Newtonsoft.Json.Linq;

namespace SampleProjectManagement.Core.Tests;

public sealed class ControllerOpenApiMetadataTests
{
    [Fact]
    public async Task SelectedProjectContractIsDiscoverableThroughOpenApiAndScalarWithoutInfrastructure()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(ProjectController).Assembly);
        builder.Services.AddOpenApi("v1", options =>
        {
            options.AddSchemaTransformer<OneOfSchemaTransformer>();
            options.AddOperationTransformer<SampleProjectManagement.Api.Models.ProjectCollectionOpenApiTransformer>();
        });
        await using var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi();
        app.MapScalarApiReference();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var document = JObject.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        Assert.NotNull(document["paths"]!["/projects/fields"]);
        var successSchema = document["paths"]!["/projects"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        Assert.NotNull(successSchema["oneOf"]);
        Assert.Equal(2, successSchema["oneOf"]!.Count());
        Assert.Contains("selection", successSchema.ToString());
        Assert.NotNull(document["paths"]!["/projects"]!["get"]!["responses"]!["200"]!["content"]!["application/x-msgpack"]);
        var scalar = await client.GetStringAsync("/scalar/v1", TestContext.Current.CancellationToken);
        Assert.Contains("scalar", scalar, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EverySampleControllerActionHasScalarMetadataAndExplicitAuthorization()
    {
        var failures = typeof(HomeController).Assembly
            .GetTypes()
            .Where(type =>
                !type.IsAbstract &&
                type.Namespace == typeof(HomeController).Namespace &&
                typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(controllerType => controllerType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(IsHttpAction)
                .Select(action => ValidateAction(controllerType, action)))
            .Where(failure => failure is not null)
            .ToArray();

        Assert.True(
            failures.Length == 0,
            $"Every sample action must be useful in Scalar:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures));
    }

    private static bool IsHttpAction(MethodInfo method)
    {
        return method.GetCustomAttribute<NonActionAttribute>(inherit: true) is null &&
               method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any();
    }

    private static string? ValidateAction(Type controllerType, MethodInfo action)
    {
        var missing = new List<string>();

        if (action.GetCustomAttribute<EndpointSummaryAttribute>(inherit: true) is null)
        {
            missing.Add(nameof(EndpointSummaryAttribute));
        }

        if (action.GetCustomAttribute<EndpointDescriptionAttribute>(inherit: true) is null)
        {
            missing.Add(nameof(EndpointDescriptionAttribute));
        }

        if (!action.GetCustomAttributes<ProducesResponseTypeAttribute>(inherit: true).Any())
        {
            missing.Add(nameof(ProducesResponseTypeAttribute));
        }

        var authorizationMetadata = controllerType
            .GetCustomAttributes(inherit: true)
            .Concat(action.GetCustomAttributes(inherit: true));
        if (!authorizationMetadata.Any(attribute =>
                attribute is IAuthorizeData || attribute is IAllowAnonymous))
        {
            missing.Add("AuthorizeAttribute/AllowAnonymousAttribute");
        }

        return missing.Count == 0
            ? null
            : $"- {controllerType.Name}.{action.Name}: {string.Join(", ", missing)}";
    }
}
