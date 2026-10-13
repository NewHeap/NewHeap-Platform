namespace NewHeap.Platform.AspNet.Policy;

/// <summary>
/// An authorization resource that names the division a decision is about. Division
/// policies use it instead of the active-division request header, so they also work
/// without an HTTP request, for example in a background agent run.
/// </summary>
public interface INhDivisionScopedResource
{
    /// <summary>
    /// The division to authorize against, or <see langword="null"/> when the resource
    /// belongs to no division. A <see langword="null"/> division never satisfies a
    /// division requirement.
    /// </summary>
    Guid? DivisionId { get; }
}
