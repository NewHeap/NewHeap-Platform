using System.Text.Json;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

/// <summary>
/// The resources of the application's API bridge gateway for NewHeap's own code that helps a person build
/// agents, such as the agent builder: the gateway's own <c>search-resources</c> and
/// <c>describe-resource</c>, run with the rights of the person of the current request, so only the
/// resources and operations that person may use are found and described. The caller's own governed call
/// is the one that is authorized and audited. The gateway registers it (<c>EnableGateway</c>); without a
/// gateway there is none.
/// </summary>
internal interface INhAiGatewayResources
{
    /// <summary>
    /// The ids of the gateway's own tools: <c>search-resources</c>, <c>describe-resource</c>,
    /// <c>query</c> and <c>get</c>.
    /// </summary>
    IReadOnlyCollection<string> ToolIds { get; }

    /// <summary>
    /// The resources whose name, title, summary or field names contain the words of
    /// <paramref name="query"/>, at most <paramref name="limit"/> (1 to 20), with their operations, as
    /// <c>search-resources</c> returns them.
    /// </summary>
    Task<TaskResult<JsonElement>> SearchAsync(string query, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// One resource as <c>describe-resource</c> returns it: its filter, search and order fields, extra
    /// parameters, the id parameter of <c>get</c>, result fields and how results are shaped. A resource
    /// that does not exist or that the person may not use fails as the gateway's own tool does.
    /// </summary>
    Task<TaskResult<JsonElement>> DescribeAsync(string resource, CancellationToken cancellationToken);
}
