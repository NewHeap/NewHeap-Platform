using System.Diagnostics;
using SampleProjectManagement.Api.Models;

namespace SampleProjectManagement.Api.Services;

public sealed class ObservabilitySampleService
{
    private const string CaseId = "SPM-105";
    private static readonly ActivitySource ActivitySource = new("SampleProjectManagement.Api");
    private readonly ILogger<ObservabilitySampleService> _logger;

    public ObservabilitySampleService(ILogger<ObservabilitySampleService> logger)
    {
        _logger = logger;
    }

    public async Task<ObservabilityResponse> RunAsync(
        bool includeHandledFailure,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("library-sample.observability");
        activity?.SetTag("sample.case", CaseId);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["sample_case"] = CaseId,
            ["operation"] = "library-sample.observability"
        });

        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(15, cancellationToken);

        var handledFailureLogged = false;
        if (includeHandledFailure)
        {
            try
            {
                throw new InvalidOperationException("Synthetic handled failure for observability verification.");
            }
            catch (InvalidOperationException exception)
            {
                handledFailureLogged = true;
                activity?.SetStatus(ActivityStatusCode.Error, "handled sample failure");
                activity?.AddEvent(new ActivityEvent(
                    "exception",
                    tags: new ActivityTagsCollection
                    {
                        ["exception.type"] = exception.GetType().FullName,
                        ["exception.escaped"] = false
                    }));
                _logger.LogWarning(
                    exception,
                    "Handled observability sample failure for {SampleCase}",
                    CaseId);
            }
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "Observability sample completed in {ElapsedMilliseconds} ms with handled failure {HandledFailureLogged}",
            stopwatch.ElapsedMilliseconds,
            handledFailureLogged);

        return new ObservabilityResponse(
            Activity.Current?.TraceId.ToString() ?? string.Empty,
            stopwatch.ElapsedMilliseconds,
            true,
            handledFailureLogged);
    }
}
