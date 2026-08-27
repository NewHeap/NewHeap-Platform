namespace NewHeap.Platform.DatabaseRead;

internal sealed record CliOptions(
    DatabaseReadCommand Command,
    string? ProfilesPath,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            return new CliOptions(DatabaseReadCommand.Validate, null, true);
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

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--profiles":
                    profilesPath = ReadValue(args, ref index, "--profiles");
                    break;
                case "--help" or "-h":
                    return new CliOptions(command, profilesPath, true);
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
            false);
    }

    public static string HelpText =>
        """
        NewHeap database read tool

        Reads one versioned JSON request from stdin and writes one JSON response to stdout.
        Database connection strings are resolved through a checked-in profile and the normal
        NewHeap appsettings/secrets substitution flow. They are never accepted as arguments.

        Usage:
          newheap-db validate [--profiles <path>] < request.json
          newheap-db query [--profiles <path>] < request.json
          newheap-db schema [--profiles <path>] < request.json

        Commands:
          validate  Validate a query or schema request and its profile without connecting.
          query     Verify the database principal is read-only and execute the diagnostic query.
          schema    Verify the principal and inspect only database objects it can select.

        Profile discovery:
          Without --profiles, the tool searches upward from the current directory for
          .newheap/database-read.json.

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
}

internal enum DatabaseReadCommand
{
    Validate,
    Query,
    Schema
}
