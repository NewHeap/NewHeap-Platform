using System.Text.Json;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.DatabaseRead;

internal static partial class DatabaseReadProfileLoader
{
    private const string RelativeCatalogPath = ".newheap/database-read.json";
    private const int MaximumCatalogBytes = 256 * 1024;
    private const int MaximumProfileCount = 64;

    private static readonly DatabaseReadLimits HardLimits = new(
        MaximumRows: 100_000,
        TimeoutSeconds: 600,
        LockTimeoutMilliseconds: 60_000,
        MaximumOutputBytes: 64 * 1024 * 1024,
        MaximumCellBytes: 1024 * 1024,
        MaximumSqlBytes: 1024 * 1024);

    private static readonly DatabaseReadLimits DefaultLimits = new(
        MaximumRows: 200,
        TimeoutSeconds: 30,
        LockTimeoutMilliseconds: 5_000,
        MaximumOutputBytes: 1024 * 1024,
        MaximumCellBytes: 16 * 1024,
        MaximumSqlBytes: 32 * 1024);

    public static async Task<ResolvedDatabaseReadProfile> LoadAsync(
        string profileName,
        string? explicitCatalogPath,
        CancellationToken cancellationToken)
    {
        var catalogPath = explicitCatalogPath ?? FindCatalog();
        if (!File.Exists(catalogPath))
        {
            throw InvalidProfile("profile-catalog-not-found", "The database read profile catalog was not found.");
        }

        if (new FileInfo(catalogPath).Length > MaximumCatalogBytes)
        {
            throw InvalidProfile(
                "profile-catalog-too-large",
                $"The database read profile catalog may contain at most {MaximumCatalogBytes} bytes.");
        }

        DatabaseReadProfileCatalog? catalog;
        try
        {
            await using var stream = File.OpenRead(catalogPath);
            catalog = await JsonSerializer.DeserializeAsync<DatabaseReadProfileCatalog>(
                stream,
                DatabaseReadJson.Options,
                cancellationToken);
        }
        catch (JsonException)
        {
            throw InvalidProfile("invalid-profile-catalog", "The database read profile catalog is not valid JSON.");
        }

        if (catalog?.SchemaVersion != 1 || catalog.Profiles is null)
        {
            throw InvalidProfile(
                "unsupported-profile-catalog",
                "The database read profile catalog must use schemaVersion 1 and contain profiles.");
        }

        ValidateProfileNames(catalog.Profiles);

        var matchingProfile = catalog.Profiles.FirstOrDefault(
            item => string.Equals(item.Key, profileName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingProfile.Key))
        {
            throw InvalidProfile("profile-not-found", $"Database read profile '{profileName}' was not found.");
        }

        var profile = matchingProfile.Value;
        var provider = ParseProvider(profile.Provider);
        var environment = RequireSafeName(profile.Environment, "environment");
        var connectionStringName = RequireSafeName(profile.ConnectionStringName, "connectionStringName");
        var configurationPath = ResolveConfigurationPath(catalogPath, profile.ConfigurationPath);
        var limits = ResolveProfileLimits(profile);

        return new ResolvedDatabaseReadProfile(
            matchingProfile.Key,
            provider,
            configurationPath,
            environment,
            connectionStringName,
            limits);
    }

    private static string FindCatalog()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RelativeCatalogPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw InvalidProfile(
            "profile-catalog-not-found",
            "No .newheap/database-read.json profile catalog was found. Use --profiles to select one explicitly.");
    }

    private static DatabaseProviderKind ParseProvider(string? provider)
    {
        return provider?.ToLowerInvariant() switch
        {
            "sql-server" => DatabaseProviderKind.SqlServer,
            "postgresql" => DatabaseProviderKind.PostgreSql,
            _ => throw InvalidProfile(
                "unsupported-provider",
                "The profile provider must be 'sql-server' or 'postgresql'.")
        };
    }

    private static void ValidateProfileNames(IReadOnlyDictionary<string, DatabaseReadProfile> profiles)
    {
        if (profiles.Count is 0 or > MaximumProfileCount)
        {
            throw InvalidProfile(
                "invalid-profile-count",
                $"The profile catalog must contain between 1 and {MaximumProfileCount} profiles.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in profiles.Keys)
        {
            RequireSafeName(name, "profile name");
            if (!names.Add(name))
            {
                throw InvalidProfile(
                    "duplicate-profile-name",
                    $"Profile name '{name}' is duplicated with different casing.");
            }
        }
    }

    private static string RequireSafeName(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeName().IsMatch(value))
        {
            throw InvalidProfile(
                "invalid-profile-value",
                $"Profile property '{propertyName}' contains an invalid value.");
        }

        return value;
    }

    private static string ResolveConfigurationPath(string catalogPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw InvalidProfile(
                "invalid-configuration-path",
                "Profile configurationPath must be a relative path inside the consumer workspace.");
        }

        var catalogDirectory = Path.GetDirectoryName(Path.GetFullPath(catalogPath))!;
        var workspaceRoot = string.Equals(
            Path.GetFileName(catalogDirectory),
            ".newheap",
            StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(catalogDirectory)!.FullName
                : catalogDirectory;
        var resolvedPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        var relativeToWorkspace = Path.GetRelativePath(workspaceRoot, resolvedPath);

        if (Path.IsPathRooted(relativeToWorkspace) ||
            relativeToWorkspace.Equals("..", StringComparison.Ordinal) ||
            relativeToWorkspace.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw InvalidProfile(
                "configuration-path-outside-workspace",
                "Profile configurationPath resolves outside the consumer workspace.");
        }

        if (!Directory.Exists(resolvedPath) || !File.Exists(Path.Combine(resolvedPath, "appsettings.json")))
        {
            throw InvalidProfile(
                "configuration-path-not-found",
                "Profile configurationPath must identify a directory containing appsettings.json.");
        }

        return resolvedPath;
    }

    private static DatabaseReadLimits ResolveProfileLimits(DatabaseReadProfile profile)
    {
        var limits = new DatabaseReadLimits(
            profile.MaximumRows ?? DefaultLimits.MaximumRows,
            profile.MaximumTimeoutSeconds ?? DefaultLimits.TimeoutSeconds,
            profile.MaximumLockTimeoutMilliseconds ?? DefaultLimits.LockTimeoutMilliseconds,
            profile.MaximumOutputBytes ?? DefaultLimits.MaximumOutputBytes,
            profile.MaximumCellBytes ?? DefaultLimits.MaximumCellBytes,
            profile.MaximumSqlBytes ?? DefaultLimits.MaximumSqlBytes);

        ValidateLimit(limits.MaximumRows, 1, HardLimits.MaximumRows, "maximumRows");
        ValidateLimit(limits.TimeoutSeconds, 1, HardLimits.TimeoutSeconds, "maximumTimeoutSeconds");
        ValidateLimit(
            limits.LockTimeoutMilliseconds,
            1,
            HardLimits.LockTimeoutMilliseconds,
            "maximumLockTimeoutMilliseconds");
        ValidateLimit(limits.MaximumOutputBytes, 4_096, HardLimits.MaximumOutputBytes, "maximumOutputBytes");
        ValidateLimit(limits.MaximumCellBytes, 16, HardLimits.MaximumCellBytes, "maximumCellBytes");
        ValidateLimit(limits.MaximumSqlBytes, 16, HardLimits.MaximumSqlBytes, "maximumSqlBytes");

        if (limits.MaximumCellBytes > limits.MaximumOutputBytes)
        {
            throw InvalidProfile(
                "invalid-profile-limit",
                "Profile limit 'maximumCellBytes' may not exceed 'maximumOutputBytes'.");
        }

        return limits;
    }

    private static void ValidateLimit(
        int value,
        int hardMinimum,
        int hardMaximum,
        string propertyName)
    {
        if (value < hardMinimum || value > hardMaximum)
        {
            throw InvalidProfile(
                "invalid-profile-limit",
                $"Profile limit '{propertyName}' must be between {hardMinimum} and {hardMaximum}.");
        }
    }

    private static DatabaseReadExpectedException InvalidProfile(string code, string message)
    {
        return new DatabaseReadExpectedException(code, message, DatabaseReadExitCode.InvalidProfile);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}
