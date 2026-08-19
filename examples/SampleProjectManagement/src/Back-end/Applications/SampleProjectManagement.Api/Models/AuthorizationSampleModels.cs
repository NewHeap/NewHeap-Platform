namespace SampleProjectManagement.Api.Models;

public sealed record AuthorizationProbeSample(
    string Level,
    string RequiredPermission,
    string Message,
    Guid? ActiveDivisionId,
    Guid? ProjectId,
    IReadOnlyCollection<string> Roles);

public sealed record AuthenticationOverrideProbeSample(
    string AuthenticationService,
    string TokenClaimStrategy,
    string RequestClaimStrategy,
    bool RequestTransformationApplied,
    Guid? UserId,
    Guid? ActiveDivisionId,
    IReadOnlyCollection<RuntimeAuthorizationClaimSample> RuntimeClaims,
    string TraceIdentifier);

public sealed record RuntimeAuthorizationClaimSample(
    string Type,
    string Value);
