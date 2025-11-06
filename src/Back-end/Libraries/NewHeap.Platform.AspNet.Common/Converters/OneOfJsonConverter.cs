using Newtonsoft.Json;
using OneOf;
using System.Reflection;

namespace NewHeap.Platform.AspNet.Common.Converters;

public class OneOfJsonConverter : JsonConverter
{
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is IOneOf)
        {
            value = ((IOneOf)value).Value;
        }
        serializer.Serialize(writer, value);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        throw new NotImplementedException();
    }

    public override bool CanConvert(Type objectType)
    {
        return objectType.GetTypeInfo().ImplementedInterfaces.Contains(typeof(IOneOf));
    }
}
