namespace NewHeap.Platform.DatabaseRead;

internal sealed record CliOptions(
    DatabaseReadCommand Command,
    string? ProfilesPath,
    string? RequestFilePath,
    string? ProfileName,
    CliDirectSchemaOptions? DirectSchema,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            return new CliOptions(DatabaseReadCommand.Validate, null, null, null, null, true);
        }

        var command = args[0].ToLowerInvariant() switch
        {
            "query" => DatabaseReadCommand.Query,
            "schema" => DatabaseReadCommand.Schema,
            "validate" => DatabaseReadCommand.Validate,
            _ => throw new DatabaseReadExpectedException(
                "unknown-command",
                $"Unknown command '{args[0]}'.",
                DatabaseReadExitCode.InvalidRequest)
        };

        string? profilesPath = null;
        string? requestFilePath = null;
        string? profileName = null;
        string? schemaSearchTerm = null;
        string? schemaName = null;
        int? maximumRows = null;
        int? timeoutSeconds = null;
        var describeIfSingle = false;

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--profiles":
                    profilesPath = ReadValue(args, ref index, "--profiles");
                    break;
                case "--request-file":
                    requestFilePath = ReadValue(args, ref index, "--request-file");
                    break;
                case "--profile":
                    profileName = ReadValue(args, ref index, "--profile");
                    break;
                case "--search":
                    schemaSearchTerm = ReadValue(args, ref index, "--search");
                    break;
                case "--schema-name":
                    schemaName = ReadValue(args, ref index, "--schema-name");
                    break;
                case "--describe-if-single":
                    describeIfSingle = true;
                    break;
                case "--maximum-rows":
                    maximumRows = ReadPositiveInteger(args, ref index, "--maximum-rows");
                    break;
                case "--timeout-seconds":
                    timeoutSeconds = ReadPositiveInteger(args, ref index, "--timeout-seconds");
                    break;
                case "--help" or "-h":
                    return new CliOptions(command, profilesPath, requestFilePath, profileName, null, true);
                default:
                    throw new DatabaseReadExpectedException(
                        "unknown-argument",
                        $"Unknown argument '{args[index]}'.",
                        DatabaseReadExitCode.InvalidRequest);
            }
        }

        var hasDirectSchemaArguments = schemaSearchTerm is not null ||
                                       schemaName is not null ||
                                       describeIfSingle ||
                                       maximumRows is not null ||
                                       timeoutSeconds is not null;
        if (hasDirectSchemaArguments && command != DatabaseReadCommand.Schema)
        {
            throw new DatabaseReadExpectedException(
                "schema-arguments-require-schema-command",
                "Arguments --search, --schema-name, --describe-if-single, --maximum-rows and --timeout-seconds are only accepted by the schema command.",
                DatabaseReadExitCode.InvalidRequest);
        }

        if (hasDirectSchemaArguments && schemaSearchTerm is null)
        {
            throw new DatabaseReadExpectedException(
                "schema-search-required",
                "Direct schema discovery requires --search <term>.",
                DatabaseReadExitCode.InvalidRequest);
        }

        if (schemaSearchTerm is not null && requestFilePath is not null)
        {
            throw new DatabaseReadExpectedException(
                "conflicting-schema-input",
                "Use either direct schema arguments or --request-file, not both.",
                DatabaseReadExitCode.InvalidRequest);
        }

        var directSchema = schemaSearchTerm is null
            ? null
            : new CliDirectSchemaOptions(
                schemaSearchTerm,
                schemaName,
                describeIfSingle,
                maximumRows,
                timeoutSeconds);

        return new CliOptions(
            command,
            profilesPath is null ? null : Path.GetFullPath(profilesPath),
            requestFilePath is null ? null : ResolveRequestFilePath(requestFilePath),
            profileName,
            directSchema,
            false);
    }

    public static string HelpText =>
        """
        NewHeap database read tool

        Reads one direct schema search or one versioned JSON request and writes one JSON
        response to stdout.
        Database connection strings are resolved through a checked-in profile and the normal
        NewHeap appsettings/secrets substitution flow. They are never accepted as arguments.

        Usage:
          newheap-db validate [--profiles <path>] [--profile <name>] [--request-file <path>]
          newheap-db query [--profiles <path>] [--profile <name>] [--request-file <path>]
          newheap-db schema [--profiles <path>] [--profile <name>] [--request-file <path>]
          newheap-db schema [--profiles <path>] [--profile <name>] --search <term>
                            [--schema-name <name>] [--describe-if-single]
                            [--maximum-rows <count>] [--timeout-seconds <seconds>]

        Commands:
          validate  Dry-run a query or schema request and its profile without connecting.
          query     Validate the request, verify the principal and execute the diagnostic query.
          schema    Validate the request, verify the principal and inspect selectable schema.

        Profile discovery:
          Without --profiles, the tool searches upward from the current directory for
          .newheap/database-read.json.
          Without --profile, the catalog's only profile is selected automatically. A catalog
          with multiple profiles requires --profile or a profile in the JSON request.

        Direct schema discovery:
          --search filters selectable table and view names without requiring JSON input.
          --schema-name optionally limits the search to one schema.
          --describe-if-single returns full columns, indexes and relationships when the search
          has exactly one untruncated match, in the same connection and read-only transaction.

        Request input:
          Without --request-file, the tool reads the JSON request from stdin.
          With --request-file, the tool reads that file instead of stdin. Pass only the file
          path as an argument; never inline the serialized JSON in a shell command.

        Streams:
          stdout  JSON response only
          stderr  Reserved for host diagnostics; never contains a connection string
        """;

    private static string ReadValue(string[] args, ref int index, string name)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new DatabaseReadExpectedException(
                "missing-argument-value",
                $"Argument '{name}' requires a value.",
                DatabaseReadExitCode.InvalidRequest);
        }

        return args[index];
    }

    private static int ReadPositiveInteger(string[] args, ref int index, string name)
    {
        var value = ReadValue(args, ref index, name);
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new DatabaseReadExpectedException(
                "invalid-argument-value",
                $"Argument '{name}' requires a positive integer.",
                DatabaseReadExitCode.InvalidRequest);
        }

        return parsed;
    }

    private static string ResolveRequestFilePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DatabaseReadExpectedException(
                "invalid-request-file-path",
                "The database read request file path is invalid.",
                DatabaseReadExitCode.InvalidRequest);
        }
    }
}

internal sealed record CliDirectSchemaOptions(
    string SearchTerm,
    string? SchemaName,
    bool DescribeIfSingle,
    int? MaximumRows,
    int? TimeoutSeconds)
{
    public DatabaseReadRequest CreateRequest(string? profileName)
    {
        return new DatabaseReadRequest
        {
            SchemaVersion = 1,
            Profile = profileName,
            Schema = new DatabaseSchemaRequest
            {
                Operation = DescribeIfSingle ? "search-and-describe" : "search",
                SchemaName = SchemaName,
                SearchTerm = SearchTerm
            },
            Limits = new DatabaseReadLimitRequest
            {
                MaximumRows = MaximumRows,
                TimeoutSeconds = TimeoutSeconds
            },
            Reason = "Discover selectable schema for a bounded read-only diagnostic query."
        };
    }
}

internal enum DatabaseReadCommand
{
    Validate,
    Query,
    Schema
}
