using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Collections;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.AspNet.Common.DAL.Bulk;

internal static class BulkUpsertGraph
{
    private static readonly MethodInfo ExecuteNavigationMethod = typeof(BulkUpsertGraph)
        .GetMethod(nameof(ExecuteNavigationTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static IReadOnlyList<INavigation> GetNavigations<TEntity>(
        DbContext context,
        IReadOnlyCollection<Expression<Func<TEntity, object?>>> navigationSelectors)
        where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException(
                $"Entity type '{typeof(TEntity).Name}' was not found in the EF Core model.");
        ValidatePrimaryKey(entityType, "root");

        var navigations = new List<INavigation>(navigationSelectors.Count);
        foreach (var selector in navigationSelectors)
        {
            ArgumentNullException.ThrowIfNull(selector);
            var member = UnwrapConvert(selector.Body) as MemberExpression;
            if (member is null || UnwrapConvert(member.Expression!) != selector.Parameters[0])
            {
                throw new ArgumentException(
                    "Bulk upsert navigation selectors may only select direct navigations.",
                    nameof(navigationSelectors));
            }

            var navigation = entityType.FindNavigation(member.Member.Name);
            if (navigation is null)
            {
                var reason = entityType.FindSkipNavigation(member.Member.Name) is null
                    ? "is not a mapped navigation"
                    : "is a many-to-many navigation";
                throw new NotSupportedException(
                    $"Bulk upsert navigation '{typeof(TEntity).Name}.{member.Member.Name}' {reason}.");
            }

            if (navigation.IsOnDependent ||
                navigation.ForeignKey.PrincipalEntityType != entityType)
            {
                throw new NotSupportedException(
                    $"Bulk upsert only supports principal-to-dependent navigations; '{typeof(TEntity).Name}.{navigation.Name}' points to a principal.");
            }

            if (!navigation.ForeignKey.PrincipalKey.IsPrimaryKey())
            {
                throw new NotSupportedException(
                    $"Bulk upsert navigation '{typeof(TEntity).Name}.{navigation.Name}' must use the principal primary key.");
            }

            ValidatePrimaryKey(navigation.TargetEntityType, "navigation");
            if (navigation.ForeignKey.Properties.Any(property => property.IsShadowProperty()))
            {
                throw new NotSupportedException(
                    $"Bulk upsert navigation '{typeof(TEntity).Name}.{navigation.Name}' cannot use shadow foreign keys.");
            }

            if (!navigations.Contains(navigation))
            {
                navigations.Add(navigation);
            }
        }

        return navigations;
    }

    internal static async Task<int> ExecuteAsync<TEntity>(
        DbContext context,
        string providerName,
        IReadOnlyList<TEntity> principals,
        IReadOnlyList<INavigation> navigations,
        DbTransaction transaction,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var affected = 0;
        // ponytail: Graph imports intentionally stop at explicitly selected immediate dependents;
        // add typed navigation paths before allowing recursive traversal.
        foreach (var navigation in navigations)
        {
            var dependents = GetDependents(principals, navigation);
            if (dependents.Count == 0)
            {
                continue;
            }

            var task = (Task<int>)ExecuteNavigationMethod
                .MakeGenericMethod(navigation.TargetEntityType.ClrType)
                .Invoke(null, [context, providerName, dependents, transaction, cancellationToken])!;
            affected += await task;
        }

        return affected;
    }

    internal static void ValidateNoNestedDependencies<TEntity>(
        IReadOnlyList<TEntity> principals,
        IReadOnlyList<INavigation> navigations)
        where TEntity : class
    {
        foreach (var navigation in navigations)
        {
            var nestedNavigations = navigation.TargetEntityType.GetNavigations()
                .Where(candidate =>
                    !candidate.IsOnDependent &&
                    candidate.ForeignKey.PrincipalEntityType == navigation.TargetEntityType)
                .Cast<INavigationBase>()
                .Concat(navigation.TargetEntityType.GetSkipNavigations())
                .ToList();
            if (nestedNavigations.Count == 0)
            {
                continue;
            }

            foreach (var principal in principals)
            {
                foreach (var dependent in GetNavigationEntities(principal, navigation))
                {
                    if (dependent is null)
                    {
                        throw new InvalidOperationException(
                            $"Bulk upsert navigation '{navigation.Name}' cannot contain null entities.");
                    }

                    var nested = nestedNavigations.FirstOrDefault(candidate =>
                        GetNavigationEntities(dependent, candidate).Any());
                    if (nested is not null)
                    {
                        throw new NotSupportedException(
                            $"Bulk upsert navigation '{typeof(TEntity).Name}.{navigation.Name}.{nested.Name}' is populated. " +
                            "Nested dependencies are not supported and would otherwise be ignored.");
                    }
                }
            }
        }
    }

    private static IReadOnlyList<object> GetDependents<TEntity>(
        IReadOnlyList<TEntity> principals,
        INavigation navigation)
        where TEntity : class
    {
        var principalProperty = navigation.ForeignKey.PrincipalKey.Properties.Single();
        var foreignKeyProperty = navigation.ForeignKey.Properties.Single();
        var dependents = new List<object>();

        foreach (var principal in principals)
        {
            var principalKey = principalProperty.GetGetter().GetClrValue(principal);
            if (IsDefaultValue(principalKey, principalProperty.ClrType))
            {
                throw new InvalidOperationException(
                    $"Bulk upsert could not resolve primary key '{principalProperty.Name}' before processing navigation '{navigation.Name}'.");
            }

            foreach (var dependent in GetNavigationEntities(principal, navigation))
            {
                AddDependent(dependent, principalKey, foreignKeyProperty, navigation, dependents);
            }
        }

        return dependents;
    }

    private static IEnumerable<object?> GetNavigationEntities(
        object entity,
        INavigationBase navigation)
    {
        var value = navigation.GetGetter().GetClrValue(entity);
        if (!navigation.IsCollection)
        {
            if (value is not null)
            {
                yield return value;
            }

            yield break;
        }

        if (value is not IEnumerable collection)
        {
            yield break;
        }

        foreach (var item in collection)
        {
            yield return item;
        }
    }

    private static void AddDependent(
        object? dependent,
        object? principalKey,
        IProperty foreignKeyProperty,
        INavigation navigation,
        ICollection<object> dependents)
    {
        if (dependent is null)
        {
            throw new InvalidOperationException(
                $"Bulk upsert navigation '{navigation.Name}' cannot contain null entities.");
        }

        SetValue(foreignKeyProperty, dependent, principalKey);
        dependents.Add(dependent);
    }

    private static async Task<int> ExecuteNavigationTypedAsync<TDependent>(
        DbContext context,
        string providerName,
        IReadOnlyList<object> dependentObjects,
        DbTransaction transaction,
        CancellationToken cancellationToken)
        where TDependent : class
    {
        var entityType = context.Model.FindEntityType(typeof(TDependent))!;
        var primaryKeyProperty = entityType.FindPrimaryKey()!.Properties.Single();
        var dependents = dependentObjects.Cast<TDependent>().ToList();

        if (primaryKeyProperty.ValueGenerated == ValueGenerated.Never)
        {
            if (dependents.Any(dependent =>
                    IsDefaultValue(
                        primaryKeyProperty.GetGetter().GetClrValue(dependent),
                        primaryKeyProperty.ClrType)))
            {
                throw new InvalidOperationException(
                    $"New navigation entity '{typeof(TDependent).Name}' must supply a non-default client-generated primary key.");
            }

            return await ExecuteGroupAsync(
                context,
                providerName,
                dependents,
                BulkUpsertOperation.Upsert,
                transaction,
                cancellationToken);
        }

        if (primaryKeyProperty.ValueGenerated != ValueGenerated.OnAdd)
        {
            throw new NotSupportedException(
                $"Bulk upsert navigation key '{typeof(TDependent).Name}.{primaryKeyProperty.Name}' must be client-generated or generated on add.");
        }

        var inserts = new List<TDependent>();
        var updates = new List<TDependent>();
        foreach (var dependent in dependents)
        {
            var key = primaryKeyProperty.GetGetter().GetClrValue(dependent);
            (IsDefaultValue(key, primaryKeyProperty.ClrType) ? inserts : updates).Add(dependent);
        }

        var affected = 0;
        if (inserts.Count > 0)
        {
            affected += await ExecuteGroupAsync(
                context,
                providerName,
                inserts,
                BulkUpsertOperation.InsertOnly,
                transaction,
                cancellationToken);
        }

        if (updates.Count > 0)
        {
            affected += await ExecuteGroupAsync(
                context,
                providerName,
                updates,
                BulkUpsertOperation.UpdateOnly,
                transaction,
                cancellationToken);
        }

        return affected;
    }

    private static Task<int> ExecuteGroupAsync<TDependent>(
        DbContext context,
        string providerName,
        IReadOnlyList<TDependent> dependents,
        BulkUpsertOperation operation,
        DbTransaction transaction,
        CancellationToken cancellationToken)
        where TDependent : class
    {
        var plan = BulkUpsertPlan<TDependent>.CreateForPrimaryKey(context, operation);
        return RepositoryBulkExtensions.ExecuteProviderAsync(
            context,
            providerName,
            plan,
            dependents,
            transaction,
            hydrateMatchedPrimaryKeys: false,
            cancellationToken);
    }

    private static void ValidatePrimaryKey(IEntityType entityType, string role)
    {
        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey?.Properties.Count != 1 ||
            primaryKey.Properties[0].IsShadowProperty() ||
            !BulkUpsertPlan<object>.IsSupportedKeyType(primaryKey.Properties[0].ClrType))
        {
            throw new NotSupportedException(
                $"Bulk upsert {role} entity '{entityType.ClrType.Name}' must have one non-shadow numeric or Guid primary key.");
        }
    }

    private static bool IsDefaultValue(object? value, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return value is null || Equals(value, Activator.CreateInstance(type));
    }

    private static void SetValue(IProperty property, object entity, object? value)
    {
        switch (property.GetMemberInfo(forMaterialization: false, forSet: true))
        {
            case PropertyInfo propertyInfo:
                propertyInfo.SetValue(entity, value);
                break;
            case FieldInfo fieldInfo:
                fieldInfo.SetValue(entity, value);
                break;
            default:
                throw new NotSupportedException(
                    $"Bulk upsert cannot set foreign key '{property.Name}'.");
        }
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
}
