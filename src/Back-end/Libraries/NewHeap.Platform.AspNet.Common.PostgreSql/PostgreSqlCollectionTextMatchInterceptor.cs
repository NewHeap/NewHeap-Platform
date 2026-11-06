using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.AspNet.Common.PostgreSql;

/// <summary>
/// Translates a collection text match, <c>EF.Functions.Like(value, NhCollectionTextMatch.Pattern(pattern))</c>,
/// to <c>EF.Functions.ILike(value, pattern)</c> before EF Core compiles the query. Every other
/// <c>LIKE</c> stays as written. The pattern keeps its wildcards; only the case no longer matters.
/// </summary>
internal sealed class PostgreSqlCollectionTextMatchInterceptor : IQueryExpressionInterceptor
{
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        return new CaseInsensitiveTextMatchVisitor().Visit(queryExpression);
    }

    private sealed class CaseInsensitiveTextMatchVisitor : ExpressionVisitor
    {
        private static readonly MethodInfo LikeMethod = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            [typeof(DbFunctions), typeof(string), typeof(string)])!;

        private static readonly MethodInfo ILikeMethod = typeof(NpgsqlDbFunctionsExtensions).GetMethod(
            nameof(NpgsqlDbFunctionsExtensions.ILike),
            [typeof(DbFunctions), typeof(string), typeof(string)])!;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method == LikeMethod
                && node.Arguments[2] is MethodCallExpression pattern
                && pattern.Method == PostgreSqlCollectionTextMatchEvaluatableFilter.PatternMethod)
            {
                return Expression.Call(
                    ILikeMethod,
                    Visit(node.Arguments[0]),
                    Visit(node.Arguments[1]),
                    Visit(pattern.Arguments[0]));
            }

            // A mark outside a LIKE has nothing to translate; EF Core reads the pattern itself.
            if (node.Method == PostgreSqlCollectionTextMatchEvaluatableFilter.PatternMethod)
            {
                return Visit(node.Arguments[0]);
            }

            return base.VisitMethodCall(node);
        }
    }
}
