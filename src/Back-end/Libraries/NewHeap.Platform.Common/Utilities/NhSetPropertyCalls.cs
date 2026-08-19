using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.Common.Utilities;

public sealed class NhSetPropertyCalls<T>
{
    private readonly List<Action<T>> _setters = new();

    public NhSetPropertyCalls<T> SetProperty<TProperty>(
        Expression<Func<T, TProperty>> selector,
        TProperty value)
    {
        if (selector.Body is not MemberExpression member)
        { 
            throw new ArgumentException("Selector must be a property");
        }

        if (member.Member is not PropertyInfo property)
        {
            throw new ArgumentException("Selector must target a property");
        }

        var parameter = Expression.Parameter(typeof(T));
        var valueParameter = Expression.Constant(value, typeof(TProperty));

        var body = Expression.Assign(
            Expression.Property(parameter, property),
            valueParameter);

        var lambda = Expression.Lambda<Action<T>>(body, parameter).Compile();

        _setters.Add(lambda);

        return this;
    }

    public void Apply(T target)
    {
        foreach (var setter in _setters)
        {
            setter(target);
        }
    }
}
