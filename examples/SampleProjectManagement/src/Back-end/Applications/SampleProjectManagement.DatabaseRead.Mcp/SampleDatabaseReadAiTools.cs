using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.DatabaseRead;

namespace SampleProjectManagement.DatabaseRead.Mcp;

public static class SampleDatabaseReadLimits
{
    public const int MaximumRows = 1_000;
    public const int TimeoutSeconds = 30;
    public const int MaximumInputBytes = 128 * 1024;
    public const int MaximumOutputBytes = 4 * 1024 * 1024;
    public const int MaximumSqlBytes = 64 * 1024;
    public const int ToolTimeoutSeconds = 45;
    public const int ToolCallBudget = 16;
}

[NhAiToolSet("sample-database")]
public sealed class SampleDatabaseReadAiTools(
    ISampleDatabaseReadExecutor executor,
    SampleDatabaseReadMcpContext serverContext)
{
    [Authorize(Policy = "sample.database-diagnostics.read")]
    [NhAiTool(
        "query",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Mcp,
        MaxInputBytes = SampleDatabaseReadLimits.MaximumInputBytes,
        MaxResultBytes = SampleDatabaseReadLimits.MaximumOutputBytes,
        TimeoutSeconds = SampleDatabaseReadLimits.ToolTimeoutSeconds,
        MaxConcurrency = 1,
        RequiredCapabilities = [SampleDatabaseReadMcpContext.Capability])]
    [Description("Run a parameterized read-only SELECT through the server-selected sample database profile after repository evidence and an exact schema description have confirmed identifiers and relationships. When table size, predicate selectivity or ordering cost is uncertain, make at most one focused index lookup; use it when immediately helpful, otherwise continue with the bounded query. RequestedRows is required and requests above 1,000 fail instead of being silently reduced.")]
    public Task<TaskResult<JsonElement>> QueryAsync(
        SampleDatabaseQueryInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!HasExpectedProfile(context, serverContext.Profile))
        {
            return Task.FromResult(TaskResult<JsonElement>.Failed(
                "sample-database-scope-invalid",
                "The governed sample database profile scope is invalid."));
        }

        return executor.QueryAsync(input, cancellationToken);
    }

    [Authorize(Policy = "sample.database-diagnostics.read")]
    [NhAiTool(
        "schema",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Mcp,
        MaxInputBytes = SampleDatabaseReadLimits.MaximumInputBytes,
        MaxResultBytes = SampleDatabaseReadLimits.MaximumOutputBytes,
        TimeoutSeconds = SampleDatabaseReadLimits.ToolTimeoutSeconds,
        MaxConcurrency = 1,
        RequiredCapabilities = [SampleDatabaseReadMcpContext.Capability])]
    [Description("Search or describe the selectable live sample database schema before constructing SQL. An exact description returns safe identifiers, columns, keys, indexes, permission-filtered named outgoing and incoming relationships with ordered column pairs and validation status, and an evidence hash.")]
    public Task<TaskResult<JsonElement>> SchemaAsync(
        SampleDatabaseSchemaInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!HasExpectedProfile(context, serverContext.Profile))
        {
            return Task.FromResult(TaskResult<JsonElement>.Failed(
                "sample-database-scope-invalid",
                "The governed sample database profile scope is invalid."));
        }

        return executor.SchemaAsync(input, cancellationToken);
    }

    [Authorize(Policy = "sample.database-diagnostics.read")]
    [NhAiTool(
        "indexes",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Mcp,
        MaxInputBytes = SampleDatabaseReadLimits.MaximumInputBytes,
        MaxResultBytes = SampleDatabaseReadLimits.MaximumOutputBytes,
        TimeoutSeconds = SampleDatabaseReadLimits.ToolTimeoutSeconds,
        MaxConcurrency = 1,
        RequiredCapabilities = [SampleDatabaseReadMcpContext.Capability])]
    [Description("Optionally inspect selectable indexes for one confirmed live database object as a quick query-design hint when predicate selectivity, ordering or expected table size is uncertain. Make at most one focused lookup and continue with a bounded query when no suitable key is immediately clear. Returns positioned column or expression keys with direction, included columns, an optional partial predicate and uniqueness/primary-key markers. Use a partial index only when the query predicate demonstrably implies its predicate, and an expression key only with a compatible query expression and ordering.")]
    public Task<TaskResult<JsonElement>> IndexesAsync(
        SampleDatabaseIndexesInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!HasExpectedProfile(context, serverContext.Profile))
        {
            return Task.FromResult(TaskResult<JsonElement>.Failed(
                "sample-database-scope-invalid",
                "The governed sample database profile scope is invalid."));
        }

        return executor.IndexesAsync(input, cancellationToken);
    }

    private static bool HasExpectedProfile(NhAiInvocationContext context, string expectedProfile) =>
        context.TryGetScopeValue("database-profile", out var profile)
        && string.Equals(profile, expectedProfile, StringComparison.Ordinal);
}

public sealed record SampleDatabaseQueryInput(
    string Sql,
    IReadOnlyList<SampleDatabaseParameterInput>? Parameters,
    int RequestedRows,
    string Reason);

public sealed record SampleDatabaseSchemaInput(
    string Operation,
    string? SchemaName,
    string? ObjectName,
    string? SearchTerm,
    string Reason);

public sealed record SampleDatabaseIndexesInput(
    string SchemaName,
    string ObjectName,
    string Reason);

public sealed record SampleDatabaseParameterInput(
    string Name,
    string Type,
    JsonElement Value);

public interface ISampleDatabaseReadExecutor
{
    Task<TaskResult<JsonElement>> QueryAsync(
        SampleDatabaseQueryInput input,
        CancellationToken cancellationToken);

    Task<TaskResult<JsonElement>> SchemaAsync(
        SampleDatabaseSchemaInput input,
        CancellationToken cancellationToken);

    Task<TaskResult<JsonElement>> IndexesAsync(
        SampleDatabaseIndexesInput input,
        CancellationToken cancellationToken);
}

public sealed class NewHeapSampleDatabaseReadExecutor(
    SampleDatabaseReadMcpContext serverContext) : ISampleDatabaseReadExecutor
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<TaskResult<JsonElement>> QueryAsync(
        SampleDatabaseQueryInput input,
        CancellationToken cancellationToken)
    {
        if (input.RequestedRows <= 0
            || input.RequestedRows > SampleDatabaseReadLimits.MaximumRows)
        {
            return TaskResult<JsonElement>.Failed(
                "sample-database-row-limit-exceeded",
                $"RequestedRows must be between 1 and {SampleDatabaseReadLimits.MaximumRows}. The query was not executed.");
        }

        if (string.IsNullOrWhiteSpace(input.Sql)
            || Encoding.UTF8.GetByteCount(input.Sql) > SampleDatabaseReadLimits.MaximumSqlBytes
            || string.IsNullOrWhiteSpace(input.Reason)
            || input.Reason.Length > 2_000
            || (input.Parameters?.Count ?? 0) > 128)
        {
            return TaskResult<JsonElement>.Failed(
                "sample-database-request-invalid",
                "The bounded sample database query request is invalid.");
        }

        var request = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            profile = serverContext.Profile,
            sql = input.Sql,
            parameters = input.Parameters ?? [],
            limits = new
            {
                maximumRows = input.RequestedRows,
                timeoutSeconds = SampleDatabaseReadLimits.TimeoutSeconds
            },
            reason = input.Reason.Trim()
        }, RequestJsonOptions);
        return await ExecuteRequestAsync("query", request, cancellationToken);
    }

    public async Task<TaskResult<JsonElement>> SchemaAsync(
        SampleDatabaseSchemaInput input,
        CancellationToken cancellationToken)
    {
        var operation = input.Operation?.Trim().ToLowerInvariant();
        var isSearch = operation == "search";
        var isDescribe = operation == "describe";
        if ((!isSearch && !isDescribe)
            || string.IsNullOrWhiteSpace(input.Reason)
            || input.Reason.Length > 2_000
            || !IsValidSchemaValue(input.SchemaName)
            || !IsValidSchemaValue(input.ObjectName)
            || !IsValidSchemaValue(input.SearchTerm)
            || (isDescribe && (string.IsNullOrWhiteSpace(input.SchemaName)
                               || string.IsNullOrWhiteSpace(input.ObjectName)))
            || (isSearch && !string.IsNullOrWhiteSpace(input.ObjectName)))
        {
            return TaskResult<JsonElement>.Failed(
                "sample-database-schema-request-invalid",
                "The bounded sample database schema request is invalid.");
        }

        var request = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            profile = serverContext.Profile,
            schema = new
            {
                operation,
                schemaName = Normalize(input.SchemaName),
                objectName = Normalize(input.ObjectName),
                searchTerm = Normalize(input.SearchTerm)
            },
            limits = new
            {
                maximumRows = SampleDatabaseReadLimits.MaximumRows,
                timeoutSeconds = SampleDatabaseReadLimits.TimeoutSeconds
            },
            reason = input.Reason.Trim()
        }, RequestJsonOptions);
        return await ExecuteRequestAsync("schema", request, cancellationToken);
    }

    public async Task<TaskResult<JsonElement>> IndexesAsync(
        SampleDatabaseIndexesInput input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Reason)
            || input.Reason.Length > 2_000
            || string.IsNullOrWhiteSpace(input.SchemaName)
            || string.IsNullOrWhiteSpace(input.ObjectName)
            || !IsValidSchemaValue(input.SchemaName)
            || !IsValidSchemaValue(input.ObjectName))
        {
            return TaskResult<JsonElement>.Failed(
                "sample-database-indexes-request-invalid",
                "The bounded sample database indexes request is invalid.");
        }

        var request = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            profile = serverContext.Profile,
            schema = new
            {
                operation = "indexes",
                schemaName = input.SchemaName.Trim(),
                objectName = input.ObjectName.Trim()
            },
            limits = new
            {
                maximumRows = SampleDatabaseReadLimits.MaximumRows,
                timeoutSeconds = SampleDatabaseReadLimits.TimeoutSeconds
            },
            reason = input.Reason.Trim()
        }, RequestJsonOptions);
        return await ExecuteRequestAsync("schema", request, cancellationToken);
    }

    private async Task<TaskResult<JsonElement>> ExecuteRequestAsync(
        string command,
        string request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(serverContext.ProfileCatalogPath))
        {
            return TaskResult<JsonElement>.Failed(
                "sample-database-profile-missing",
                "The sample database profile catalog is unavailable.");
        }

        var validation = await ExecuteAsync("validate", request, cancellationToken);
        if (!validation.Success)
        {
            return validation;
        }

        return await ExecuteAsync(command, request, cancellationToken);
    }

    private async Task<TaskResult<JsonElement>> ExecuteAsync(
        string command,
        string request,
        CancellationToken cancellationToken)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(request));
        await using var output = new MemoryStream();
        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            [command, "--profiles", serverContext.ProfileCatalogPath],
            input,
            output,
            cancellationToken);
        if (output.Length > SampleDatabaseReadLimits.MaximumOutputBytes)
        {
            return TaskResult<JsonElement>.Failed(
                "sample-database-output-too-large",
                "The NewHeap database read response exceeded the sample MCP output boundary.");
        }

        output.Position = 0;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                output,
                cancellationToken: cancellationToken);
            var result = document.RootElement.Clone();
            if (exitCode != 0
                || !result.TryGetProperty("ok", out var ok)
                || ok.ValueKind != JsonValueKind.True)
            {
                return SafeFailure(result);
            }

            return TaskResult<JsonElement>.Succeeded(result);
        }
        catch (JsonException)
        {
            return TaskResult<JsonElement>.Failed(
                "sample-database-response-invalid",
                "The NewHeap database read tool returned an invalid response.");
        }
    }

    private static TaskResult<JsonElement> SafeFailure(JsonElement result)
    {
        if (!result.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object)
        {
            return Rejected();
        }

        var classification = SafeToken(error, "classification", 64);
        var provider = SafeToken(error, "provider", 32);
        var providerCode = SafeToken(error, "providerCode", 16);
        var code = classification ?? SafeToken(error, "code", 64) ?? "sample-database-rejected";
        var message = classification switch
        {
            "object-not-found" => "A referenced database object does not exist. Inspect the live schema before retrying.",
            "column-not-found" => "A referenced database column does not exist. Describe the live object before retrying.",
            "schema-not-found" => "A referenced database schema does not exist. Search the live schema before retrying.",
            "permission-denied" => "The read-only principal cannot access a referenced object or column.",
            "syntax-error" => "The database rejected the SQL syntax. Check the selected provider dialect.",
            "statement-timeout" => "The diagnostic statement reached its execution timeout. Narrow the query before retrying.",
            "lock-timeout" => "The diagnostic statement could not acquire a lock within its boundary.",
            "deadlock" => "The diagnostic statement was selected as a deadlock victim.",
            _ when code == "schema-object-not-found" =>
                "The database object was not found or is not selectable by the configured principal.",
            _ => "The NewHeap database read tool rejected the sample diagnostic request."
        };
        if (provider is not null && providerCode is not null)
        {
            message = $"{message} Provider {provider} code {providerCode}.";
        }

        return TaskResult<JsonElement>.Failed(code, message);
    }

    private static TaskResult<JsonElement> Rejected() =>
        TaskResult<JsonElement>.Failed(
            "sample-database-rejected",
            "The NewHeap database read tool rejected the sample diagnostic request.");

    private static string? SafeToken(JsonElement value, string propertyName, int maximumLength)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var candidate = property.GetString();
        return !string.IsNullOrWhiteSpace(candidate)
               && candidate.Length <= maximumLength
               && candidate.All(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            ? candidate
            : null;
    }

    private static bool IsValidSchemaValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Length <= 256 && !value.Any(char.IsControl);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
