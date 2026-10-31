using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead;

/// <summary>
/// One read-only tool of the MCP server: its input schema is written out, and each call becomes the
/// request a caller of the one-shot command would send. The response JSON comes back unchanged, and
/// a refused or failed call is a tool error that carries the same safe error JSON.
/// </summary>
internal sealed class DatabaseReadMcpTool : McpServerTool
{
    private const string DefaultSchemaReason = "Discover selectable schema for a bounded read-only diagnostic query.";

    private const string SchemaNameProperty =
        """
        "schemaName": { "type": "string", "description": "Exact schema name." }
        """;

    private const string RequiredObjectProperties =
        """
        "schemaName": { "type": "string", "description": "Exact schema name." },
        "objectName": { "type": "string", "description": "Exact table or view name." }
        """;

    private const string MaximumRowsProperty =
        """
        "maximumRows": { "type": "integer", "minimum": 1, "description": "At most this many rows or objects, within the profile ceiling." }
        """;

    private const string TimeoutProperty =
        """
        "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Time limit in seconds, within the profile ceiling." }
        """;

    private const string SchemaReasonProperty =
        """
        "reason": { "type": "string", "description": "Why this is needed." }
        """;

    private readonly DatabaseReadCommand _command;
    private readonly string? _schemaOperation;
    private readonly HashSet<string> _argumentNames;
    private readonly string _profileName;
    private readonly DatabaseReadSessionHost _host;
    private readonly SemaphoreSlim _calls;

    private DatabaseReadMcpTool(
        string name,
        string title,
        string description,
        string inputSchema,
        DatabaseReadCommand command,
        string? schemaOperation,
        string profileName,
        DatabaseReadSessionHost host,
        SemaphoreSlim calls)
    {
        var schema = JsonSerializer.Deserialize<JsonElement>(inputSchema);

        ProtocolTool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = schema,
            Annotations = new ToolAnnotations
            {
                Title = title,
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            }
        };
        _command = command;
        _schemaOperation = schemaOperation;
        _argumentNames = schema
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        _profileName = profileName;
        _host = host;
        _calls = calls;
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata => [];

    public static IReadOnlyList<McpServerTool> CreateAll(
        string profileName,
        DatabaseReadSessionHost host,
        SemaphoreSlim calls)
    {
        DatabaseReadMcpTool Schema(
            string name,
            string title,
            string description,
            string operation,
            string inputSchema)
        {
            return new DatabaseReadMcpTool(
                name,
                title,
                description,
                inputSchema,
                DatabaseReadCommand.Schema,
                operation,
                profileName,
                host,
                calls);
        }

        return
        [
            Schema(
                "database_schema_search",
                "Search database schema",
                "Search selectable tables and views by name. Returns bounded summaries with SQL identifiers.",
                "search",
                $$"""
                {
                  "type": "object",
                  "properties": {
                    {{SchemaNameProperty}},
                    "searchTerm": { "type": "string", "description": "Part of a table or view name." },
                    {{MaximumRowsProperty}},
                    {{TimeoutProperty}},
                    {{SchemaReasonProperty}}
                  },
                  "additionalProperties": false
                }
                """),
            Schema(
                "database_schema_describe",
                "Describe a database object",
                "Describe one table or view: selectable columns, indexes and named relationships with ordered column pairs.",
                "describe",
                $$"""
                {
                  "type": "object",
                  "properties": {
                    {{RequiredObjectProperties}},
                    {{TimeoutProperty}},
                    {{SchemaReasonProperty}}
                  },
                  "required": ["schemaName", "objectName"],
                  "additionalProperties": false
                }
                """),
            Schema(
                "database_schema_indexes",
                "List database indexes",
                "List the indexes of one table or view with key order, direction and included columns.",
                "indexes",
                $$"""
                {
                  "type": "object",
                  "properties": {
                    {{RequiredObjectProperties}},
                    {{TimeoutProperty}},
                    {{SchemaReasonProperty}}
                  },
                  "required": ["schemaName", "objectName"],
                  "additionalProperties": false
                }
                """),
            Schema(
                "database_schema_catalog",
                "Describe a database catalog",
                "Describe every selectable table and view, or those of one schema, in one call. Reports truncated when a limit cuts it short.",
                "catalog",
                $$"""
                {
                  "type": "object",
                  "properties": {
                    {{SchemaNameProperty}},
                    {{MaximumRowsProperty}},
                    {{TimeoutProperty}},
                    {{SchemaReasonProperty}}
                  },
                  "additionalProperties": false
                }
                """),
            new DatabaseReadMcpTool(
                "database_query",
                "Query the database",
                "Run one read-only SELECT in a read-only transaction. Put data values in parameters and a row cap such as TOP or LIMIT in the SQL.",
                $$"""
                {
                  "type": "object",
                  "properties": {
                    "sql": { "type": "string", "description": "One SELECT or read-only WITH statement, without comments." },
                    "parameters": {
                      "type": "array",
                      "description": "Typed data values that the SQL refers to by name. A date-time with Z or an offset is an instant; one with neither is a wall-clock value and binds as written.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "name": { "type": "string" },
                          "type": {
                            "type": "string",
                            "enum": ["string", "boolean", "int32", "int64", "decimal", "double", "uuid", "date-time", "date", "binary-base64"]
                          },
                          "value": { "description": "The value, or null." }
                        },
                        "required": ["name", "type", "value"],
                        "additionalProperties": false
                      }
                    },
                    {{MaximumRowsProperty}},
                    {{TimeoutProperty}},
                    "reason": { "type": "string", "description": "Why this query is needed." }
                  },
                  "required": ["sql", "reason"],
                  "additionalProperties": false
                }
                """,
                DatabaseReadCommand.Query,
                null,
                profileName,
                host,
                calls)
        ];
    }

    /// <summary>
    /// Runs the call as a request of the one-shot pipeline. The core serves one request at a time,
    /// and a client may send calls in parallel, so they wait for each other here.
    /// </summary>
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var arguments = request.Params?.Arguments;
        using var response = new MemoryStream();

        await _calls.WaitAsync(cancellationToken);

        int exitCode;
        try
        {
            exitCode = await NewHeapDatabaseReadApplication.RunRequestAsync(
                _command,
                _profileName,
                _ => Task.FromResult(CreateRequest(arguments)),
                _host,
                response,
                cancellationToken);
        }
        finally
        {
            _calls.Release();
        }

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = Encoding.UTF8.GetString(response.GetBuffer(), 0, (int)response.Length).TrimEnd('\n')
                }
            ],
            IsError = exitCode != (int)DatabaseReadExitCode.Success
        };
    }

    /// <summary>
    /// Builds the request from the tool's own arguments alone. The profile is not one of them, and
    /// an argument the tool does not declare is refused instead of ignored, so a caller that
    /// believes it chose a profile or an environment finds out.
    /// </summary>
    private DatabaseReadRequest CreateRequest(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments?.Keys.Any(name => !_argumentNames.Contains(name)) == true)
        {
            throw new DatabaseReadExpectedException(
                "invalid-tool-argument",
                $"This tool accepts only: {string.Join(", ", _argumentNames.Order(StringComparer.Ordinal))}.",
                DatabaseReadExitCode.InvalidRequest);
        }

        var argumentsJson = JsonSerializer.SerializeToUtf8Bytes(arguments, DatabaseReadJson.Options);
        if (argumentsJson.Length > DatabaseReadJson.MaximumRequestBytes)
        {
            throw DatabaseReadJson.RequestTooLarge();
        }

        var values = arguments is null
            ? new DatabaseReadMcpArguments()
            : JsonSerializer.Deserialize<DatabaseReadMcpArguments>(argumentsJson, DatabaseReadJson.Options)!;

        return new DatabaseReadRequest
        {
            SchemaVersion = 1,
            Sql = values.Sql,
            Parameters = values.Parameters,
            Schema = _schemaOperation is null
                ? null
                : new DatabaseSchemaRequest
                {
                    Operation = _schemaOperation,
                    SchemaName = values.SchemaName,
                    ObjectName = values.ObjectName,
                    SearchTerm = values.SearchTerm
                },
            Limits = new DatabaseReadLimitRequest
            {
                MaximumRows = values.MaximumRows,
                TimeoutSeconds = values.TimeoutSeconds
            },
            Reason = values.Reason ?? (_schemaOperation is null ? null : DefaultSchemaReason)
        };
    }

    /// <summary>
    /// Every argument of every tool. Each tool's own schema says which of them it accepts.
    /// </summary>
    private sealed class DatabaseReadMcpArguments
    {
        public string? Sql { get; init; }

        public IReadOnlyList<DatabaseReadParameterRequest>? Parameters { get; init; }

        public string? SchemaName { get; init; }

        public string? ObjectName { get; init; }

        public string? SearchTerm { get; init; }

        public int? MaximumRows { get; init; }

        public int? TimeoutSeconds { get; init; }

        public string? Reason { get; init; }
    }
}
