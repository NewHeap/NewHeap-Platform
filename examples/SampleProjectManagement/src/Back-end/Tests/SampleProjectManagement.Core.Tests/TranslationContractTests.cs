using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public class TranslationContractTests
{
    [Fact]
    public void FrontendModuleTranslationsHaveEqualDashCaseKeys()
    {
        var root = FindRepositoryRoot();
        var i18n = Path.Combine(
            root,
            "examples", "SampleProjectManagement", "src", "Front-end", "projects", "management", "public", "i18n");
        using var nl = JsonDocument.Parse(File.ReadAllText(Path.Combine(i18n, "nl.json")));
        using var en = JsonDocument.Parse(File.ReadAllText(Path.Combine(i18n, "en.json")));

        var nlKeys = Keys(nl.RootElement.GetProperty("project"));
        var enKeys = Keys(en.RootElement.GetProperty("project"));

        Assert.Equal(nlKeys, enKeys);
        Assert.All(nlKeys, key => Assert.Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$", key));
    }

    [Fact]
    public void EverySampleTranslationResourceFamilyHasEqualKeys()
    {
        var root = FindRepositoryRoot();
        var sampleRoot = Path.Combine(root, "examples", "SampleProjectManagement");

        var jsonFamilies = Directory
            .EnumerateDirectories(Path.Combine(sampleRoot, "src", "Front-end", "projects"), "i18n", SearchOption.AllDirectories)
            .Select(directory => Directory.EnumerateFiles(directory, "*.json").Order().ToArray())
            .Where(files => files.Length > 0);

        foreach (var files in jsonFamilies)
        {
            AssertResourceKeysEqual(files, file =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                return FlattenJsonKeys(document.RootElement).ToHashSet(StringComparer.Ordinal);
            });
        }

        var resourceFiles = Directory.EnumerateFiles(
            Path.Combine(sampleRoot, "src", "Back-end", "Applications"), "*.resx", SearchOption.AllDirectories);
        var resxFamilies = resourceFiles.GroupBy(file =>
            Regex.Replace(file, @"\.[a-z]{2}-[A-Z]{2}\.resx$", string.Empty));

        foreach (var family in resxFamilies)
        {
            AssertResourceKeysEqual(family.Order().ToArray(), file =>
                XDocument.Load(file)
                    .Descendants("data")
                    .Select(element => element.Attribute("name")?.Value)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToHashSet(StringComparer.Ordinal));
        }
    }

    private static void AssertResourceKeysEqual(
        IReadOnlyList<string> files,
        Func<string, HashSet<string>> readKeys)
    {
        Assert.True(files.Count >= 2, $"Resource family has fewer than two cultures: {files.FirstOrDefault()}");
        var expected = readKeys(files[0]);

        foreach (var file in files.Skip(1))
        {
            var actual = readKeys(file);
            var missing = expected.Except(actual).Order().ToArray();
            var unexpected = actual.Except(expected).Order().ToArray();
            Assert.True(
                missing.Length == 0 && unexpected.Length == 0,
                $"Translation drift in {file}. Missing: [{string.Join(", ", missing)}]. Unexpected: [{string.Join(", ", unexpected)}].");
        }
    }

    private static IEnumerable<string> FlattenJsonKeys(JsonElement element, string prefix = "")
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in FlattenJsonKeys(property.Value, key))
                {
                    yield return child;
                }
            }
            else
            {
                yield return key;
            }
        }
    }

    private static SortedSet<string> Keys(JsonElement module) =>
        new(module.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
