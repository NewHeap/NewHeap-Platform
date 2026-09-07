using System.Reflection;

namespace NewHeap.Platform.Mapping;

internal static class ConventionSource
{
    internal static bool TryResolve(
        Type sourceType,
        string destinationName,
        out MappingMemberResolver? resolver,
        out Type? valueType)
    {
        var members = new List<MemberInfo>();
        if (!TryFindPath(sourceType, destinationName, members))
        {
            resolver = null;
            valueType = null;
            return false;
        }

        var resolvedType = GetMemberType(members[^1]);
        valueType = resolvedType;
        resolver = (source, _, _, _) =>
        {
            object? value = source;
            foreach (var member in members)
            {
                if (value is null)
                {
                    return resolvedType.IsValueType ? Activator.CreateInstance(resolvedType) : null;
                }

                value = member switch
                {
                    PropertyInfo property => property.GetValue(value),
                    FieldInfo field => field.GetValue(value),
                    MethodInfo method => method.Invoke(value, null),
                    _ => throw new InvalidOperationException("Unsupported convention source member.")
                };
            }

            return value;
        };
        return true;
    }

    private static bool TryFindPath(Type sourceType, string name, List<MemberInfo> path)
    {
        var directMember = FindMember(sourceType, name);
        if (directMember is not null)
        {
            path.Add(directMember);
            return true;
        }

        for (var index = 1; index < name.Length; index++)
        {
            if (!char.IsUpper(name[index]))
            {
                continue;
            }

            var prefix = FindMember(sourceType, name[..index]);
            if (prefix is null)
            {
                continue;
            }

            path.Add(prefix);
            if (TryFindPath(GetMemberType(prefix), name[index..], path))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        var declaringTypes = type.IsInterface ? new[] { type }.Concat(type.GetInterfaces()) : [type];
        var members = declaringTypes.SelectMany(declaringType =>
            declaringType.GetMembers(BindingFlags.Instance | BindingFlags.Public)).ToArray();
        var dataMember = members.FirstOrDefault(member =>
            string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (member is PropertyInfo { GetMethod.IsPublic: true } property && property.GetIndexParameters().Length == 0 ||
             member is FieldInfo));
        if (dataMember is not null)
        {
            return dataMember;
        }

        var methods = members.OfType<MethodInfo>().Where(method =>
            method.DeclaringType != typeof(object) && !method.IsSpecialName &&
            !method.ContainsGenericParameters && method.ReturnType != typeof(void) &&
            method.GetParameters().Length == 0).ToArray();
        return methods.FirstOrDefault(method => string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? methods.FirstOrDefault(method => string.Equals(method.Name, "Get" + name, StringComparison.OrdinalIgnoreCase));
    }

    private static Type GetMemberType(MemberInfo member)
    {
        return member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            MethodInfo method => method.ReturnType,
            _ => throw new InvalidOperationException("Unsupported convention source member.")
        };
    }
}
