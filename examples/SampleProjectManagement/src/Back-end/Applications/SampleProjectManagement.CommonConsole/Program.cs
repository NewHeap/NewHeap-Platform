using System.Text.Json;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;

// SPM-216: a domain consumer can use Common without an ASP.NET host or SQL storage.
var validation = TaskResult.Failed("project-name", "A project name is required.");
var operation = TaskResult.Succeeded();
validation.ApplyTo(operation);

if (operation.Success || operation.GetResultItems().Single().Name != "project-name")
{
    throw new InvalidOperationException("The enclosing operation must preserve validation failures.");
}

var queueResolver = new NhHangfireQueueNameResolver();
if (string.IsNullOrWhiteSpace(queueResolver.GetQueueName("project-maintenance")))
{
    throw new InvalidOperationException("The neutral Hangfire queue helper must remain available.");
}

var assemblyName = typeof(Program).Assembly.GetName().Name;
using var runtime = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.runtimeconfig.json")));
var options = runtime.RootElement.GetProperty("runtimeOptions");
var frameworks = options.TryGetProperty("frameworks", out var multiple)
    ? multiple.EnumerateArray().ToArray()
    : [options.GetProperty("framework")];

if (frameworks.Any(framework => framework.GetProperty("name").GetString() == "Microsoft.AspNetCore.App"))
{
    throw new InvalidOperationException("Common must not require the ASP.NET shared framework.");
}

using var dependencies = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.deps.json")));
foreach (var library in dependencies.RootElement.GetProperty("libraries").EnumerateObject())
{
    if (library.Name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
        || library.Name.StartsWith("Hangfire.AspNetCore/", StringComparison.Ordinal)
        || library.Name.StartsWith("Hangfire.SqlServer/", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Unexpected web or SQL storage dependency: {library.Name}");
    }
}

Console.WriteLine("Common validation and queue helpers work without ASP.NET or Hangfire SQL storage.");
