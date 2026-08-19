using System.Linq.Expressions;

namespace NewHeap.Platform.Common.Models;

internal static class CollectionProcessingExpressionResolver
{
    public static string GetFilterKey<TEntity>(Expression<Func<TEntity, object?>> selector)
    {
        return NormalizeFilterKey(ResolveFilterKey(StripConvert(selector.Body)));
    }

    public static string GetOrderKey<TEntity>(Expression<Func<TEntity, object?>> selector)
    {
        return NormalizeOrderKey(ResolveOrderKey(StripConvert(selector.Body)));
    }

    private static string ResolveFilterKey(Expression expression)
    {
        expression = StripConvert(expression);

        if (expression is MemberExpression memberExpression)
        {
            var parent = memberExpression.Expression == null ? null : StripConvert(memberExpression.Expression);

            if (parent is MethodCallExpression memberSourceCall &&
                TryResolveCollectionMethod(memberSourceCall, out var collectionKey, out _, out _))
            {
                return AppendChildKey(collectionKey, memberExpression.Member.Name);
            }

            return ResolveMemberPath(memberExpression);
        }

        if (expression is MethodCallExpression methodCallExpression)
        {
            if (TryResolveCollectionMethod(methodCallExpression, out var key, out _, out _))
            {
                return key;
            }
        }

        throw new ArgumentException("A filterable selector can only be inferred from a member access or collection projection expression.");
    }

    private static string ResolveOrderKey(Expression expression)
    {
        expression = StripConvert(expression);

        if (expression is MemberExpression memberExpression)
        {
            var parent = memberExpression.Expression == null ? null : StripConvert(memberExpression.Expression);

            if (parent is MethodCallExpression memberSourceCall &&
                TryResolveCollectionMethod(memberSourceCall, out var collectionKey, out _, out _))
            {
                return AppendChildKey(collectionKey, memberExpression.Member.Name);
            }

            return ResolveMemberPath(memberExpression);
        }

        if (expression is MethodCallExpression methodCallExpression)
        {
            if (TryResolveCollectionMethod(methodCallExpression, out var key, out _, out _))
            {
                return key;
            }
        }

        throw new ArgumentException("An orderable selector can only be inferred from a member access or collection projection expression.");
    }

    private static bool TryResolveCollectionMethod(
        MethodCallExpression methodCallExpression,
        out string key,
        out CollectionTerminal terminal,
        out CollectionOrderDirection orderDirection)
    {
        key = string.Empty;
        terminal = CollectionTerminal.Any;
        orderDirection = CollectionOrderDirection.Ascending;

        if (IsFirstOrLast(methodCallExpression.Method.Name, out terminal))
        {
            var source = StripConvert(methodCallExpression.Arguments[0]);
            return TryResolveCollectionMethod(source as MethodCallExpression ?? throw new ArgumentException("First/Last collection selectors must be applied to a collection projection."), out key, out _, out orderDirection)
                   && TryApplyTerminal(ref key, terminal, orderDirection);
        }

        if (IsOrdering(methodCallExpression.Method.Name, out orderDirection))
        {
            var collectionPath = ResolveExpressionPath(methodCallExpression.Arguments[0]);
            key = collectionPath;
            return true;
        }

        if (methodCallExpression.Method.Name == nameof(Enumerable.Select) && methodCallExpression.Arguments.Count == 2)
        {
            var source = StripConvert(methodCallExpression.Arguments[0]);
            var lambda = GetLambda(methodCallExpression.Arguments[1]);
            var selectedPath = ResolveFilterKey(lambda.Body);
            terminal = CollectionTerminal.Any;

            if (source is MethodCallExpression sourceMethodCall && IsOrdering(sourceMethodCall.Method.Name, out orderDirection))
            {
                var collectionPath = ResolveExpressionPath(sourceMethodCall.Arguments[0]);
                key = $"{collectionPath}.{{first:{GetDirectionKey(orderDirection)}}}{selectedPath}";
                return true;
            }

            key = $"{ResolveExpressionPath(source)}{{any}}.{selectedPath}";
            return true;
        }

        return false;
    }

    private static bool TryApplyTerminal(ref string key, CollectionTerminal terminal, CollectionOrderDirection direction)
    {
        if (terminal == CollectionTerminal.Any)
        {
            return true;
        }

        var anyTokenIndex = key.IndexOf("{any}.", StringComparison.Ordinal);
        if (anyTokenIndex >= 0)
        {
            key = key.Remove(anyTokenIndex, "{any}.".Length)
                .Insert(anyTokenIndex, $".{{{GetTerminalKey(terminal)}:{GetDirectionKey(direction)}}}");
            return true;
        }

        key = $"{key}.{{{GetTerminalKey(terminal)}:{GetDirectionKey(direction)}}}";
        return true;
    }

    private static string AppendChildKey(string collectionKey, string childKey)
    {
        return collectionKey.EndsWith("}", StringComparison.Ordinal)
            ? $"{collectionKey}{childKey}"
            : $"{collectionKey}.{childKey}";
    }

    private static string ResolveExpressionPath(Expression expression)
    {
        expression = StripConvert(expression);

        if (expression is MemberExpression memberExpression)
        {
            return ResolveMemberPath(memberExpression);
        }

        if (expression is MethodCallExpression methodCallExpression &&
            TryResolveCollectionMethod(methodCallExpression, out var key, out _, out _))
        {
            return key;
        }

        throw new ArgumentException("A collection processing key can only be inferred from a member access or collection projection expression.");
    }

    private static string ResolveMemberPath(MemberExpression memberExpression)
    {
        var members = new Stack<string>();
        Expression? expression = memberExpression;

        while (expression is MemberExpression currentMemberExpression)
        {
            members.Push(currentMemberExpression.Member.Name);
            expression = currentMemberExpression.Expression == null ? null : StripConvert(currentMemberExpression.Expression);
        }

        if (expression is not ParameterExpression || !members.Any())
        {
            throw new ArgumentException("A collection processing key can only be inferred from a member access expression.");
        }

        return string.Join(".", members);
    }

    private static LambdaExpression GetLambda(Expression expression)
    {
        expression = StripConvert(expression);

        if (expression is UnaryExpression unaryExpression && unaryExpression.NodeType == ExpressionType.Quote)
        {
            expression = unaryExpression.Operand;
        }

        return expression as LambdaExpression
               ?? throw new ArgumentException("Collection projection methods must use a lambda selector.");
    }

    private static bool IsFirstOrLast(string methodName, out CollectionTerminal terminal)
    {
        terminal = methodName switch
        {
            nameof(Enumerable.First) or nameof(Enumerable.FirstOrDefault) => CollectionTerminal.First,
            nameof(Enumerable.Last) or nameof(Enumerable.LastOrDefault) => CollectionTerminal.Last,
            _ => CollectionTerminal.Any
        };

        return terminal != CollectionTerminal.Any;
    }

    private static bool IsOrdering(string methodName, out CollectionOrderDirection direction)
    {
        direction = methodName switch
        {
            nameof(Enumerable.OrderByDescending) or nameof(Enumerable.ThenByDescending) => CollectionOrderDirection.Descending,
            nameof(Enumerable.OrderBy) or nameof(Enumerable.ThenBy) => CollectionOrderDirection.Ascending,
            _ => CollectionOrderDirection.Ascending
        };

        return methodName is nameof(Enumerable.OrderBy)
            or nameof(Enumerable.ThenBy)
            or nameof(Enumerable.OrderByDescending)
            or nameof(Enumerable.ThenByDescending);
    }

    private static string NormalizeFilterKey(string key)
    {
        return key
            .Replace(".{first:asc}", "{any}.", StringComparison.OrdinalIgnoreCase)
            .Replace(".{first:desc}", "{any}.", StringComparison.OrdinalIgnoreCase)
            .Replace(".{last:asc}", "{any}.", StringComparison.OrdinalIgnoreCase)
            .Replace(".{last:desc}", "{any}.", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOrderKey(string key)
    {
        return key.Replace("{any}.", ".{first:asc}", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTerminalKey(CollectionTerminal terminal)
    {
        return terminal == CollectionTerminal.Last ? "last" : "first";
    }

    private static string GetDirectionKey(CollectionOrderDirection direction)
    {
        return direction == CollectionOrderDirection.Descending ? "desc" : "asc";
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression unaryExpression &&
               (unaryExpression.NodeType == ExpressionType.Convert ||
                unaryExpression.NodeType == ExpressionType.ConvertChecked))
        {
            expression = unaryExpression.Operand;
        }

        return expression;
    }

    private enum CollectionTerminal
    {
        Any,
        First,
        Last
    }

    private enum CollectionOrderDirection
    {
        Ascending,
        Descending
    }
}
