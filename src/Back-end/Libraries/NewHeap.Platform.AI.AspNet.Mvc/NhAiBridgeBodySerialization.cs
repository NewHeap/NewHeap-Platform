using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>Serializes a bridged request body with access to the current invocation scope.</summary>
public interface INhAiBridgeBodySerializer
{
    string Serialize(object body, IServiceProvider requestServices);
}

/// <summary>
/// Serializes bodies with the MVC Newtonsoft.Json settings resolved from the current request
/// services. Register it with <see cref="NhAiMvcBridgeBuilder.UseBodySerializer{TSerializer}"/>.
/// </summary>
public sealed class NhAiMvcNewtonsoftJsonBodySerializer : INhAiBridgeBodySerializer
{
    public string Serialize(object body, IServiceProvider requestServices)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(requestServices);
        var options = requestServices.GetRequiredService<IOptions<MvcNewtonsoftJsonOptions>>().Value;
        return JsonConvert.SerializeObject(body, options.SerializerSettings);
    }
}

internal sealed class NhAiBridgeConventionBodySerializer(
    INhAiBridgeConventions conventions) : INhAiBridgeBodySerializer
{
    public string Serialize(object body, IServiceProvider requestServices)
    {
        ArgumentNullException.ThrowIfNull(requestServices);
        return conventions.SerializeBody(body);
    }
}
