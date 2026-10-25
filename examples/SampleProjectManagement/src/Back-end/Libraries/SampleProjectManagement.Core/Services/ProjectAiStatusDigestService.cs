using System.Text.Json;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// The structured answer of <see cref="ProjectAiStatusDigestService"/>. Both lists use the same item
/// type, and <see cref="Highlight"/> may be <see langword="null"/>: the shapes whose schema the sample
/// normalizes for strict providers.
/// </summary>
public sealed record ProjectAiStatusDigest(
    string Summary,
    IReadOnlyList<ProjectAiDigestItem> OnTrack,
    IReadOnlyList<ProjectAiDigestItem> AtRisk,
    ProjectAiDigestItem? Highlight);

public sealed record ProjectAiDigestItem(
    string Project,
    string Reason);

/// <summary>
/// SPM-335: asks the governed project model for a structured status digest of the projects the caller
/// passes. The schema comes from <see cref="ProjectAiStructuredOutput"/>, the sample's own
/// normalization for strict providers. Every expected outcome is a <see cref="TaskResult"/>: the
/// executor's failed result propagates unchanged, also <see cref="NhAiModelFailureCodes.Timeout"/>
/// when the profile's timeout or the run's deadline ends the call, and only the caller's own
/// cancellation throws.
/// </summary>
public sealed class ProjectAiStatusDigestService(INhAiChatExecutor executor)
{
    public const string ProfileName = "project-assistant";
    public const string SchemaName = "project_status_digest";
    public const string InvalidDigestCode = "project-ai-digest-invalid";

    private static readonly JsonSerializerOptions DigestOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The normalized response format the service sends.
    /// </summary>
    public static ChatResponseFormatJson ResponseFormat { get; } =
        ProjectAiStructuredOutput.ForJsonSchema<ProjectAiStatusDigest>(
            SchemaName,
            "A status digest of the given projects.");

    public async Task<TaskResult<ProjectAiStatusDigest>> CreateAsync(
        NhAiInvocationContext context,
        IReadOnlyList<string> projectLines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(projectLines);

        var projects = string.Join('\n', projectLines.Take(50));
        var result = await executor.GetResponseAsync(
            new NhAiChatRequest(
                context,
                ProfileName,
                NhAiModelCapability.StructuredOutput,
                NhAiDataClassification.Internal,
                [
                    new ChatMessage(ChatRole.System, "Sort the projects into on track and at risk, and name one highlight if any."),
                    new ChatMessage(ChatRole.User, projects)
                ])
            {
                Options = new ChatOptions { ResponseFormat = ResponseFormat },
                EstimatedInputTokens = Math.Max(1, (projects.Length + 3) / 4),
                RequestedOutputTokens = 512
            },
            cancellationToken);
        if (!result.Success)
        {
            return TaskResult<ProjectAiStatusDigest>.Failed(result);
        }

        try
        {
            var digest = JsonSerializer.Deserialize<ProjectAiStatusDigest>(result.Data.Response.Text, DigestOptions);
            return digest is null
                ? TaskResult<ProjectAiStatusDigest>.Failed(InvalidDigestCode, "The model returned no status digest.")
                : TaskResult<ProjectAiStatusDigest>.Succeeded(digest);
        }
        catch (JsonException)
        {
            // The model's text can carry content; only the failure code leaves the service.
            return TaskResult<ProjectAiStatusDigest>.Failed(InvalidDigestCode, "The model returned no valid status digest.");
        }
    }
}
