using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;

namespace NewHeap.Platform.Mapping;

internal static class ScalarMapping
{
    internal static bool CanConvert(Type sourceType, Type destinationType)
    {
        sourceType = Nullable.GetUnderlyingType(sourceType) ?? sourceType;
        destinationType = Nullable.GetUnderlyingType(destinationType) ?? destinationType;

        return sourceType == typeof(string) &&
                   (destinationType == typeof(Guid) || destinationType == typeof(TimeSpan) ||
                    destinationType == typeof(DateTimeOffset)) ||
               sourceType == typeof(DateTime) && destinationType == typeof(DateTimeOffset);
    }

    internal static object Convert(object source, Type destinationType)
    {
        if (destinationType.IsEnum)
        {
            if (source is string text)
            {
                if (text.Length == 0)
                {
                    return Activator.CreateInstance(destinationType)!;
                }

                foreach (var field in destinationType.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var wireValue = field.GetCustomAttribute<EnumMemberAttribute>()?.Value;
                    if (wireValue is not null && string.Equals(text, wireValue, StringComparison.OrdinalIgnoreCase))
                    {
                        return field.GetValue(null)!;
                    }
                }

                return Enum.Parse(destinationType, text, ignoreCase: true);
            }

            if (source is Enum && Enum.TryParse(destinationType, source.ToString(), ignoreCase: true, out var namedValue))
            {
                return namedValue!;
            }

            return Enum.ToObject(destinationType, source);
        }

        if (source is string value)
        {
            if (destinationType == typeof(Guid))
            {
                return Guid.Parse(value);
            }

            if (destinationType == typeof(TimeSpan))
            {
                return TimeSpan.Parse(value, CultureInfo.CurrentCulture);
            }

            if (destinationType == typeof(DateTimeOffset))
            {
                return DateTimeOffset.Parse(value, CultureInfo.CurrentCulture);
            }
        }

        if (source is DateTime dateTime && destinationType == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(dateTime);
        }

        return System.Convert.ChangeType(source, destinationType, CultureInfo.CurrentCulture);
    }
}
