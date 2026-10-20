using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Reads the stable code and the field errors of a failed <see cref="TaskResult"/> as the assistant
/// builds them: the code is the name of the first result item, and field errors follow as items
/// named <see cref="NhAssistantValidationErrors.FieldPrefix"/> and the field.
/// </summary>
internal static class NhAssistantResults
{
    /// <summary>
    /// The code of a failed result, or <see langword="null"/> when it names none.
    /// </summary>
    public static string? CodeOf(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)
                && !name.StartsWith(NhAssistantValidationErrors.FieldPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// The code of a failed result, or <paramref name="fallback"/> when it names none.
    /// </summary>
    public static string CodeOf(TaskResult result, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        return CodeOf(result) ?? fallback;
    }

    /// <summary>
    /// The field errors of a failed result by field, each value once; empty when it has none.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> FieldErrorsOf(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.GetResultItems()
            .Where(item => item.Name.StartsWith(NhAssistantValidationErrors.FieldPrefix, StringComparison.Ordinal))
            .GroupBy(item => item.Name[NhAssistantValidationErrors.FieldPrefix.Length..], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(item => item.ErrorMessages)
                    .Select(message => message.ToString())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
    }
}
