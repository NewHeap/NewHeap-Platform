using System.Collections.Concurrent;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-325: the sample's stand-in for another system that keeps the latest analysis of every
/// project. Its client answers after <see cref="Latency"/>, and, as some client libraries do, it
/// reports a cancelled call as an error of its own (<see cref="ProjectAnalysisArchiveException"/>)
/// rather than as an <see cref="OperationCanceledException"/>. The project-analysis child calls it
/// outside a step and lets that error travel: while the owner's cancellation is requested, the
/// background-operation runner records the operation as cancelled instead of as a failure it
/// retries.
/// </summary>
public sealed class ProjectAnalysisArchive(TimeSpan latency)
{
    private readonly ConcurrentDictionary<Guid, int> _analysedPasses = new();

    /// <summary>How long the archive takes to answer a call.</summary>
    public TimeSpan Latency { get; } = latency;

    /// <summary>
    /// Stores the number of passes analysed for the project.
    /// </summary>
    /// <exception cref="ProjectAnalysisArchiveException">The call was cancelled before the archive
    /// answered.</exception>
    public async Task StoreAsync(Guid projectId, int analysedPasses, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Latency, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new ProjectAnalysisArchiveException(
                "The project analysis archive did not answer before the call was cancelled.",
                exception);
        }

        _analysedPasses[projectId] = analysedPasses;
    }

    /// <summary>
    /// The number of passes stored for the project, or <see langword="null"/> when none was stored.
    /// </summary>
    public int? FindAnalysedPasses(Guid projectId)
    {
        return _analysedPasses.TryGetValue(projectId, out var passes)
            ? passes
            : null;
    }
}

/// <summary>
/// The archive's own error for a call that did not complete.
/// </summary>
public sealed class ProjectAnalysisArchiveException(string message, Exception innerException)
    : Exception(message, innerException);
