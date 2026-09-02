using System.Globalization;
using System.Text;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseReadValueFormatter
{
    public static object? Format(object? value, int maximumCellBytes, out bool wasTruncated)
    {
        wasTruncated = false;

        return value switch
        {
            null or DBNull => null,
            string text => LimitString(text, maximumCellBytes, out wasTruncated),
            char character => character.ToString(),
            bool or byte or sbyte or short or ushort or int or uint or float or double => value,
            long integer => integer.ToString(CultureInfo.InvariantCulture),
            ulong integer => integer.ToString(CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            Guid identifier => identifier.ToString("D"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => LimitBinary(bytes, maximumCellBytes, out wasTruncated),
            _ => LimitString(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name,
                maximumCellBytes,
                out wasTruncated)
        };
    }

    private static string LimitString(string value, int maximumBytes, out bool wasTruncated)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            wasTruncated = false;
            return value;
        }

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, middle)) <= maximumBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        wasTruncated = true;
        if (low > 0 &&
            low < value.Length &&
            char.IsHighSurrogate(value[low - 1]) &&
            char.IsLowSurrogate(value[low]))
        {
            low--;
        }

        return value[..low];
    }

    private static string LimitBinary(byte[] value, int maximumBytes, out bool wasTruncated)
    {
        var maximumRawBytes = maximumBytes / 4 * 3;
        if (value.Length <= maximumRawBytes)
        {
            wasTruncated = false;
            return Convert.ToBase64String(value);
        }

        wasTruncated = true;
        return Convert.ToBase64String(value.AsSpan(0, maximumRawBytes));
    }
}
