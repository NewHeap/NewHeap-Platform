namespace NewHeap.Platform.DatabaseRead;

internal static class SqlReadOnlyPolicy
{
    private static readonly HashSet<string> ForbiddenTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALTER", "BACKUP", "BEGIN", "BULK", "CALL", "CLUSTER", "COMMIT", "COMMENT", "COPY",
        "CREATE", "DBCC", "DECLARE", "DELETE", "DENY", "DO", "DROP", "EXEC", "EXECUTE", "GRANT",
        "INSERT", "INTO", "KILL", "LISTEN", "LOCK", "MERGE", "NOTIFY", "OPENQUERY", "OPENROWSET",
        "OPENDATASOURCE", "RECONFIGURE", "REFRESH", "REINDEX", "RESET", "RESTORE", "REVOKE",
        "ROLLBACK", "SAVE", "SECURITY", "SET", "SHUTDOWN", "TRUNCATE", "UNLISTEN", "UPDATE",
        "UPDATETEXT", "USE", "VACUUM", "WAITFOR", "WRITETEXT"
    };

    private static readonly HashSet<string> SqlServerForbiddenTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "HOLDLOCK", "PAGLOCK", "ROWLOCK", "TABLOCK", "TABLOCKX", "UPDLOCK", "XLOCK"
    };

    private static readonly HashSet<string> PostgreSqlForbiddenTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "DBLINK", "DBLINK_CONNECT", "DBLINK_EXEC", "LO_EXPORT", "LO_IMPORT", "NEXTVAL",
        "PG_ADVISORY_LOCK", "PG_ADVISORY_LOCK_SHARED", "PG_ADVISORY_UNLOCK",
        "PG_ADVISORY_UNLOCK_ALL", "PG_ADVISORY_UNLOCK_SHARED", "PG_ADVISORY_XACT_LOCK",
        "PG_ADVISORY_XACT_LOCK_SHARED", "PG_BACKUP_START", "PG_BACKUP_STOP",
        "PG_CANCEL_BACKEND", "PG_CREATE_RESTORE_POINT", "PG_LOGICAL_EMIT_MESSAGE", "PG_LOGDIR_LS",
        "PG_LS_DIR", "PG_NOTIFY", "PG_PROMOTE", "PG_READ_BINARY_FILE", "PG_READ_FILE",
        "PG_RELOAD_CONF", "PG_ROTATE_LOGFILE", "PG_SLEEP", "PG_STAT_FILE",
        "PG_TERMINATE_BACKEND", "PG_TRY_ADVISORY_LOCK", "PG_TRY_ADVISORY_LOCK_SHARED",
        "PG_TRY_ADVISORY_XACT_LOCK", "PG_TRY_ADVISORY_XACT_LOCK_SHARED", "SET_CONFIG", "SETVAL",
        "SHARE"
    };

    public static void Validate(string sql, DatabaseProviderKind provider)
    {
        var tokens = Tokenize(sql, provider);
        if (tokens.Count == 0 ||
            !(tokens[0].Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
              tokens[0].Equals("WITH", StringComparison.OrdinalIgnoreCase)))
        {
            Reject("query-not-read-only", "Only SELECT statements and read-only common table expressions are allowed.");
        }

        if (!tokens.Contains("SELECT", StringComparer.OrdinalIgnoreCase))
        {
            Reject("query-not-read-only", "The query does not contain a SELECT statement.");
        }

        foreach (var token in tokens)
        {
            if (ForbiddenTokens.Contains(token))
            {
                Reject("query-not-read-only", $"SQL token '{token}' is not allowed by the read-only policy.");
            }

            if (provider == DatabaseProviderKind.SqlServer && SqlServerForbiddenTokens.Contains(token))
            {
                Reject("locking-hint-not-allowed", $"SQL Server locking hint '{token}' is not allowed.");
            }

            if (provider == DatabaseProviderKind.PostgreSql && PostgreSqlForbiddenTokens.Contains(token))
            {
                Reject("function-not-allowed", $"PostgreSQL function '{token}' is not allowed.");
            }
        }
    }

    private static IReadOnlyList<string> Tokenize(string sql, DatabaseProviderKind provider)
    {
        var tokens = new List<string>();
        var semicolonPositions = new List<int>();
        var lastOutsideWhitespace = -1;

        for (var index = 0; index < sql.Length;)
        {
            var current = sql[index];

            if (char.IsWhiteSpace(current) || current == '\uFEFF')
            {
                index++;
                continue;
            }

            lastOutsideWhitespace = index;

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                Reject("comments-not-allowed", "SQL comments are not allowed.");
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                Reject("comments-not-allowed", "SQL comments are not allowed.");
            }

            if (current == ';')
            {
                semicolonPositions.Add(index);
                index++;
                continue;
            }

            if (current == '\'')
            {
                index = SkipQuoted(sql, index, '\'', '\'');
                continue;
            }

            if (current == '"')
            {
                index = SkipQuoted(sql, index, '"', '"');
                continue;
            }

            if (current == '[' && provider == DatabaseProviderKind.SqlServer)
            {
                index = SkipQuoted(sql, index, '[', ']');
                continue;
            }

            if (current == '$' && provider == DatabaseProviderKind.PostgreSql)
            {
                Reject("dollar-quoting-not-allowed", "PostgreSQL dollar-quoted strings are not allowed.");
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index;
                index++;
                while (index < sql.Length &&
                       (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                {
                    index++;
                }

                tokens.Add(sql[start..index].ToUpperInvariant());
                continue;
            }

            index++;
        }

        if (semicolonPositions.Count > 1 ||
            (semicolonPositions.Count == 1 && semicolonPositions[0] != lastOutsideWhitespace))
        {
            Reject("multiple-statements-not-allowed", "Only one SQL statement is allowed.");
        }

        return tokens;
    }

    private static int SkipQuoted(string sql, int start, char opening, char closing)
    {
        var index = start + 1;
        while (index < sql.Length)
        {
            if (sql[index] != closing)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == closing)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        Reject("unterminated-quoted-value", $"SQL contains an unterminated {opening} quoted value.");
        return sql.Length;
    }

    private static void Reject(string code, string message)
    {
        throw new DatabaseReadExpectedException(code, message, DatabaseReadExitCode.PolicyRejected);
    }
}
