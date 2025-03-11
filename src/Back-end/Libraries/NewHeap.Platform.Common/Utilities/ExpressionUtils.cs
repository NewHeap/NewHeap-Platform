using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.Common.Utilities;

public static partial class ExpressionUtils
{
    public static Expression? JoinExpressions(IList<Expression> expressions,
        Func<Expression, Expression, Expression> join)
    {
        Expression? andExpression = null;

        foreach (var expression in expressions)
        {
            if (andExpression == null)
            {
                andExpression = expression;
            }
            else
            {
                andExpression = join(andExpression, expression);
            }
        }

        return andExpression;
    }

    public static Expression CreateMemberAccess(Expression target, string selector)
    {
        var expression = target;
        var selectorParts = selector.Split('.').ToList();
        for (var i = 0; i < selectorParts.Count; i++)
        {
            var selectorPart = selectorParts[i];
            expression = Expression.PropertyOrField(expression, selectorPart);
        }

        return expression;
    }
}