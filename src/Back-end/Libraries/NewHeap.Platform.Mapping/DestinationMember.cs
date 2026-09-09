using System.Reflection;

namespace NewHeap.Platform.Mapping;

internal sealed class DestinationMember(MemberInfo member)
{
    public string Name => member.Name;

    public Type ValueType => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo fieldInfo => fieldInfo.FieldType,
        _ => throw new InvalidOperationException("Unsupported destination member.")
    };

    public bool CanWrite => member switch
    {
        PropertyInfo property => property.SetMethod is not null,
        FieldInfo fieldInfo => !fieldInfo.IsInitOnly && !fieldInfo.IsLiteral,
        _ => false
    };

    public object? GetValue(object destination)
    {
        return member switch
        {
            PropertyInfo property => property.GetValue(destination),
            FieldInfo fieldInfo => fieldInfo.GetValue(destination),
            _ => throw new InvalidOperationException("Unsupported destination member.")
        };
    }

    public void SetValue(object destination, object? value)
    {
        switch (member)
        {
            case PropertyInfo property:
                property.SetValue(destination, value);
                break;
            case FieldInfo fieldInfo:
                fieldInfo.SetValue(destination, value);
                break;
            default:
                throw new InvalidOperationException("Unsupported destination member.");
        }
    }
}
