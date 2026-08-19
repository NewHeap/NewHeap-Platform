using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.Common.Models;

public static class NhProjection
{
    public static NhProjectionSourceBuilder<TEntity> For<TEntity>()
        where TEntity : class
    {
        return new NhProjectionSourceBuilder<TEntity>();
    }

    public static NhProjectionBuilder<TEntity, TViewModel> For<TEntity, TViewModel>()
        where TEntity : class
        where TViewModel : class, new()
    {
        return new NhProjectionBuilder<TEntity, TViewModel>();
    }
}

public sealed class NhProjectionSourceBuilder<TEntity>
    where TEntity : class
{
    public NhProjectionDefinition<TEntity, TProjection> Select<TProjection>(
        Expression<Func<TEntity, TProjection>> selector)
        where TProjection : class
    {
        return new NhProjectionDefinition<TEntity, TProjection>(selector);
    }
}

public sealed class NhProjectionDefinition<TEntity, TProjection>
    where TEntity : class
    where TProjection : class
{
    private readonly List<Expression<Func<TProjection, object?>>> _searchableSelectors;
    private readonly List<Expression<Func<TProjection, object?>>> _filterableSelectors;
    private readonly List<Expression<Func<TProjection, object?>>> _orderableSelectors;

    internal NhProjectionDefinition(
        Expression<Func<TEntity, TProjection>> selector,
        IEnumerable<Expression<Func<TProjection, object?>>>? searchableSelectors = null,
        IEnumerable<Expression<Func<TProjection, object?>>>? filterableSelectors = null,
        IEnumerable<Expression<Func<TProjection, object?>>>? orderableSelectors = null)
    {
        ArgumentNullException.ThrowIfNull(selector);

        Selector = selector;
        _searchableSelectors = searchableSelectors?.ToList() ?? [];
        _filterableSelectors = filterableSelectors?.ToList() ?? [];
        _orderableSelectors = orderableSelectors?.ToList() ?? [];
    }

    public Expression<Func<TEntity, TProjection>> Selector { get; }

    public NhProjectionDefinition<TEntity, TProjection> IsSearchable(
        params Expression<Func<TProjection, object?>>[] selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        foreach (var selector in selectors)
        {
            ArgumentNullException.ThrowIfNull(selector);
            _searchableSelectors.Add(selector);
        }

        return this;
    }

    public NhProjectionDefinition<TEntity, TProjection> IsFilterable(
        params Expression<Func<TProjection, object?>>[] selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        foreach (var selector in selectors)
        {
            ArgumentNullException.ThrowIfNull(selector);
            CollectionProcessingExpressionResolver.GetFilterKey(selector);
            _filterableSelectors.Add(selector);
        }

        return this;
    }

    public NhProjectionDefinition<TEntity, TProjection> IsOrderable(
        params Expression<Func<TProjection, object?>>[] selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        foreach (var selector in selectors)
        {
            ArgumentNullException.ThrowIfNull(selector);
            CollectionProcessingExpressionResolver.GetOrderKey(selector);
            _orderableSelectors.Add(selector);
        }

        return this;
    }

    public Func<TEntity, TProjection> Compile()
    {
        return Selector.Compile();
    }

    internal void ApplyTo(
        CollectionProcessingOptionsBuilder<TProjection, TProjection> options)
    {
        if (_searchableSelectors.Count > 0)
        {
            options
                .SearchableFromAttributes()
                .WithSearchable(_searchableSelectors.ToArray());
        }

        if (_filterableSelectors.Count > 0)
        {
            options
                .FilterableFromAttributes()
                .WithFilterable(_filterableSelectors.ToArray());
        }

        if (_orderableSelectors.Count > 0)
        {
            options
                .OrderableFromAttributes()
                .WithOrderable(_orderableSelectors.ToArray());
        }
    }

    public static implicit operator Expression<Func<TEntity, TProjection>>(
        NhProjectionDefinition<TEntity, TProjection> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return projection.Selector;
    }
}

public sealed class NhProjectionBuilder<TEntity, TViewModel>
    where TEntity : class
    where TViewModel : class, new()
{
    private static readonly IReadOnlyDictionary<string, PropertyInfo> SourceProperties =
        GetReadableProperties(typeof(TEntity));

    private static readonly IReadOnlyDictionary<string, PropertyInfo> DestinationProperties =
        GetWritableProperties(typeof(TViewModel));

    private readonly Dictionary<string, LambdaExpression> _explicitMappings =
        new(StringComparer.Ordinal);

    private readonly HashSet<string> _ignoredProperties =
        new(StringComparer.Ordinal);

    private readonly List<Expression<Func<TViewModel, object?>>> _searchableSelectors = [];
    private readonly List<Expression<Func<TViewModel, object?>>> _filterableSelectors = [];
    private readonly List<Expression<Func<TViewModel, object?>>> _orderableSelectors = [];

    public NhProjectionBuilder<TEntity, TViewModel> Map<TDestinationMember, TSourceMember>(
        Expression<Func<TViewModel, TDestinationMember>> destination,
        Expression<Func<TEntity, TSourceMember>> source)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        var destinationProperty = GetDestinationProperty(destination);
        EnsureAssignable(source.Body.Type, destinationProperty.PropertyType, destinationProperty.Name);

        _explicitMappings[destinationProperty.Name] = source;
        _ignoredProperties.Remove(destinationProperty.Name);

        return this;
    }

    public NhProjectionBuilder<TEntity, TViewModel> Ignore<TDestinationMember>(
        Expression<Func<TViewModel, TDestinationMember>> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var destinationProperty = GetDestinationProperty(destination);

        _explicitMappings.Remove(destinationProperty.Name);
        _ignoredProperties.Add(destinationProperty.Name);

        return this;
    }

    public NhProjectionBuilder<TEntity, TViewModel> IsSearchable(
        params Expression<Func<TViewModel, object?>>[] selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        foreach (var selector in selectors)
        {
            ArgumentNullException.ThrowIfNull(selector);
            _searchableSelectors.Add(selector);
        }

        return this;
    }

    public NhProjectionBuilder<TEntity, TViewModel> IsFilterable(
        params Expression<Func<TViewModel, object?>>[] selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        foreach (var selector in selectors)
        {
            ArgumentNullException.ThrowIfNull(selector);
            CollectionProcessingExpressionResolver.GetFilterKey(selector);
            _filterableSelectors.Add(selector);
        }

        return this;
    }

    public NhProjectionBuilder<TEntity, TViewModel> IsOrderable(
        params Expression<Func<TViewModel, object?>>[] selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        foreach (var selector in selectors)
        {
            ArgumentNullException.ThrowIfNull(selector);
            CollectionProcessingExpressionResolver.GetOrderKey(selector);
            _orderableSelectors.Add(selector);
        }

        return this;
    }

    public NhProjectionDefinition<TEntity, TViewModel> Build()
    {
        return new NhProjectionDefinition<TEntity, TViewModel>(
            BuildSelector(),
            _searchableSelectors,
            _filterableSelectors,
            _orderableSelectors);
    }

    public Expression<Func<TEntity, TViewModel>> BuildSelector()
    {
        var parameter = Expression.Parameter(typeof(TEntity), "source");
        var bindings = new List<MemberBinding>();

        foreach (var destinationProperty in DestinationProperties.Values)
        {
            if (_ignoredProperties.Contains(destinationProperty.Name))
            {
                continue;
            }

            if (_explicitMappings.TryGetValue(destinationProperty.Name, out var explicitMapping))
            {
                var body = new ParameterReplaceVisitor(explicitMapping.Parameters[0], parameter)
                    .Visit(explicitMapping.Body)!;

                bindings.Add(Expression.Bind(
                    destinationProperty,
                    ConvertIfRequired(body, destinationProperty.PropertyType)));

                continue;
            }

            if (!SourceProperties.TryGetValue(destinationProperty.Name, out var sourceProperty) ||
                !CanAssign(sourceProperty.PropertyType, destinationProperty.PropertyType))
            {
                continue;
            }

            var sourceMember = Expression.Property(parameter, sourceProperty);

            bindings.Add(Expression.Bind(
                destinationProperty,
                ConvertIfRequired(sourceMember, destinationProperty.PropertyType)));
        }

        var bodyExpression = Expression.MemberInit(
            Expression.New(typeof(TViewModel)),
            bindings);

        return Expression.Lambda<Func<TEntity, TViewModel>>(bodyExpression, parameter);
    }

    private static PropertyInfo GetDestinationProperty<TDestinationMember>(
        Expression<Func<TViewModel, TDestinationMember>> destination)
    {
        var body = StripConvert(destination.Body);

        if (body is not MemberExpression memberExpression ||
            memberExpression.Member is not PropertyInfo property ||
            StripConvert(memberExpression.Expression!) != destination.Parameters[0])
        {
            throw new ArgumentException(
                "A projection destination must select one direct property.",
                nameof(destination));
        }

        if (property.SetMethod?.IsPublic != true ||
            property.GetIndexParameters().Length != 0)
        {
            throw new ArgumentException(
                $"Projection destination property '{property.Name}' must have a public setter.",
                nameof(destination));
        }

        return property;
    }

    private static IReadOnlyDictionary<string, PropertyInfo> GetReadableProperties(Type type)
    {
        return type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property =>
                property.GetMethod?.IsPublic == true &&
                property.GetIndexParameters().Length == 0)
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, PropertyInfo> GetWritableProperties(Type type)
    {
        return type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property =>
                property.SetMethod?.IsPublic == true &&
                property.GetIndexParameters().Length == 0)
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
    }

    private static bool CanAssign(Type sourceType, Type destinationType)
    {
        return destinationType.IsAssignableFrom(sourceType) ||
               Nullable.GetUnderlyingType(destinationType) == sourceType;
    }

    private static void EnsureAssignable(
        Type sourceType,
        Type destinationType,
        string destinationPropertyName)
    {
        if (CanAssign(sourceType, destinationType))
        {
            return;
        }

        throw new ArgumentException(
            $"Projection source type '{sourceType.Name}' cannot be assigned to " +
            $"destination property '{destinationPropertyName}' of type '{destinationType.Name}'.");
    }

    private static Expression ConvertIfRequired(Expression expression, Type destinationType)
    {
        return expression.Type == destinationType
            ? expression
            : Expression.Convert(expression, destinationType);
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression unaryExpression &&
               (unaryExpression.NodeType == ExpressionType.Convert ||
                unaryExpression.NodeType == ExpressionType.ConvertChecked))
        {
            expression = unaryExpression.Operand;
        }

        return expression;
    }

    private sealed class ParameterReplaceVisitor : ExpressionVisitor
    {
        private readonly ParameterExpression _source;
        private readonly ParameterExpression _target;

        public ParameterReplaceVisitor(
            ParameterExpression source,
            ParameterExpression target)
        {
            _source = source;
            _target = target;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == _source
                ? _target
                : base.VisitParameter(node);
        }
    }
}

public static class NhProjectionQueryableExtensions
{
    public static IQueryable<TProjection> Select<TEntity, TProjection>(
        this IQueryable<TEntity> queryable,
        NhProjectionDefinition<TEntity, TProjection> projection)
        where TEntity : class
        where TProjection : class
    {
        ArgumentNullException.ThrowIfNull(queryable);
        ArgumentNullException.ThrowIfNull(projection);

        return Queryable.Select(queryable, projection.Selector);
    }
}
