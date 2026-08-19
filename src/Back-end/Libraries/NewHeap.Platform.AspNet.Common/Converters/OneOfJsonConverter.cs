using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        var token = JToken.Load(reader);
        var oneOfType = GetOneOfType(objectType);
        var types = oneOfType.GetGenericArguments();

        for (var index = 0; index < types.Length; index++)
        {
            if (token.Type == JTokenType.Null && types[index].IsValueType && Nullable.GetUnderlyingType(types[index]) is null)
            {
                continue;
            }

            if (types[index] == typeof(string) && token.Type is not JTokenType.String and not JTokenType.Null ||
                IsNumeric(types[index]) && token.Type is not JTokenType.Integer and not JTokenType.Float)
            {
                continue;
            }

            try
            {
                var value = token.ToObject(types[index], serializer);
                var oneOf = oneOfType.GetMethod($"FromT{index}", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, new[] { value })!;

                if (oneOfType == objectType)
                {
                    return oneOf;
                }

                var constructor = objectType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { oneOfType },
                    null);

                if (constructor is not null)
                {
                    return constructor.Invoke(new[] { oneOf });
                }

                var conversion = objectType.GetMethod(
                    "op_Implicit",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { types[index] },
                    null);

                if (conversion is not null)
                {
                    return conversion.Invoke(null, new[] { value })!;
                }

                throw new JsonSerializationException($"Could not create {objectType} from {oneOfType}.");
            }
            catch (JsonException)
            {
                // Try the next OneOf variant.
            }
        }

        throw new JsonSerializationException($"Could not deserialize {token} to any variant of {objectType}.");
    }

    private static Type GetOneOfType(Type objectType)
    {
        if (objectType.IsGenericType && objectType.GetGenericTypeDefinition().Name.StartsWith("OneOf`", StringComparison.Ordinal))
        {
            return objectType;
        }

        var baseType = objectType.BaseType;
        while (baseType is not null && (!baseType.IsGenericType || !baseType.GetGenericTypeDefinition().Name.StartsWith("OneOfBase`", StringComparison.Ordinal)))
        {
            baseType = baseType.BaseType;
        }

        if (baseType is null)
        {
            throw new JsonSerializationException($"Could not find a OneOf base type for {objectType}.");
        }

        return typeof(OneOf<>).Assembly.GetType($"OneOf.OneOf`{baseType.GetGenericArguments().Length}")!
            .MakeGenericType(baseType.GetGenericArguments());
    }

    private static bool IsNumeric(Type type)
    {
        return Type.GetTypeCode(Nullable.GetUnderlyingType(type) ?? type) is TypeCode.Byte or TypeCode.SByte or
            TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or
            TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
    }

    public override bool CanConvert(Type objectType)
    {
        return objectType.GetTypeInfo().ImplementedInterfaces.Contains(typeof(IOneOf));
    }
}
