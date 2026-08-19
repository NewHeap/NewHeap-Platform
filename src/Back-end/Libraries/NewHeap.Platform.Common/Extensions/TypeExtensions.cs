
using System.Collections;
using System.Reflection;

namespace NewHeap.Platform.Common;

public static partial class TypeExtensions
{
    public static bool CanBeInstantiated(this Type type) =>
        type.GetConstructor(Type.EmptyTypes) != null && !type.IsAbstract && !type.IsGenericType;

    /// <summary>
    /// Gives the default value of a type, instantiates a value for value types (int, float, struct)
    /// Returns null for reference types.
    /// This may fault on structs that can't be instantiated by Activator (if the object does not have a ctor without arguments)
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public static object? GetDefaultValueOfType(this Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : default;
    }

    /// <inheritdoc cref="IsGenericTypeOfCore"/>
    public static bool IsGenericTypeOf(this Type genericType, Type someType)
    {
        return IsGenericTypeOfCore(genericType, someType);
    }

    /// <summary>
    /// Checks if type is a generic type regardless of the type arguments.
    /// Taken from: <see href="https://stackoverflow.com/a/1855248/4122889"/>
    /// </summary>
    /// <param name="genericType"></param>
    /// <param name="someType"></param>
    /// <returns></returns>
    private static bool IsGenericTypeOfCore(Type genericType, Type someType)
    {
        if (someType.IsGenericType
            && genericType == someType.GetGenericTypeDefinition()) return true;

        return someType.BaseType != null
               && IsGenericTypeOf(genericType, someType.BaseType);
    }

    public static bool IsGenericInterfaceImplemented(this Type type, Type other)
    {
        var interfaces = type
            .GetInterfaces();

        return interfaces.Any(c => c.IsGenericType && c.GetGenericTypeDefinition() == other.GetGenericTypeDefinition());
    }

    /// <summary>
    /// Determine whether a type is simple (String, Decimal, DateTime, etc) 
    /// or complex (i.e. custom class with public properties and methods).
    /// </summary>
    /// <see href="https://gist.github.com/jonathanconway/3330614"/>
    /// <see href="http://stackoverflow.com/questions/2442534/how-to-test-if-type-is-primitive"/>
    public static bool IsSimpleType(
        this Type type)
    {
        return type.IsValueType
               || type.IsPrimitive
               || new[]
                   {
                       typeof(string),
                       typeof(decimal),
                       typeof(DateTime),
                       typeof(DateTimeOffset),
                       typeof(DateOnly),
                       typeof(TimeSpan),
                       typeof(Guid)
                   }
                   .ToList()
                   .Contains(type)
               || Convert.GetTypeCode(type) != TypeCode.Object;
    }

    public static IEnumerable<(PropertyInfo prop, List<PropertyInfo> parents)> TraversePropertiesInOrder(this Type type, BindingFlags bindingAttr, List<PropertyInfo>? parents = null, int maxDepth = 3)
    {
        parents ??= new();

        if (parents.Count >= 3)
            yield break;

        foreach (var property in type.GetProperties(bindingAttr))
        {
            if (property.PropertyType.IsSimpleType() || Nullable.GetUnderlyingType(property.PropertyType) != null)
                yield return (property, parents);
            else if (property.PropertyType.IsAssignableTo(typeof(IEnumerable)))
                yield return (property, parents);
            else
            {
                foreach (var p in TraversePropertiesInOrder(property.PropertyType, bindingAttr, parents.Concat(new[] { property }).ToList()))
                {
                    yield return p;
                }
            }
        }
    }

    public static IEnumerable<Type> GetBaseClasses(this Type type)
    {
        var currentType = type;
        while (currentType is { BaseType: not null })
        {
            currentType = currentType.BaseType;
            yield return currentType;
        }
    }
}