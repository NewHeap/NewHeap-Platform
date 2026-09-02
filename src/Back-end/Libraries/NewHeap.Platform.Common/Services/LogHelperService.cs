using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.Common.Translations;
using Newtonsoft.Json.Linq;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.Common.Services;

public static partial class TypeExtensions
{
    public static bool IsPrimitive(this Type type)
    {
        if (type == typeof(string))
        {
            return true;
        }

        return type.IsValueType & type.IsPrimitive;
    }
}

public static partial class JObjectExtensions
{
    public static bool TryGetTokenValue<T>(this JObject obj, string name, out T? value)
    {
        var tokens = obj.SelectTokens($"$..{name}");
        if (!tokens.Any())
        {
            value = default;
            return false;
        }

        value = tokens
            .Select(t => t.Value<T>())
            .FirstOrDefault();
        return true;
    }
}

public partial class LogHelperService
{
    private readonly IStringLocalizer<SharedDataAnnotationRecources> _localizer;
    private readonly ILogger<LogHelperService> _logger;

    public LogHelperService(
        IStringLocalizer<SharedDataAnnotationRecources> localizer,
        ILogger<LogHelperService> logger)
    {
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>
    ///     Diff between two objects and see which properties have changed
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="original">Original object</param>
    /// <param name="updated">Updated object</param>
    /// <param name="valueResolver"></param>
    /// <param name="selectors">Properties to check</param>
    /// <returns></returns>
    public Task<IEnumerable<ChangedValue>> ChangedProperties<T>(
        T original,
        T updated,
        Dictionary<Expression<Func<T?, object?>>, Func<object?, Task<string?>>> valueResolver,
        params Expression<Func<T?, object?>>[] selectors)
    {
        return ChangedProperties(original, updated, null, valueResolver, selectors);
    }

    public async Task<IEnumerable<ChangedValue>> ChangedProperties<T>(
       T original,
       T updated,
       Dictionary<Expression<Func<T?, object?>>, Func<object?, Task<string?>>>? compareValueResolver,
       Dictionary<Expression<Func<T?, object?>>, Func<object?, Task<string?>>>? valueResolver,
       params Expression<Func<T?, object?>>[] selectors)
    {
        var changedValues = new List<ChangedValue>();

        foreach (var expression in selectors)
        {
            var memberExpression = ExtractMemberExpression(expression);

            if (memberExpression != null)
            {
                var property = (PropertyInfo)memberExpression.Member;

                var memberOriginal = GetMemberObject(memberExpression, original);
                var memberUpdated = GetMemberObject(memberExpression, updated);

                var val1 = memberOriginal != null ? property.GetValue(memberOriginal) : null;
                var val2 = memberUpdated != null ? property.GetValue(GetMemberObject(memberExpression, updated)) : null;

                var compareResolve = compareValueResolver?.FirstOrDefault(x =>
                        ExpressionEqualityComparer.Instance.Equals(expression, x.Key));

                if (compareResolve.HasValue && compareResolve.Value.Value != null)
                {
                    try
                    {
                        var func = compareResolve.Value.Value;
                        val1 = await func(val1);
                        val2 = await func(val2);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogDebug(
                            exception,
                            "Could not resolve the comparison value for property {PropertyName}.",
                            property.Name);
                    }
                }

                if ((val1 != null && !val1.Equals(val2)) || (val1 == null && val2 != null))
                {
                    var keyName = property
                                      ?.GetCustomAttributes(typeof(DisplayAttribute), true)
                                      ?.Cast<DisplayAttribute>()
                                      .SingleOrDefault()?.Name
                                  ?? property!.Name;

                    if (_localizer != null)
                    {
                        keyName = _localizer[keyName];
                    }

                    var originalValue = val1?.ToString();
                    var updatedValue = val2?.ToString();
                    var resolve = valueResolver?.FirstOrDefault(x =>
                        ExpressionEqualityComparer.Instance.Equals(expression, x.Key));

                    if (resolve.HasValue && resolve.Value.Value != null)
                    {
                        try
                        {
                            var func = resolve.Value.Value;
                            originalValue = await func(val1);
                            updatedValue = await func(val2);
                        }
                        catch (Exception exception)
                        {
                            _logger.LogDebug(
                                exception,
                                "Could not resolve the display value for property {PropertyName}.",
                                property.Name);
                        }
                    }

                    changedValues.Add(new ChangedValue
                    {
                        Key = keyName,
                        OriginalValue = originalValue,
                        UpdateValue = updatedValue
                    });
                }
            }
        }

        return changedValues;
    }


    private static object? GetMemberObject(MemberExpression expression, object? obj)
    {
        if (obj == null || expression.Expression!.NodeType == ExpressionType.Parameter)
        {
            return obj;
        }

        if (expression.Expression.NodeType == ExpressionType.MemberAccess)
        {
            var prop = (PropertyInfo)((MemberExpression)expression.Expression).Member;
            obj = prop.GetValue(obj);
            return GetMemberObject((MemberExpression)expression.Expression, obj);
        }

        return null;
    }

    private static MemberExpression? ExtractMemberExpression(Expression expression)
    {
        if (expression.NodeType == ExpressionType.MemberAccess)
        {
            return (MemberExpression)expression;
        }

        if (expression.NodeType == ExpressionType.Lambda)
        {
            return ExtractMemberExpression(((LambdaExpression)expression).Body);
        }

        if (expression.NodeType == ExpressionType.Convert)
        {
            var operand = ((UnaryExpression)expression).Operand;
            return ExtractMemberExpression(operand);
        }

        return null;
    }

    #region Copy

    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public static object? Copy(object originalObject)
    {
        return InternalCopy(originalObject, new Dictionary<object, object>(new ReferenceEqualityComparer()));
    }

    private static object? InternalCopy(object? originalObject, IDictionary<object, object> visited)
    {
        if (originalObject == null)
        {
            return null;
        }

        var typeToReflect = originalObject.GetType();
        if (typeToReflect.IsPrimitive())
        {
            return originalObject;
        }

        if (visited.ContainsKey(originalObject))
        {
            return visited[originalObject];
        }

        if (typeof(Delegate).IsAssignableFrom(typeToReflect))
        {
            return null;
        }

        var cloneObject = CloneMethod.Invoke(originalObject, null);
        if (typeToReflect.IsArray)
        {
            var arrayType = typeToReflect.GetElementType()!;
            if (arrayType.IsPrimitive() == false)
            {
                var clonedArray = (Array)cloneObject!;
                clonedArray.ForEach((array, indices) =>
                    array.SetValue(InternalCopy(clonedArray.GetValue(indices), visited), indices));
            }
        }

        visited.Add(originalObject, cloneObject!);
        CopyFields(originalObject, visited, cloneObject!, typeToReflect);
        RecursiveCopyBaseTypePrivateFields(originalObject, visited, cloneObject!, typeToReflect);
        return cloneObject;
    }

    private static void RecursiveCopyBaseTypePrivateFields(object originalObject, IDictionary<object, object> visited,
        object cloneObject, Type typeToReflect)
    {
        if (typeToReflect.BaseType != null)
        {
            RecursiveCopyBaseTypePrivateFields(originalObject, visited, cloneObject, typeToReflect.BaseType);
            CopyFields(originalObject, visited, cloneObject, typeToReflect.BaseType,
                BindingFlags.Instance | BindingFlags.NonPublic, info => info.IsPrivate);
        }
    }

    private static void CopyFields(object originalObject, IDictionary<object, object> visited, object cloneObject,
        Type typeToReflect,
        BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                                    BindingFlags.FlattenHierarchy, Func<FieldInfo, bool>? filter = null)
    {
        foreach (var fieldInfo in typeToReflect.GetFields(bindingFlags))
        {
            if (filter != null && filter(fieldInfo) == false)
            {
                continue;
            }

            if (fieldInfo.FieldType.IsPrimitive())
            {
                continue;
            }

            var originalFieldValue = fieldInfo.GetValue(originalObject);
            var clonedFieldValue = InternalCopy(originalFieldValue, visited);
            fieldInfo.SetValue(cloneObject, clonedFieldValue);
        }
    }

    public static T? Copy<T>(T original)
    {
        return (T?)Copy((object)original!);
    }

    #endregion

    public static class ValueResolvers
    {
        public static Func<object, Task<string>> EnumerableValueResolver<TEnumerable, T>(Func<T, string> keySelector)
            where TEnumerable : IEnumerable<T>, new()
        {
            //var changedProperties2 = await _logHelper.ChangedProperties(originalData, updatedData, new Dictionary<Expression<Func<MyEntity, object>>, Func<object, Task<string>>>
            //    { 
            //        // Method resolvers
            //        { x => x.MyStringList, LogHelper.ValueResolvers.EnumerableValueResolver<List<string>, string>(k => k) },
            //        { x => x.MyObjectList, LogHelper.ValueResolvers.EnumerableValueResolver<List<MyObject>, MyObject>(k => $"{k.Name} - {k.Discipline}") },
            //    },
            //    x => x.MyStringList,
            //    x => x.MyObjectList
            //);

            Func<object, Task<string>> listStringFunc = (x) =>
            {
                var mutateValue = new TEnumerable();
                if (x != null)
                {
                    mutateValue = (TEnumerable)x;
                }

                var stringList = mutateValue.Select(keySelector).ToList();

                stringList = [.. stringList.OrderBy(x => x)];

                return System.Threading.Tasks.Task.FromResult(string.Join(", ", stringList));
            };

            return listStringFunc;
        }
    }
}

public partial struct ChangedValue
{
    public string Key { get; set; }
    public object? OriginalValue { get; set; }

    public object? UpdateValue { get; set; }
}

internal partial class ReferenceEqualityComparer : EqualityComparer<object>
{
    public override bool Equals(object? x, object? y)
    {
        return ReferenceEquals(x, y);
    }

    public override int GetHashCode(object? obj)
    {
        if (obj == null)
        {
            return 0;
        }

        return obj.GetHashCode();
    }
}

internal static partial class ArrayExtensions
{
    public static void ForEach(this Array array, Action<Array, int[]> action)
    {
        if (array.LongLength == 0)
        {
            return;
        }

        var walker = new ArrayTraverse(array);
        do
        {
            action(array, walker.Position);
        } while (walker.Step());
    }
}

internal partial class ArrayTraverse
{
    private readonly int[] maxLengths;
    public int[] Position;

    public ArrayTraverse(Array array)
    {
        maxLengths = new int[array.Rank];
        for (var i = 0; i < array.Rank; ++i)
        {
            maxLengths[i] = array.GetLength(i) - 1;
        }

        Position = new int[array.Rank];
    }

    public bool Step()
    {
        for (var i = 0; i < Position.Length; ++i)
        {
            if (Position[i] < maxLengths[i])
            {
                Position[i]++;
                for (var j = 0; j < i; j++)
                {
                    Position[j] = 0;
                }

                return true;
            }
        }

        return false;
    }
}
