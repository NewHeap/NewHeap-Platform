using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Runtime.CompilerServices;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.DAL;

public readonly record struct RawSqlString(string Value)
{
    public override string ToString() => Value;
}

public static class RepositorySqlExtensions
{
    public static RawSqlString Raw(this string? value)
    {
        return new RawSqlString(value ?? string.Empty);
    }

    public static Task<int> ExecuteNhSql<TEntity>(
        this IRepository<TEntity> repository,
        FormattableString sql,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(sql);

        var sqlWithRawArguments = CreateSqlWithRawArguments(sql);
        return repository.Context.Database.ExecuteSqlAsync(sqlWithRawArguments, cancellationToken);
    }

    public static IQueryable<TEntity> ExecuteNhSqlQuery<TEntity>(
        this IRepository<TEntity> repository,
        FormattableString sql)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(sql);

        var sqlWithRawArguments = CreateSqlWithRawArguments(sql);
        return repository.Context.Set<TEntity>().FromSql(sqlWithRawArguments);
    }

    public static IQueryable<TResult> ExecuteNhSqlQuery<TResult>(
        this DbContext context,
        FormattableString sql)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sql);

        return context.Database.ExecuteNhSqlQuery<TResult>(sql);
    }

    public static IQueryable<TResult> ExecuteNhSqlQuery<TResult>(
        this DatabaseFacade database,
        FormattableString sql)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sql);

        var sqlWithRawArguments = CreateSqlWithRawArguments(sql);
        return database.SqlQuery<TResult>(sqlWithRawArguments);
    }

    internal static FormattableString CreateSqlWithRawArguments(FormattableString sql)
    {
        var arguments = sql.GetArguments();
        var newArguments = new List<object?>();
        var mappedArgumentIndexes = new Dictionary<int, int>();
        var newFormat = new StringBuilder(sql.Format.Length);

        for (var i = 0; i < sql.Format.Length; i++)
        {
            var current = sql.Format[i];
            if (current == '{')
            {
                if (i + 1 < sql.Format.Length && sql.Format[i + 1] == '{')
                {
                    newFormat.Append("{{");
                    i++;
                    continue;
                }

                var formatItemEnd = sql.Format.IndexOf('}', i + 1);
                if (formatItemEnd == -1)
                {
                    newFormat.Append(current);
                    continue;
                }

                var formatItem = sql.Format.Substring(i + 1, formatItemEnd - i - 1);
                var indexLength = GetArgumentIndexLength(formatItem);
                if (indexLength == 0 || !int.TryParse(formatItem[..indexLength], out var argumentIndex))
                {
                    newFormat.Append(sql.Format, i, formatItemEnd - i + 1);
                    i = formatItemEnd;
                    continue;
                }

                if (argumentIndex < 0 || argumentIndex >= arguments.Length)
                {
                    throw new FormatException($"Argument index {argumentIndex} is outside the argument list.");
                }

                if (arguments[argumentIndex] is RawSqlString rawSqlString)
                {
                    newFormat.Append(EscapeCompositeFormatLiteral(rawSqlString.Value));
                }
                else
                {
                    if (!mappedArgumentIndexes.TryGetValue(argumentIndex, out var newArgumentIndex))
                    {
                        newArgumentIndex = newArguments.Count;
                        mappedArgumentIndexes.Add(argumentIndex, newArgumentIndex);
                        newArguments.Add(arguments[argumentIndex]);
                    }

                    newFormat.Append('{');
                    newFormat.Append(newArgumentIndex);
                    newFormat.Append(formatItem[indexLength..]);
                    newFormat.Append('}');
                }

                i = formatItemEnd;
                continue;
            }

            if (current == '}' && i + 1 < sql.Format.Length && sql.Format[i + 1] == '}')
            {
                newFormat.Append("}}");
                i++;
                continue;
            }

            newFormat.Append(current);
        }

        return FormattableStringFactory.Create(newFormat.ToString(), newArguments.ToArray());
    }

    private static int GetArgumentIndexLength(string formatItem)
    {
        var indexLength = 0;
        while (indexLength < formatItem.Length && char.IsDigit(formatItem[indexLength]))
        {
            indexLength++;
        }

        return indexLength;
    }

    private static string EscapeCompositeFormatLiteral(string value)
    {
        return value
            .Replace("{", "{{")
            .Replace("}", "}}");
    }
}
