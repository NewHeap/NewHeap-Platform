using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Linq.Expressions;

namespace NewHeap.Platform.Common.Extensions;

public static class IModelExtensions
{
    public static string Column<T>(this IModel model, Expression<Func<T, object?>> prop, bool prefixTable = true)
    {
        var entityDefinition = model.FindEntityType(typeof(T));
        
        if (entityDefinition == null)
        {
            throw new InvalidOperationException("Entity type not found in model.");
        }
        
        var memberExpression = prop.Body as MemberExpression 
                               ?? (prop.Body as UnaryExpression)?.Operand as MemberExpression;

        if (memberExpression == null)
        {
            throw new InvalidOperationException($"Expression {prop.Body} does not refer to a property.");
        }
        
        var memberName = memberExpression.Member.Name;
        if (memberName == null)
        {
            throw new InvalidOperationException($"Property {memberExpression.Member.Name} not found in entity type {typeof(T).Name}");
        }
        
        var column = entityDefinition.GetProperty(memberName).GetColumnName();

        if (!prefixTable)
        {
            return $"[{column}]";
        }
        
        var tableName = entityDefinition.GetSchemaQualifiedTableName();
        return $"{tableName}.[{column}]";
    }
    
    public static string Table<T>(this IModel model)
    {
        var entityDefinition = model.FindEntityType(typeof(T));

        if (entityDefinition == null)
        {
            throw new InvalidOperationException("Entity type not found in model.");
        }
        
        return entityDefinition.GetSchemaQualifiedTableName() ?? throw new InvalidOperationException($"Entity type {typeof(T).Name} does not refer to a table.");
    }
}