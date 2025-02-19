using Microsoft.AspNetCore.Http;
using System;
using System.Dynamic;
using System.Linq;
using System.Text.Json;

namespace NewHeap.Platform.AspNet;

public static partial class HttpContextExtensions
{
    public static Guid? GetActiveDivisionId(this HttpRequest httpRequest)
    {
        var activeDivisionIdString = httpRequest.Headers.FirstOrDefault(x => x.Key.ToLower().Trim() == Common.Constants.HttpHeaderKeys.ActiveDivisionId.ToLower().Trim()).Value.ToString();
        var activeDivisionIdFound = Guid.TryParse(activeDivisionIdString, out Guid activeDivisionId);

        return activeDivisionIdFound ? activeDivisionId : null;
    }
}
