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

    private static readonly HashSet<string> PostgreSqlForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHARE"
    };

    /// <summary>
    /// Functions refused on PostgreSQL. A double-quoted identifier names the same function when its
    /// text matches, so quoted identifiers are compared with this list as well. The XML and text
    /// search functions on the last lines run SQL text that the policy reads as a string literal.
    /// </summary>
    private static readonly HashSet<string> PostgreSqlForbiddenFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DBLINK", "DBLINK_CONNECT", "DBLINK_EXEC", "LO_EXPORT", "LO_IMPORT", "NEXTVAL",
        "PG_ADVISORY_LOCK", "PG_ADVISORY_LOCK_SHARED", "PG_ADVISORY_UNLOCK",
        "PG_ADVISORY_UNLOCK_ALL", "PG_ADVISORY_UNLOCK_SHARED", "PG_ADVISORY_XACT_LOCK",
        "PG_ADVISORY_XACT_LOCK_SHARED", "PG_BACKUP_START", "PG_BACKUP_STOP",
        "PG_CANCEL_BACKEND", "PG_CREATE_RESTORE_POINT", "PG_LOGICAL_EMIT_MESSAGE", "PG_LOGDIR_LS",
        "PG_LS_DIR", "PG_NOTIFY", "PG_PROMOTE", "PG_READ_BINARY_FILE", "PG_READ_FILE",
        "PG_RELOAD_CONF", "PG_ROTATE_LOGFILE", "PG_SLEEP", "PG_SLEEP_FOR", "PG_SLEEP_UNTIL",
        "PG_STAT_FILE", "PG_TERMINATE_BACKEND", "PG_TRY_ADVISORY_LOCK", "PG_TRY_ADVISORY_LOCK_SHARED",
        "PG_TRY_ADVISORY_XACT_LOCK", "PG_TRY_ADVISORY_XACT_LOCK_SHARED", "SET_CONFIG", "SETVAL",
        "CURSOR_TO_XML", "CURSOR_TO_XMLSCHEMA", "QUERY_TO_XML", "QUERY_TO_XML_AND_XMLSCHEMA",
        "QUERY_TO_XMLSCHEMA", "TS_REWRITE", "TS_STAT"
    };

    /// <summary>
    /// Functions refused on MySQL and MariaDB: they sleep or wait, take or release locks that other
    /// sessions wait for, read server files or change a sequence. A backtick- or double-quoted
    /// identifier with the same text is refused as well.
    /// </summary>
    private static readonly HashSet<string> MySqlForbiddenFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "BENCHMARK", "GET_LOCK", "LOAD_FILE", "MASTER_GTID_WAIT", "MASTER_POS_WAIT", "NEXTVAL",
        "RELEASE_ALL_LOCKS", "RELEASE_LOCK", "SETVAL", "SLEEP", "SOURCE_POS_WAIT",
        "WAIT_FOR_EXECUTED_GTID_SET", "WAIT_UNTIL_SQL_THREAD_AFTER_GTIDS"
    };

    public static void Validate(string sql, DatabaseProviderKind provider)
    {
        var (tokens, quotedIdentifiers) = Tokenize(sql, provider);
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

        var mySqlFamily = IsMySqlFamily(provider);
        for (var position = 0; position < tokens.Count; position++)
        {
            var token = tokens[position];
            if (ForbiddenTokens.Contains(token))
            {
                Reject("query-not-read-only", $"SQL token '{token}' is not allowed by the read-only policy.");
            }

            if (provider == DatabaseProviderKind.SqlServer && SqlServerForbiddenTokens.Contains(token))
            {
                Reject("locking-hint-not-allowed", $"SQL Server locking hint '{token}' is not allowed.");
            }

            if (provider == DatabaseProviderKind.PostgreSql &&
                (PostgreSqlForbiddenFunctions.Contains(token) || PostgreSqlForbiddenKeywords.Contains(token)))
            {
                Reject("function-not-allowed", $"PostgreSQL function '{token}' is not allowed.");
            }

            if (mySqlFamily && MySqlForbiddenFunctions.Contains(token))
            {
                Reject("function-not-allowed", $"{ProviderName(provider)} function '{token}' is not allowed.");
            }

            // FOR SHARE and LOCK IN SHARE MODE take row locks that writers wait for; FOR UPDATE and
            // LOCK are refused as tokens already.
            if (mySqlFamily && token.Equals("SHARE", StringComparison.OrdinalIgnoreCase))
            {
                Reject("locking-read-not-allowed", $"{ProviderName(provider)} locking reads are not allowed.");
            }

            if (mySqlFamily &&
                token.Equals("NEXT", StringComparison.OrdinalIgnoreCase) &&
                position + 1 < tokens.Count &&
                tokens[position + 1].Equals("VALUE", StringComparison.OrdinalIgnoreCase))
            {
                Reject("function-not-allowed", $"{ProviderName(provider)} NEXT VALUE FOR is not allowed.");
            }
        }

        var forbiddenFunctions = provider switch
        {
            DatabaseProviderKind.PostgreSql => PostgreSqlForbiddenFunctions,
            DatabaseProviderKind.MySql or DatabaseProviderKind.MariaDb => MySqlForbiddenFunctions,
            _ => null
        };
        foreach (var identifier in quotedIdentifiers)
        {
            if (forbiddenFunctions?.Contains(identifier) == true)
            {
                Reject("function-not-allowed", $"{ProviderName(provider)} function '{identifier}' is not allowed.");
            }
        }
    }

    private static bool IsMySqlFamily(DatabaseProviderKind provider) =>
        provider is DatabaseProviderKind.MySql or DatabaseProviderKind.MariaDb;

    private static string ProviderName(DatabaseProviderKind provider) => provider switch
    {
        DatabaseProviderKind.SqlServer => "SQL Server",
        DatabaseProviderKind.PostgreSql => "PostgreSQL",
        DatabaseProviderKind.MySql => "MySQL",
        _ => "MariaDB"
    };

    /// <summary>
    /// Splits the SQL into upper-cased words outside quoted text and, on PostgreSQL, MySQL and
    /// MariaDB, the upper-cased text of every quoted identifier, with doubled quotes unescaped.
    /// MySQL and MariaDB read a double-quoted value as an identifier under <c>ANSI_QUOTES</c>, so its
    /// text counts as one there too.
    /// </summary>
    private static (IReadOnlyList<string> Tokens, IReadOnlyList<string> QuotedIdentifiers) Tokenize(
        string sql,
        DatabaseProviderKind provider)
    {
        var tokens = new List<string>();
        var quotedIdentifiers = new List<string>();
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

            if (current == '#' && IsMySqlFamily(provider))
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
                if (IsMySqlFamily(provider))
                {
                    RejectMySqlBackslash(sql, index, '\'', provider);
                }

                index = SkipQuoted(sql, index, '\'', '\'');
                continue;
            }

            if (current == '`' && IsMySqlFamily(provider))
            {
                var end = SkipQuoted(sql, index, '`', '`');
                quotedIdentifiers.Add(sql[(index + 1)..(end - 1)]
                    .Replace("``", "`", StringComparison.Ordinal)
                    .ToUpperInvariant());
                index = end;
                continue;
            }

            if (current == '"')
            {
                if (IsMySqlFamily(provider))
                {
                    RejectMySqlBackslash(sql, index, '"', provider);
                }

                var end = SkipQuoted(sql, index, '"', '"');
                if (provider == DatabaseProviderKind.PostgreSql || IsMySqlFamily(provider))
                {
                    quotedIdentifiers.Add(sql[(index + 1)..(end - 1)]
                        .Replace("\"\"", "\"", StringComparison.Ordinal)
                        .ToUpperInvariant());
                }

                index = end;
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

                if (provider == DatabaseProviderKind.PostgreSql &&
                    index - start == 1 &&
                    sql[start] is 'U' or 'u' &&
                    index + 1 < sql.Length &&
                    sql[index] == '&' &&
                    sql[index + 1] == '"')
                {
                    Reject(
                        "unicode-escaped-identifier-not-allowed",
                        "PostgreSQL Unicode-escaped identifiers are not allowed.");
                }

                // In an escape string a backslash can escape the quote that would end a standard
                // string, so its end is not where this tokenizer would look for it. The provider
                // keeps standard_conforming_strings on, so other string literals end as read here.
                if (provider == DatabaseProviderKind.PostgreSql &&
                    index - start == 1 &&
                    sql[start] is 'E' or 'e' &&
                    index < sql.Length &&
                    sql[index] == '\'')
                {
                    Reject(
                        "escape-string-not-allowed",
                        "PostgreSQL escape strings are not allowed. Pass the value as a parameter.");
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

        return (tokens, quotedIdentifiers);
    }

    /// <summary>
    /// MySQL and MariaDB end a quoted value where a backslash does not escape its quote, unless the
    /// session's <c>sql_mode</c> has <c>NO_BACKSLASH_ESCAPES</c>, so a value with a backslash can end
    /// elsewhere than where the policy reads it. Such a value is refused; it goes in a typed
    /// parameter instead. A doubled quote ends where the policy reads it in every mode.
    /// </summary>
    private static void RejectMySqlBackslash(string sql, int start, char quote, DatabaseProviderKind provider)
    {
        for (var index = start + 1; index < sql.Length; index++)
        {
            if (sql[index] == '\\')
            {
                Reject(
                    "backslash-escape-not-allowed",
                    $"{ProviderName(provider)} string literals and quoted identifiers with a backslash are not allowed. Pass the value as a parameter.");
            }

            if (sql[index] != quote)
            {
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == quote)
            {
                index++;
                continue;
            }

            return;
        }
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
