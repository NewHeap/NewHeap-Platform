using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SampleProjectManagement.Api.Controllers;
using System.Reflection;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class ControllerOpenApiMetadataTests
{
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
