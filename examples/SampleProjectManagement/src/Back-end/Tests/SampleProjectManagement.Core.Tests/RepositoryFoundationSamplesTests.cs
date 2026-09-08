using System.Xml.Linq;
using Xunit;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-216: the consumer repository keeps shared build settings and all direct
/// NuGet versions beside the solution in src/Back-end.
/// </summary>
public sealed class RepositoryFoundationSamplesTests
{
    [Fact]
    public async Task CommonConsoleRunsWithoutAspNetOrHangfireSqlStorage()
    {
        var frameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = frameworkDirectory.Parent!.Name;
        var consolePath = Path.Combine(FindBackendRoot(), "Applications",
            "SampleProjectManagement.CommonConsole", "bin", configuration,
            frameworkDirectory.Name, "SampleProjectManagement.CommonConsole.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(consolePath);

        using var process = Process.Start(startInfo)!;
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"Common console failed: {output}{error}");
        Assert.Contains("without ASP.NET or Hangfire SQL storage", output);
    }

    [Fact]
    public void HttpCompositionAdaptsNeutralResultsToModelState()
    {
        var validation = TaskResult.Failed("project-name", "A project name is required.");
        var modelState = new ModelStateDictionary();

        validation.ApplyToModelState(modelState);

        Assert.False(modelState.IsValid);
        Assert.Equal("A project name is required.", modelState["project-name"]!.Errors.Single().ErrorMessage);
    }

    [Fact]
    public void BackendProjectsUseSharedBuildAndCentralPackageFiles()
    {
        var backendRoot = FindBackendRoot();
        Assert.Equal("Back-end", new DirectoryInfo(backendRoot).Name);
        Assert.Equal("src", Directory.GetParent(backendRoot)?.Name);
        var sampleRoot = Directory.GetParent(Directory.GetParent(backendRoot)!.FullName)!.FullName;
        Assert.False(File.Exists(Path.Combine(sampleRoot, "SampleProjectManagement.slnx")));
        Assert.False(File.Exists(Path.Combine(sampleRoot, "Directory.Build.props")));
        Assert.False(File.Exists(Path.Combine(sampleRoot, "Directory.Packages.props")));
        var buildProps = XDocument.Load(Path.Combine(backendRoot, "Directory.Build.props"));
        var packageProps = XDocument.Load(Path.Combine(backendRoot, "Directory.Packages.props"));

        Assert.Equal("net10.0", Property(buildProps, "TargetFramework"));
        Assert.Equal("enable", Property(buildProps, "Nullable"));
        Assert.Equal("enable", Property(buildProps, "ImplicitUsings"));
        Assert.Equal("false", Property(buildProps, "IsPackable"));
        Assert.Equal("true", Property(packageProps, "ManagePackageVersionsCentrally"));

        var centralVersions = packageProps
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageVersion")
            .ToDictionary(
                element => RequiredAttribute(element, "Include"),
                element => RequiredAttribute(element, "Version"),
                StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(centralVersions);

        var projectFiles = Directory.EnumerateFiles(
            backendRoot,
            "*.csproj",
            SearchOption.AllDirectories);

        foreach (var projectFile in projectFiles)
        {
            var project = XDocument.Load(projectFile);
            foreach (var packageReference in project
                         .Descendants()
                         .Where(element => element.Name.LocalName == "PackageReference"))
            {
                var packageId = RequiredAttribute(packageReference, "Include");
                Assert.Null(packageReference.Attribute("Version"));
                Assert.Null(packageReference.Attribute("VersionOverride"));
                Assert.DoesNotContain(
                    packageReference.Elements(),
                    element => element.Name.LocalName is "Version" or "VersionOverride");
                Assert.True(
                    centralVersions.ContainsKey(packageId),
                    $"{Path.GetRelativePath(backendRoot, projectFile)} references {packageId} without a central PackageVersion.");
            }
        }
    }

    private static string FindBackendRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SampleProjectManagement.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the SampleProjectManagement backend root.");
    }

    private static string Property(XDocument document, string name) =>
        document.Descendants().Single(element => element.Name.LocalName == name).Value;

    private static string RequiredAttribute(XElement element, string name) =>
        element.Attribute(name)?.Value
        ?? throw new InvalidDataException($"{element.Name.LocalName} is missing its {name} attribute.");
}
