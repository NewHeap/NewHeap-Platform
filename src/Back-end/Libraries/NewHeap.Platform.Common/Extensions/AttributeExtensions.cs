using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.Common.Extensions;

public static partial class AttributeExtensions
{
    public static TResult TryGetAttribute<TSource, TResult>(this TSource instance,
        Expression<Func<TSource, object>> selector)
        where TResult : Attribute
    {
        if (selector.NodeType != ExpressionType.Lambda)
        {
            throw new ArgumentException("Selector must be lambda expression", "selector");
        }

        LambdaExpression? lambda = selector;

        var memberExpression = ExtractMemberExpression(lambda.Body);

        if (memberExpression == null)
        {
            throw new ArgumentException("Selector must be member access expression", "selector");
        }

        if (memberExpression.Member.DeclaringType == null)
        {
            throw new InvalidOperationException("Property does not have declaring type");
        }

        var propertyInfo = TryGetPropertyInfo<TSource>(instance, selector);

        if (propertyInfo != null)
        {
            return (TResult)propertyInfo.GetCustomAttributes(typeof(TResult), false).FirstOrDefault();
        }

        return null;
    }

    public static PropertyInfo TryGetPropertyInfo<TSource>(this TSource instance,
        Expression<Func<TSource, object>> selector)
    {
        if (selector.NodeType != ExpressionType.Lambda)
        {
            throw new ArgumentException("Selector must be lambda expression", "selector");
        }

        LambdaExpression lambda = selector;

        var memberExpression = ExtractMemberExpression(lambda.Body);

        if (memberExpression == null)
        {
            throw new ArgumentException("Selector must be member access expression", "selector");
        }

        if (memberExpression.Member.DeclaringType == null)
        {
            throw new InvalidOperationException("Property does not have declaring type");
        }

        return memberExpression.Member.DeclaringType.GetProperty(memberExpression.Member.Name);
    }

    private static MemberExpression ExtractMemberExpression(Expression expression)
    {
        if (expression.NodeType == ExpressionType.MemberAccess)
        {
            return (MemberExpression)expression;
        }

        if (expression.NodeType == ExpressionType.Convert)
        {
            var operand = ((UnaryExpression)expression).Operand;
            return ExtractMemberExpression(operand);
        }

        return null;
    }

    public static string StringGuidelineMaxLength<T>(this T instance, Expression<Func<T, object>> selector)
    {
        var attribute = TryGetAttribute<T, StringLengthAttribute>(instance, selector);
        var propertyInfo = TryGetPropertyInfo(instance, selector);
        var propertyValue = (string)propertyInfo.GetValue(instance);
        var result = propertyValue;

        if (attribute != null && propertyInfo != null && propertyValue != null && result != null)
        {
            if (propertyValue.Length > attribute.MaximumLength && attribute != null)
            {
                result = propertyValue.Substring(0, attribute.MaximumLength);
            }
        }

        return result;
    }
}