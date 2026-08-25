using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal static class NhBackgroundOperationJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    static NhBackgroundOperationJson()
    {
        Options.Converters.Add(new JsonStringEnumConverter());
    }

    internal static string Serialize(object? value)
    {
        return value is null
            ? "{}"
            : JsonSerializer.Serialize(value, value.GetType(), Options);
    }
}
