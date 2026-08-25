using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

public static partial class NhBackgroundOperationResourceKey
{
    public static string ForAction(string action, string resourceType, params object?[] resourceIds)
    {
        ValidateComponent(action, nameof(action));
        ValidateComponent(resourceType, nameof(resourceType));
        if (resourceIds.Length == 0)
        {
            throw new ArgumentException("At least one resource id is required.", nameof(resourceIds));
        }

        var parts = new[] { action, resourceType }
            .Concat(resourceIds.Select(x => x?.ToString() ?? throw new ArgumentException("Resource ids cannot be null.", nameof(resourceIds))))
            .Select(x => Uri.EscapeDataString(x.Trim().ToLowerInvariant()));
        return Bound(string.Join(':', parts));
    }

    public static string ForUserAction(string action, Guid userId)
    {
        return ForAction(action, "user", userId);
    }

    public static string ForDivisionAction(string action, Guid divisionId)
    {
        return ForAction(action, "division", divisionId);
    }

    public static string ForResourceAction(string action, string resourceType, Guid resourceId)
    {
        return ForAction(action, resourceType, resourceId);
    }

    private static string Bound(string value)
    {
        if (value.Length <= 450)
        {
            return value;
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return $"{value[..380]}:{hash}";
    }

    private static void ValidateComponent(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Resource-key components are required.", parameterName);
        }
    }
}

internal static partial class NhBackgroundOperationKeys
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StepKeyRegex();

    internal static void ValidateStepKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || !StepKeyRegex().IsMatch(key))
        {
            throw new ArgumentException("Step keys must be lowercase dash-case and at most 200 characters.", nameof(key));
        }
    }

    internal static void ValidateOperationType(string operationType)
    {
        if (string.IsNullOrWhiteSpace(operationType)
            || operationType.Length > 200
            || !StepKeyRegex().IsMatch(operationType))
        {
            throw new ArgumentException(
                "Operation types must be stable lowercase dash-case identifiers of at most 200 characters.",
                nameof(operationType));
        }
    }

    internal static string NormalizeQueueName(string queue)
    {
        if (string.IsNullOrWhiteSpace(queue))
        {
            throw new ArgumentException("A background-operation queue name is required.", nameof(queue));
        }

        var normalized = queue.Trim().ToLowerInvariant();
        if (normalized.Length > 50
            || normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new ArgumentException(
                "Background-operation queue names may contain lowercase ASCII letters, digits, underscores, and dashes and must not exceed 50 characters.",
                nameof(queue));
        }

        return normalized;
    }

    internal static string HashIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Idempotency key is required.", nameof(key));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));
    }

    internal static string HashResourceKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Resource key is required.", nameof(key));
        }
        var normalized = key.Trim().ToLowerInvariant();
        if (normalized.Length > 450)
        {
            throw new ArgumentException("Resource key cannot exceed 450 characters.", nameof(key));
        }
        return $"v1:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))}";
    }
}
