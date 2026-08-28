namespace NewHeap.Platform.DatabaseRead;

internal sealed record CliOptions(
    DatabaseReadCommand Command,
    string? ProfilesPath,
    string? RequestFilePath,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            return new CliOptions(DatabaseReadCommand.Validate, null, null, true);
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
                case "--help" or "-h":
                    return new CliOptions(command, profilesPath, requestFilePath, true);
                default:
                    throw new DatabaseReadExpectedException(
                        "unknown-argument",
                        $"Unknown argument '{args[index]}'.",
                        DatabaseReadExitCode.InvalidRequest);
            }
        }

        return new CliOptions(
            command,
            profilesPath is null ? null : Path.GetFullPath(profilesPath),
            requestFilePath is null ? null : ResolveRequestFilePath(requestFilePath),
            false);
    }

    public static string HelpText =>
        """
        NewHeap database read tool

        Reads one versioned JSON request from stdin or --request-file and writes one JSON
        response to stdout.
        Database connection strings are resolved through a checked-in profile and the normal
        NewHeap appsettings/secrets substitution flow. They are never accepted as arguments.

        Usage:
          newheap-db validate [--profiles <path>] [--request-file <path>]
          newheap-db query [--profiles <path>] [--request-file <path>]
          newheap-db schema [--profiles <path>] [--request-file <path>]

        Commands:
          validate  Validate a query or schema request and its profile without connecting.
          query     Verify the database principal is read-only and execute the diagnostic query.
          schema    Verify the principal and inspect selectable objects, columns and indexes.

        Profile discovery:
          Without --profiles, the tool searches upward from the current directory for
          .newheap/database-read.json.

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

internal enum DatabaseReadCommand
{
    Validate,
    Query,
    Schema
}
