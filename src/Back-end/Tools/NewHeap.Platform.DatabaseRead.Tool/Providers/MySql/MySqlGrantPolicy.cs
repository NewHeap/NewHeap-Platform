using System.Text;

namespace NewHeap.Platform.DatabaseRead;

/// <summary>
/// Decides whether one line of <c>SHOW GRANTS</c> stays within what a read-only MySQL or MariaDB
/// principal may hold. The policy denies by default: a line passes only when it is a role grant
/// without <c>ADMIN OPTION</c>, <c>USAGE</c> on <c>*.*</c>, or <c>SELECT</c> on a schema, table or
/// column outside the system schemas, without <c>GRANT OPTION</c>. Every other privilege rejects
/// the principal with its check, and a line the policy cannot read rejects it with
/// <c>grant-not-recognized</c>.
/// </summary>
/// <remarks>
/// A <c>REVOKE</c> line, which MySQL shows for a partial revoke, only narrows a global grant, and
/// every global grant other than <c>USAGE</c> rejects the principal already, so it is skipped. The
/// text after <c>TO</c> names the grantee and, on MariaDB, can hold a password hash; it is read
/// only for <c>GRANT OPTION</c> and <c>ADMIN OPTION</c> and never leaves this class.
/// </remarks>
internal static class MySqlGrantPolicy
{
    /// <summary>
    /// The checks of MySQL and MariaDB principal verification, in the order in which the first
    /// failing one is reported.
    /// </summary>
    public static readonly IReadOnlyList<string> CheckOrder =
    [
        GlobalPrivilege,
        DynamicPrivilege,
        ProxyPrivilege,
        GrantOption,
        RoleAdminOption,
        SystemSchemaSelect,
        SchemaPrivilegeNotAllowlisted,
        TablePrivilegeNotAllowlisted,
        ColumnPrivilegeNotAllowlisted,
        RoutinePrivilege,
        DefinerView,
        GrantNotRecognized
    ];

    public const string GlobalPrivilege = "global-privilege";
    public const string DynamicPrivilege = "dynamic-privilege";
    public const string ProxyPrivilege = "proxy-privilege";
    public const string GrantOption = "grant-option";
    public const string RoleAdminOption = "role-admin-option";
    public const string SystemSchemaSelect = "system-schema-select";
    public const string SchemaPrivilegeNotAllowlisted = "schema-privilege-not-allowlisted";
    public const string TablePrivilegeNotAllowlisted = "table-privilege-not-allowlisted";
    public const string ColumnPrivilegeNotAllowlisted = "column-privilege-not-allowlisted";
    public const string RoutinePrivilege = "routine-privilege";
    public const string DefinerView = "definer-view";
    public const string GrantNotRecognized = "grant-not-recognized";

    /// <summary>
    /// The schemas whose tables hold credentials, other sessions' statements or server state. The
    /// <c>mysql</c> schema holds the password hashes of every account.
    /// </summary>
    private static readonly string[] SystemSchemas = ["mysql", "sys", "performance_schema", "information_schema"];

    /// <summary>
    /// Returns the check that <paramref name="line"/> fails, or <c>null</c> when it stays within the
    /// read-only policy.
    /// </summary>
    public static string? Classify(string line, DatabaseProviderKind flavor)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!TryTokenize(line, out var tokens) || tokens.Count == 0)
        {
            return GrantNotRecognized;
        }

        if (tokens[0].IsWord("REVOKE"))
        {
            return null;
        }

        if (!tokens[0].IsWord("GRANT"))
        {
            return GrantNotRecognized;
        }

        var toIndex = IndexOfWord(tokens, "TO", 1, tokens.Count);
        if (toIndex < 0)
        {
            return GrantNotRecognized;
        }

        var rest = tokens.Skip(toIndex + 1).ToArray();
        var violations = new HashSet<string>(StringComparer.Ordinal);
        if (ContainsWords(rest, "GRANT", "OPTION"))
        {
            violations.Add(GrantOption);
        }

        var onIndex = IndexOfWord(tokens, "ON", 1, toIndex);
        if (onIndex < 0)
        {
            if (!IsAccountList(tokens, 1, toIndex))
            {
                return GrantNotRecognized;
            }

            if (ContainsWords(rest, "ADMIN", "OPTION"))
            {
                violations.Add(RoleAdminOption);
            }

            return First(violations);
        }

        if (onIndex == 2 && tokens[1].IsWord("PROXY"))
        {
            violations.Add(ProxyPrivilege);
            return First(violations);
        }

        if (!TryReadPrivileges(tokens, 1, onIndex, out var privileges) ||
            !TryReadTarget(tokens, onIndex + 1, toIndex, out var target))
        {
            return GrantNotRecognized;
        }

        foreach (var privilege in privileges)
        {
            var violation = ClassifyPrivilege(privilege, target, flavor);
            if (violation is not null)
            {
                violations.Add(violation);
            }
        }

        return First(violations);
    }

    /// <summary>
    /// The check that comes first in <see cref="CheckOrder"/>, or <c>null</c> for none.
    /// </summary>
    public static string? First(IEnumerable<string> checks)
    {
        var found = checks.ToHashSet(StringComparer.Ordinal);

        return CheckOrder.FirstOrDefault(found.Contains);
    }

    /// <summary>
    /// Whether a MySQL schema pattern of a schema-level grant matches a system schema. In such a
    /// pattern <c>%</c> and <c>_</c> are wildcards and a backslash escapes the next character; the
    /// comparison ignores case, because the server may.
    /// </summary>
    public static bool MatchesSystemSchema(string pattern)
    {
        return SystemSchemas.Any(schema => MatchesPattern(pattern, 0, schema, 0));
    }

    private static string? ClassifyPrivilege(GrantPrivilege privilege, GrantTarget target, DatabaseProviderKind flavor)
    {
        var isSelect = privilege.Name == "SELECT";

        return target.Kind switch
        {
            GrantTargetKind.Global when privilege.Name == "USAGE" && !privilege.HasColumns => null,
            GrantTargetKind.Global when flavor == DatabaseProviderKind.MySql && IsDynamicPrivilegeName(privilege.Name) =>
                DynamicPrivilege,
            GrantTargetKind.Global => GlobalPrivilege,
            GrantTargetKind.Schema when isSelect && !privilege.HasColumns =>
                MatchesSystemSchema(target.Schema!) ? SystemSchemaSelect : null,
            GrantTargetKind.Schema => SchemaPrivilegeNotAllowlisted,
            GrantTargetKind.Table when isSelect =>
                IsSystemSchema(target.Schema!) ? SystemSchemaSelect : null,
            GrantTargetKind.Table when privilege.HasColumns => ColumnPrivilegeNotAllowlisted,
            GrantTargetKind.Table => TablePrivilegeNotAllowlisted,
            _ => RoutinePrivilege
        };
    }

    /// <summary>
    /// MySQL writes its static privileges as words separated by spaces and its dynamic privileges,
    /// such as <c>BACKUP_ADMIN</c> or <c>SYSTEM_VARIABLES_ADMIN</c>, as one word with underscores.
    /// </summary>
    private static bool IsDynamicPrivilegeName(string name)
    {
        return name.Contains('_', StringComparison.Ordinal) &&
               name.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    }

    private static bool IsSystemSchema(string schema)
    {
        return SystemSchemas.Any(system => string.Equals(system, schema, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesPattern(string pattern, int patternIndex, string value, int valueIndex)
    {
        while (patternIndex < pattern.Length)
        {
            var current = pattern[patternIndex];
            if (current == '%')
            {
                for (var next = valueIndex; next <= value.Length; next++)
                {
                    if (MatchesPattern(pattern, patternIndex + 1, value, next))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (valueIndex >= value.Length)
            {
                return false;
            }

            if (current == '_')
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (current == '\\' && patternIndex + 1 < pattern.Length)
            {
                patternIndex++;
                current = pattern[patternIndex];
            }

            if (char.ToLowerInvariant(current) != char.ToLowerInvariant(value[valueIndex]))
            {
                return false;
            }

            patternIndex++;
            valueIndex++;
        }

        return valueIndex == value.Length;
    }

    private static bool TryReadPrivileges(
        IReadOnlyList<GrantToken> tokens,
        int start,
        int end,
        out IReadOnlyList<GrantPrivilege> privileges)
    {
        var result = new List<GrantPrivilege>();
        privileges = result;
        var index = start;
        while (index < end)
        {
            var words = new List<string>();
            while (index < end && tokens[index].Kind == GrantTokenKind.Word)
            {
                words.Add(tokens[index].Text.ToUpperInvariant());
                index++;
            }

            if (words.Count == 0)
            {
                return false;
            }

            var hasColumns = false;
            if (index < end && tokens[index].IsSymbol('('))
            {
                index++;
                if (!TryReadIdentifierList(tokens, ref index, end))
                {
                    return false;
                }

                if (index >= end || !tokens[index].IsSymbol(')'))
                {
                    return false;
                }

                index++;
                hasColumns = true;
            }

            result.Add(new GrantPrivilege(string.Join(' ', words), hasColumns));

            if (index == end)
            {
                return true;
            }

            if (!tokens[index].IsSymbol(','))
            {
                return false;
            }

            index++;
        }

        return false;
    }

    private static bool TryReadIdentifierList(IReadOnlyList<GrantToken> tokens, ref int index, int end)
    {
        while (index < end)
        {
            if (!tokens[index].IsName)
            {
                return false;
            }

            index++;
            if (index < end && tokens[index].IsSymbol(','))
            {
                index++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool TryReadTarget(
        IReadOnlyList<GrantToken> tokens,
        int start,
        int end,
        out GrantTarget target)
    {
        target = new GrantTarget(GrantTargetKind.Global, null);
        var parts = tokens.Skip(start).Take(end - start).ToArray();

        if (parts.Length == 3 && parts[0].IsSymbol('*') && parts[1].IsSymbol('.') && parts[2].IsSymbol('*'))
        {
            return true;
        }

        if (parts.Length == 3 && parts[0].IsName && parts[1].IsSymbol('.') && parts[2].IsSymbol('*'))
        {
            target = new GrantTarget(GrantTargetKind.Schema, parts[0].Text);
            return true;
        }

        if (parts.Length == 3 && parts[0].IsName && parts[1].IsSymbol('.') && parts[2].IsName)
        {
            target = new GrantTarget(GrantTargetKind.Table, parts[0].Text);
            return true;
        }

        var routineStart = 0;
        if (parts.Length > 0 && (parts[0].IsWord("PROCEDURE") || parts[0].IsWord("FUNCTION") || parts[0].IsWord("PACKAGE")))
        {
            routineStart = parts.Length > 1 && parts[1].IsWord("BODY") ? 2 : 1;
        }

        if (routineStart > 0 &&
            parts.Length == routineStart + 3 &&
            parts[routineStart].IsName &&
            parts[routineStart + 1].IsSymbol('.') &&
            parts[routineStart + 2].IsName)
        {
            target = new GrantTarget(GrantTargetKind.Routine, parts[routineStart].Text);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A comma-separated list of accounts or roles, such as <c>`reader`@`%`,`auditor`</c>.
    /// </summary>
    private static bool IsAccountList(IReadOnlyList<GrantToken> tokens, int start, int end)
    {
        var index = start;
        while (index < end)
        {
            if (!tokens[index].IsName)
            {
                return false;
            }

            index++;
            if (index < end && tokens[index].IsSymbol('@'))
            {
                index++;
                if (index >= end || !tokens[index].IsName)
                {
                    return false;
                }

                index++;
            }

            if (index == end)
            {
                return true;
            }

            if (!tokens[index].IsSymbol(','))
            {
                return false;
            }

            index++;
        }

        return false;
    }

    private static int IndexOfWord(IReadOnlyList<GrantToken> tokens, string word, int start, int end)
    {
        var depth = 0;
        for (var index = start; index < end; index++)
        {
            if (tokens[index].IsSymbol('('))
            {
                depth++;
            }
            else if (tokens[index].IsSymbol(')'))
            {
                depth--;
            }
            else if (depth == 0 && tokens[index].IsWord(word))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ContainsWords(IReadOnlyList<GrantToken> tokens, string first, string second)
    {
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (tokens[index].IsWord(first) && tokens[index + 1].IsWord(second))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a line into words, backtick-quoted identifiers, single-quoted strings and single
    /// symbols. A doubled quote inside a quoted value stands for the quote itself; a backslash in a
    /// string escapes the next character, as MySQL writes them.
    /// </summary>
    private static bool TryTokenize(string line, out IReadOnlyList<GrantToken> tokens)
    {
        var result = new List<GrantToken>();
        tokens = result;
        var index = 0;
        while (index < line.Length)
        {
            var current = line[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current is '`' or '\'' or '"')
            {
                if (!TryReadQuoted(line, ref index, current, out var text))
                {
                    return false;
                }

                result.Add(new GrantToken(
                    current == '\'' ? GrantTokenKind.String : GrantTokenKind.Identifier,
                    text));
                continue;
            }

            if (char.IsLetterOrDigit(current) || current is '_' or '$')
            {
                var start = index;
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] is '_' or '$'))
                {
                    index++;
                }

                result.Add(new GrantToken(GrantTokenKind.Word, line[start..index]));
                continue;
            }

            result.Add(new GrantToken(GrantTokenKind.Symbol, current.ToString()));
            index++;
        }

        return true;
    }

    private static bool TryReadQuoted(string line, ref int index, char quote, out string text)
    {
        var builder = new StringBuilder();
        index++;
        while (index < line.Length)
        {
            var current = line[index];
            if (current == '\\' && quote != '`' && index + 1 < line.Length)
            {
                builder.Append(line[index + 1]);
                index += 2;
                continue;
            }

            if (current == quote)
            {
                if (index + 1 < line.Length && line[index + 1] == quote)
                {
                    builder.Append(quote);
                    index += 2;
                    continue;
                }

                index++;
                text = builder.ToString();
                return true;
            }

            builder.Append(current);
            index++;
        }

        text = string.Empty;
        return false;
    }

    private enum GrantTokenKind
    {
        Word,
        Identifier,
        String,
        Symbol
    }

    private sealed record GrantToken(GrantTokenKind Kind, string Text)
    {
        /// <summary>
        /// A name as MySQL writes an account, role, schema, table or column in a grant: quoted, or
        /// a plain word when it needs no quotes.
        /// </summary>
        public bool IsName => Kind is GrantTokenKind.Identifier or GrantTokenKind.Word or GrantTokenKind.String;

        public bool IsWord(string word) =>
            Kind == GrantTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);

        public bool IsSymbol(char symbol) =>
            Kind == GrantTokenKind.Symbol && Text.Length == 1 && Text[0] == symbol;
    }

    private sealed record GrantPrivilege(string Name, bool HasColumns);

    private enum GrantTargetKind
    {
        Global,
        Schema,
        Table,
        Routine
    }

    private sealed record GrantTarget(GrantTargetKind Kind, string? Schema);
}
