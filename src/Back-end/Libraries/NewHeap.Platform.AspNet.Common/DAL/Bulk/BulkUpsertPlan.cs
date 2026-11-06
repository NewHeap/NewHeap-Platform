using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using NewHeap.Platform.AspNet.Common.Models;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.AspNet.Common.DAL.Bulk;

internal sealed class BulkUpsertPlan<TEntity>
    where TEntity : class
{
    private readonly BulkUpsertPlanShape _shape;

    private BulkUpsertPlan(
        BulkUpsertPlanShape shape,
        IReadOnlyList<BulkUpsertProperty<TEntity>> stagingProperties,
        IReadOnlyList<BulkUpsertProperty<TEntity>> insertProperties,
        IReadOnlyList<BulkUpsertProperty<TEntity>> matchProperties,
        IReadOnlyList<BulkUpsertProperty<TEntity>> updateProperties,
        BulkUpsertProperty<TEntity>? generatedPrimaryKey)
    {
        _shape = shape;
        TableName = shape.TableName;
        Schema = shape.Schema;
        Operation = shape.Operation;
        StagingProperties = stagingProperties;
        InsertProperties = insertProperties;
        MatchProperties = matchProperties;
        UpdateProperties = updateProperties;
        GeneratedPrimaryKey = generatedPrimaryKey;
    }

    public string TableName { get; }

    public string? Schema { get; }

    internal BulkUpsertOperation Operation { get; }

    internal IReadOnlyList<BulkUpsertProperty<TEntity>> StagingProperties { get; }

    public IReadOnlyList<BulkUpsertProperty<TEntity>> InsertProperties { get; }

    public IReadOnlyList<BulkUpsertProperty<TEntity>> MatchProperties { get; }

    public IReadOnlyList<BulkUpsertProperty<TEntity>> UpdateProperties { get; }

    internal BulkUpsertProperty<TEntity>? GeneratedPrimaryKey { get; }

    /// <summary>
    /// Whether the entity has non-key properties that the database generates on add, typically
    /// through a default value. Their columns are written only when the batch sets them, so the
    /// input must be inspected with <see cref="ForBatch"/> before it is staged.
    /// </summary>
    internal bool HasGeneratedOnAddProperties => _shape.GeneratedOnAddMetadata.Count > 0;

    public static BulkUpsertPlan<TEntity> Create<TMatch>(
        DbContext context,
        Expression<Func<TEntity, TMatch>> matchOn)
    {
        var entityType = GetEntityType(context);
        return Create(
            context,
            entityType,
            GetMatchProperties(entityType, matchOn),
            BulkUpsertOperation.Upsert,
            allowStoreGeneratedMatch: false);
    }

    internal static BulkUpsertPlan<TEntity> CreateForPrimaryKey(
        DbContext context,
        BulkUpsertOperation operation)
    {
        var entityType = GetEntityType(context);
        var primaryKey = entityType.FindPrimaryKey()
            ?? throw new NotSupportedException(
                $"Bulk upsert navigation entity '{typeof(TEntity).Name}' must have a primary key.");
        return Create(
            context,
            entityType,
            primaryKey.Properties,
            operation,
            allowStoreGeneratedMatch: true);
    }

    private static IEntityType GetEntityType(DbContext context)
    {
        return context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException(
                $"Entity type '{typeof(TEntity).Name}' was not found in the EF Core model.");
    }

    private static BulkUpsertPlan<TEntity> Create(
        DbContext context,
        IEntityType entityType,
        IReadOnlyList<IProperty> matchMetadata,
        BulkUpsertOperation operation,
        bool allowStoreGeneratedMatch)
    {
        var tableName = entityType.GetTableName()
            ?? throw new NotSupportedException(
                $"Bulk upsert requires '{typeof(TEntity).Name}' to be mapped to a relational table.");
        var schema = entityType.GetSchema();

        var tableMappings = context.Model.GetEntityTypes()
            .Where(candidate =>
                string.Equals(candidate.GetTableName(), tableName, StringComparison.Ordinal) &&
                string.Equals(candidate.GetSchema(), schema, StringComparison.Ordinal))
            .ToList();
        if (tableMappings.Count != 1)
        {
            throw new NotSupportedException(
                $"Bulk upsert does not support inheritance or table sharing for '{typeof(TEntity).Name}'.");
        }

        var storeObject = StoreObjectIdentifier.Table(tableName, schema);
        foreach (var property in matchMetadata)
        {
            if (property.IsNullable)
            {
                throw new InvalidOperationException(
                    $"Bulk upsert match property '{property.Name}' must be non-nullable.");
            }

            if (!allowStoreGeneratedMatch && property.ValueGenerated != ValueGenerated.Never)
            {
                throw new NotSupportedException(
                    $"Bulk upsert cannot match on store-generated property '{property.Name}'.");
            }
        }

        ValidateUniqueMatch(entityType, matchMetadata);

        var mappedProperties = entityType.GetProperties()
            .Where(property => property.GetColumnName(storeObject) is not null)
            .ToList();
        var unsupportedShadowProperty = mappedProperties.FirstOrDefault(property =>
            property.IsShadowProperty() &&
            !property.IsNullable &&
            property.ValueGenerated == ValueGenerated.Never);
        if (unsupportedShadowProperty is not null)
        {
            throw new NotSupportedException(
                $"Bulk upsert cannot supply required shadow property '{unsupportedShadowProperty.Name}'.");
        }

        var writableMetadata = mappedProperties
            .Where(property =>
                !property.IsShadowProperty() &&
                property.ValueGenerated == ValueGenerated.Never &&
                property.GetBeforeSaveBehavior() == PropertySaveBehavior.Save)
            .ToList();
        if (writableMetadata.Count == 0)
        {
            throw new NotSupportedException(
                $"Entity type '{typeof(TEntity).Name}' has no writable columns for bulk upsert.");
        }

        // Non-key properties generated on add, by convention every property with a database
        // default, are written only when the batch sets them; see ForBatch. Properties generated
        // on update or computed (OnUpdate, OnAddOrUpdate) are never written, as with SaveChanges.
        var primaryKey = entityType.FindPrimaryKey();
        var generatedOnAddMetadata = mappedProperties
            .Where(property =>
                !property.IsShadowProperty() &&
                property.ValueGenerated == ValueGenerated.OnAdd &&
                !(primaryKey?.Properties.Contains(property) ?? false) &&
                !matchMetadata.Contains(property) &&
                property.GetBeforeSaveBehavior() == PropertySaveBehavior.Save)
            .ToList();

        var shape = new BulkUpsertPlanShape(
            tableName,
            schema,
            operation,
            storeObject,
            primaryKey,
            matchMetadata,
            mappedProperties,
            writableMetadata,
            generatedOnAddMetadata);
        return Build(shape, suppliedGeneratedOnAdd: []);
    }

    /// <summary>
    /// Returns the plan for one batch. A non-key property generated on add is written when every
    /// row sets it, and left to the database when no row does, so a value the caller set is never
    /// dropped silently. A batch in which only some rows set it is refused before any SQL runs,
    /// because one statement cannot apply the database default to some rows only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The batch contains a null entity, or only some rows set a property generated on add.</exception>
    internal BulkUpsertPlan<TEntity> ForBatch(IReadOnlyList<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        if (_shape.GeneratedOnAddMetadata.Count == 0)
        {
            return this;
        }

        var supplied = new List<IProperty>();
        foreach (var property in _shape.GeneratedOnAddMetadata)
        {
            var getter = property.GetGetter();
            var setCount = 0;
            foreach (var entity in entities)
            {
                if (entity is null)
                {
                    throw new InvalidOperationException("Bulk upsert input cannot contain null entities.");
                }

                if (!getter.HasSentinelValue(entity))
                {
                    setCount++;
                }
            }

            if (setCount == 0)
            {
                continue;
            }

            if (setCount != entities.Count)
            {
                throw new InvalidOperationException(
                    $"Bulk upsert property '{typeof(TEntity).Name}.{property.Name}' is generated on add " +
                    $"(it has a database default) and is set on {setCount} of {entities.Count} rows. " +
                    "Set it on every row to write the values, or on none to let the database default apply; " +
                    "import rows with and without a value in separate calls.");
            }

            supplied.Add(property);
        }

        return supplied.Count == 0
            ? this
            : Build(_shape, supplied);
    }

    private static BulkUpsertPlan<TEntity> Build(
        BulkUpsertPlanShape shape,
        IReadOnlyCollection<IProperty> suppliedGeneratedOnAdd)
    {
        // Keep the model's column order so supplied generated columns sit where they are mapped.
        var insertMetadata = shape.MappedProperties
            .Where(property =>
                shape.WritableMetadata.Contains(property) ||
                suppliedGeneratedOnAdd.Contains(property))
            .ToList();

        var creationDateTimeName = typeof(IdDbEntity).IsAssignableFrom(typeof(TEntity))
            ? nameof(IdDbEntity.CreationDateTime)
            : null;
        var updateMetadata = insertMetadata
            .Where(property =>
                !shape.MatchMetadata.Contains(property) &&
                !(shape.PrimaryKey?.Properties.Contains(property) ?? false) &&
                !property.IsConcurrencyToken &&
                property.GetAfterSaveBehavior() == PropertySaveBehavior.Save &&
                !string.Equals(property.Name, creationDateTimeName, StringComparison.Ordinal))
            .ToList();
        if (shape.Operation == BulkUpsertOperation.UpdateOnly && updateMetadata.Count == 0)
        {
            throw new NotSupportedException(
                $"Bulk upsert navigation entity '{typeof(TEntity).Name}' has no writable columns to update.");
        }

        var stagingMetadata = insertMetadata
            .Concat(shape.MatchMetadata)
            .Distinct()
            .ToList();
        var propertyMap = stagingMetadata.ToDictionary(
            property => property,
            property => CreateProperty(property, shape.StoreObject));
        var generatedPrimaryKeyMetadata = shape.PrimaryKey?.Properties.Count == 1
            ? shape.PrimaryKey.Properties[0]
            : null;
        var generatedPrimaryKey = generatedPrimaryKeyMetadata is not null &&
                                  generatedPrimaryKeyMetadata.ValueGenerated == ValueGenerated.OnAdd &&
                                  !generatedPrimaryKeyMetadata.IsShadowProperty() &&
                                  IsSupportedKeyType(generatedPrimaryKeyMetadata.ClrType)
            ? CreateProperty(generatedPrimaryKeyMetadata, shape.StoreObject)
            : null;

        return new BulkUpsertPlan<TEntity>(
            shape,
            stagingMetadata.Select(property => propertyMap[property]).ToList(),
            insertMetadata.Select(property => propertyMap[property]).ToList(),
            shape.MatchMetadata.Select(property => propertyMap[property]).ToList(),
            updateMetadata.Select(property => propertyMap[property]).ToList(),
            generatedPrimaryKey);
    }

    private static BulkUpsertProperty<TEntity> CreateProperty(
        IProperty property,
        StoreObjectIdentifier storeObject)
    {
        return new BulkUpsertProperty<TEntity>(
            property,
            property.GetColumnName(storeObject)!,
            property.GetRelationalTypeMapping());
    }

    internal static bool IsSupportedKeyType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(Guid) || Type.GetTypeCode(type) is
            TypeCode.Byte or
            TypeCode.SByte or
            TypeCode.Int16 or
            TypeCode.UInt16 or
            TypeCode.Int32 or
            TypeCode.UInt32 or
            TypeCode.Int64 or
            TypeCode.UInt64 or
            TypeCode.Decimal;
    }

    private static IReadOnlyList<IProperty> GetMatchProperties<TMatch>(
        IEntityType entityType,
        Expression<Func<TEntity, TMatch>> matchOn)
    {
        var body = UnwrapConvert(matchOn.Body);
        var expressions = body switch
        {
            MemberExpression member => [member],
            NewExpression composite => composite.Arguments,
            _ => throw new ArgumentException(
                "The bulk upsert match selector must select a mapped property or an anonymous object of mapped properties.",
                nameof(matchOn))
        };

        var properties = new List<IProperty>(expressions.Count);
        foreach (var expression in expressions)
        {
            var member = UnwrapConvert(expression) as MemberExpression;
            if (member is null || UnwrapConvert(member.Expression!) != matchOn.Parameters[0])
            {
                throw new ArgumentException(
                    "The bulk upsert match selector may only contain direct entity properties.",
                    nameof(matchOn));
            }

            var property = entityType.FindProperty(member.Member.Name)
                ?? throw new ArgumentException(
                    $"Match property '{member.Member.Name}' is not a mapped scalar property.",
                    nameof(matchOn));
            if (!properties.Contains(property))
            {
                properties.Add(property);
            }
        }

        if (properties.Count == 0)
        {
            throw new ArgumentException("At least one match property is required.", nameof(matchOn));
        }

        return properties;
    }

    private static Expression UnwrapConvert(Expression expression)
    {
        while (expression is UnaryExpression
               {
                   NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
               } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static void ValidateUniqueMatch(
        IEntityType entityType,
        IReadOnlyCollection<IProperty> matchProperties)
    {
        static bool HasSameProperties(
            IReadOnlyList<IProperty> candidate,
            IReadOnlyCollection<IProperty> expected)
        {
            return candidate.Count == expected.Count && candidate.All(expected.Contains);
        }

        var hasUniqueKey = entityType.GetKeys()
            .Any(key => HasSameProperties(key.Properties, matchProperties));
        var hasUniqueIndex = entityType.GetIndexes()
            .Any(index =>
                index.IsUnique &&
                index.GetFilter() is null &&
                HasSameProperties(index.Properties, matchProperties));
        if (!hasUniqueKey && !hasUniqueIndex)
        {
            throw new InvalidOperationException(
                $"Bulk upsert match properties for '{typeof(TEntity).Name}' must exactly match a non-filtered unique key or index.");
        }
    }
}

/// <summary>
/// The model facts a bulk upsert plan is built from; one batch only decides which properties
/// generated on add it writes.
/// </summary>
internal sealed record BulkUpsertPlanShape(
    string TableName,
    string? Schema,
    BulkUpsertOperation Operation,
    StoreObjectIdentifier StoreObject,
    IKey? PrimaryKey,
    IReadOnlyList<IProperty> MatchMetadata,
    IReadOnlyList<IProperty> MappedProperties,
    IReadOnlyList<IProperty> WritableMetadata,
    IReadOnlyList<IProperty> GeneratedOnAddMetadata);

internal sealed class BulkUpsertProperty<TEntity>(
    IProperty property,
    string columnName,
    RelationalTypeMapping typeMapping)
    where TEntity : class
{
    private readonly MemberInfo _setterMember = property.GetMemberInfo(
        forMaterialization: false,
        forSet: true);

    public string ColumnName { get; } = columnName;

    public string StoreTypeName { get; } = typeMapping.StoreTypeNameBase;

    internal Type ModelClrType { get; } = property.ClrType;

    internal string? DefaultValueSql { get; } = property.GetDefaultValueSql();

    public Type ProviderClrType { get; } = Nullable.GetUnderlyingType(
        typeMapping.Converter?.ProviderClrType ?? property.ClrType)
        ?? typeMapping.Converter?.ProviderClrType
        ?? property.ClrType;

    public object? GetProviderValue(TEntity entity)
    {
        var value = property.GetGetter().GetClrValue(entity);
        return value is null || typeMapping.Converter is null
            ? value
            : typeMapping.Converter.ConvertToProvider(value);
    }

    internal void SetProviderValue(TEntity entity, object value)
    {
        var modelValue = typeMapping.Converter is null
            ? value
            : typeMapping.Converter.ConvertFromProvider(value);
        switch (_setterMember)
        {
            case PropertyInfo propertyInfo:
                propertyInfo.SetValue(entity, modelValue);
                break;
            case FieldInfo fieldInfo:
                fieldInfo.SetValue(entity, modelValue);
                break;
            default:
                throw new NotSupportedException(
                    $"Bulk upsert cannot set generated property '{property.Name}'.");
        }
    }
}

internal enum BulkUpsertOperation
{
    Upsert,
    InsertOnly,
    UpdateOnly
}
