namespace SampleProjectManagement.Api.Models;

/// <summary>
/// Stable response contracts used by the sample API's generated OpenAPI document.
/// Keeping these responses explicit makes Scalar useful as executable API documentation.
/// </summary>
public sealed record CacheInvalidationSample(string CacheKey, bool Invalidated);

public sealed record RecurringJobSampleResult(string RecurringJobId);

public sealed record NotificationCreatedSampleResult(Guid Id, int DeliveryCount);

public sealed record ProjectStatusOptionSample(int Id, string Name);

public sealed record StartupConfigurationSample(DateTimeOffset? ConfiguredAtUtc);

public sealed record QueryEchoSample(string Value, int Length);

public sealed record DeduplicatedHttpSample(Guid ExecutionId, DateTimeOffset ExecutedAtUtc);

public sealed record HttpTraceSample(string TraceIdentifier, string CorrelationHeader);

public sealed record AuthorizationResultSample(string AuthorizedBy);

public sealed record ProjectCountSample(int Count);

public sealed record SampleProblem(string Error, string Message);

public sealed record LocalizationSample(string Culture, string ProjectCreated);

public sealed record InvariantFormSample(decimal Budget, DateTime Deadline, string Culture);

public sealed record ObservabilityResponse(
    string TraceId,
    long ElapsedMilliseconds,
    bool CompletionLogged,
    bool HandledFailureLogged);

public sealed record PublishedEventSample(Guid EventId, string Topic);
