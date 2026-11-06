using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.Common.Services;

/// <summary>
/// The text match of collection search and of the <c>LIKE</c> filter. Collection processing matches
/// with <c>EF.Functions.Like(value, NhCollectionTextMatch.Pattern(pattern))</c>.
/// </summary>
/// <remarks>
/// <para>
/// Without a provider registration EF Core evaluates the marked pattern into a query parameter before
/// it translates the query, so the match is the provider's <c>LIKE</c> and follows its collation. SQL
/// Server's default collation matches case-insensitively.
/// </para>
/// <para>
/// A provider registration may keep the mark and translate the match itself. <c>UseNewHeapPostgreSql</c>
/// matches with <c>ILIKE</c>, so the same search finds the same rows on PostgreSQL as on SQL Server;
/// <c>UseNewHeapCaseSensitiveCollectionTextMatch</c> keeps PostgreSQL's case-sensitive <c>LIKE</c>.
/// The wildcards of the pattern are unchanged: <c>%</c> and <c>_</c> keep their <c>LIKE</c> meaning.
/// </para>
/// </remarks>
public static class NhCollectionTextMatch
{
    private static readonly MethodInfo LikeMethod = typeof(DbFunctionsExtensions).GetMethod(
        nameof(DbFunctionsExtensions.Like),
        [typeof(DbFunctions), typeof(string), typeof(string)])!;

    private static readonly MethodInfo PatternMethod = typeof(NhCollectionTextMatch).GetMethod(
        nameof(Pattern),
        [typeof(string)])!;

    /// <summary>
    /// Marks <paramref name="pattern"/> as the pattern of a collection text match and returns it
    /// unchanged. Use it in a query of your own to match text as collection search does, for example
    /// <c>EF.Functions.Like(project.Name, NhCollectionTextMatch.Pattern("%" + text + "%"))</c>.
    /// </summary>
    /// <remarks>
    /// The pattern must not read the row: EF Core evaluates it before it sends the query.
    /// </remarks>
    public static string? Pattern(string? pattern)
    {
        return pattern;
    }

    /// <summary>
    /// <c>EF.Functions.Like(value, NhCollectionTextMatch.Pattern(pattern))</c> as an expression.
    /// </summary>
    internal static Expression CreateLike(Expression value, Expression pattern)
    {
        return Expression.Call(
            LikeMethod,
            Expression.Constant(EF.Functions),
            value,
            Expression.Call(PatternMethod, pattern));
    }
}
