using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Authentication;

namespace NewHeap.Platform.AI.Chat.AspNet.Live;

/// <summary>
/// The path <c>MapNewHeapAssistant</c> maps the assistant hub on (<see cref="NhAssistantOptions.HubPath"/>,
/// <c>/hub/assistant</c> by default, on the host's root and outside the route prefix). The NewHeap bearer
/// setup reads the token and the active division from the query string for it, also when the hub moved
/// out of <c>/hub</c>; an empty path maps no hub and opens nothing.
/// </summary>
internal sealed class NhAssistantHubPath(IOptions<NhAssistantOptions> options) : INhLibraryHubPath
{
    public string? HubPath => options.Value.HubPath;
}
