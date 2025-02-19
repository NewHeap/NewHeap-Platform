using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AspNet.Common;

namespace NewHeap.Platform.AspNet;

public static partial class HttpContextExtensions
{
    public static Guid? GetActiveDivisionId(this HttpRequest httpRequest)
    {
        var activeDivisionIdString = httpRequest.Headers
            .FirstOrDefault(x => x.Key.ToLower().Trim() == Constants.HttpHeaderKeys.ActiveDivisionId.ToLower().Trim())
            .Value.ToString();
        var activeDivisionIdFound = Guid.TryParse(activeDivisionIdString, out var activeDivisionId);

        return activeDivisionIdFound ? activeDivisionId : null;
    }
}