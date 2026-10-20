using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-251: resolves MCP secret references <c>secret-file:&lt;name&gt;</c> from files in a mounted
/// secrets directory, as container platforms provide secrets (one file per secret). The directory is
/// <see cref="DirectoryKey"/>; without it no reference resolves. A name is a single file name, so a
/// reference never reaches a file outside the directory. The file is read for every connection, so a
/// rotated secret applies at once, and its content is never logged.
/// </summary>
public sealed class SampleMountedSecretResolver(IConfiguration configuration) : INhAssistantMcpSecretResolver
{
    public const string Prefix = "secret-file";
    public const string DirectoryKey = "SampleProjectManagement:Assistant:McpSecretDirectory";

    public async ValueTask<TaskResult<string>> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        var directory = configuration[DirectoryKey];
        if (string.IsNullOrWhiteSpace(directory)
            || name.IndexOfAny(['/', '\\']) >= 0
            || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            return NotFound();
        }

        var path = Path.Combine(Path.GetFullPath(directory), name);
        if (!File.Exists(path))
        {
            return NotFound();
        }
        var secret = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
        return secret.Length == 0 ? NotFound() : TaskResult<string>.Succeeded(secret);
    }

    private static TaskResult<string> NotFound()
    {
        return TaskResult<string>.Failed("sample-secret-not-found", "The mounted secret does not exist.");
    }
}
