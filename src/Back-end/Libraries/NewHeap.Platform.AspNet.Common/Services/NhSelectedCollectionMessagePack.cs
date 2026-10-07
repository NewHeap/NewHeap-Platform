using System.Buffers;
using System.Globalization;
using MessagePack;
using NewHeap.Platform.AspNet.Common.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NewHeap.Platform.AspNet.Common.Services;

/// <summary>Standard MessagePack maps with the same names and scalar semantics as the JSON response.</summary>
public static class NhSelectedCollectionMessagePack
{
    public static byte[] Serialize(NhSelectedCollectionResult result, JsonSerializerSettings settings)
    {
        // Honor the application's JSON converters without a JSON text encode/parse round trip.
        // This compatibility baseline is intentionally benchmarked before optimizing the token allocation.
        var document = JToken.FromObject(result, JsonSerializer.Create(settings));
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        Write(ref writer, document, settings);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(ref MessagePackWriter writer, JToken token, JsonSerializerSettings settings)
    {
        switch (token)
        {
            case JObject obj:
                writer.WriteMapHeader(obj.Count);
                foreach (var property in obj.Properties())
                {
                    writer.Write(property.Name);
                    Write(ref writer, property.Value, settings);
                }
                break;
            case JArray array:
                writer.WriteArrayHeader(array.Count);
                foreach (var item in array)
                {
                    Write(ref writer, item, settings);
                }
                break;
            case JValue value when value.Type is JTokenType.Null or JTokenType.Undefined:
                writer.WriteNil();
                break;
            case JValue value when value.Type == JTokenType.Boolean:
                writer.Write(value.Value<bool>());
                break;
            case JValue value when value.Type == JTokenType.Integer:
                if (value.Value is ulong unsigned)
                {
                    writer.Write(unsigned);
                }
                else
                {
                    writer.Write(value.Value<long>());
                }
                break;
            case JValue value when value.Type == JTokenType.Float:
                // Browser JSON numbers and MessagePack floats both use IEEE-754 doubles.
                var number = value.Value<double>();
                if (double.IsFinite(number))
                {
                    writer.Write(number);
                }
                else
                {
                    var jsonNumber = JToken.Parse(JsonConvert.SerializeObject(value.Value, settings));
                    if (jsonNumber.Type == JTokenType.Float && !double.IsFinite(jsonNumber.Value<double>()))
                    {
                        throw new InvalidOperationException("Selected collections require standard JSON number formatting.");
                    }
                    Write(ref writer, jsonNumber, settings);
                }
                break;
            case JValue value when value.Type == JTokenType.Date:
                var jsonDate = JsonConvert.SerializeObject(value.Value, settings);
                writer.Write(JsonConvert.DeserializeObject<string>(jsonDate));
                break;
            case JValue value when value.Type is JTokenType.String or JTokenType.Guid or JTokenType.Uri or JTokenType.TimeSpan:
                writer.Write(Convert.ToString(value.Value, CultureInfo.InvariantCulture));
                break;
            default:
                throw new InvalidOperationException($"Unsupported selected-collection token: {token.Type}.");
        }
    }
}
