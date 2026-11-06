using Microsoft.EntityFrameworkCore.Query;
using NewHeap.Platform.Common.Services;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.AspNet.Common.PostgreSql;

/// <summary>
/// Keeps <see cref="NhCollectionTextMatch.Pattern"/> in the query, so the mark survives until
/// <see cref="PostgreSqlCollectionTextMatchInterceptor"/> translates the match. EF Core still turns
/// the pattern inside the mark into a query parameter.
/// </summary>
internal sealed class PostgreSqlCollectionTextMatchEvaluatableFilter : IEvaluatableExpressionFilterPlugin
{
    internal static readonly MethodInfo PatternMethod = typeof(NhCollectionTextMatch).GetMethod(
        nameof(NhCollectionTextMatch.Pattern),
        [typeof(string)])!;

    public bool IsEvaluatableExpression(Expression expression)
    {
        return expression is not MethodCallExpression call || call.Method != PatternMethod;
    }
}
