using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NewHeap.Platform.AspNet.Proxy;

internal static class NhProxyJson
{
    internal static JsonSerializerOptions CreateOptions(bool writeIndented = false, bool requireConstructorParameters = true)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type == typeof(NhProxyTransform))
            {
                // Missing discriminators are invalid input, not unsupported serializer operations.
                typeInfo.CreateObject = () => throw new JsonException(
                    "A transform must specify 'kind': 'path', 'query', 'header', 'original-host' or 'forwarded-headers'.");
            }
        });

        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver,
            WriteIndented = writeIndented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = requireConstructorParameters,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
        };
    }
}
