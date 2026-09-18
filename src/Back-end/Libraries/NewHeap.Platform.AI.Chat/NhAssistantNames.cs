namespace NewHeap.Platform.AI.Chat;

internal static class NhAssistantNames
{
    public static bool IsSegment(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value[0] != '-'
            && value[^1] != '-'
            && !value.Contains("--", StringComparison.Ordinal)
            && value.All(character => character == '-'
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9'));
    }

    public static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!IsSegment(value))
        {
            throw new ArgumentException(
                "Assistant identifiers must use bounded lowercase dash-case.",
                parameterName);
        }
    }
}
