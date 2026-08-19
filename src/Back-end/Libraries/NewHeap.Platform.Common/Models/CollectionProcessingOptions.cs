using System.Linq.Expressions;

namespace NewHeap.Platform.Common.Models;

public sealed class CollectionProcessingOptions<TEntity, TViewModel>
    where TEntity : class
    where TViewModel : class
{
    internal CollectionProcessingOptions(
        bool useSearchableAttributes,
        bool useFilterableAttributes,
        bool useOrderableAttributes,
        IReadOnlyList<Expression<Func<TEntity, object?>>> searchableSelectors,
        IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> filterableSelectors,
        IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> orderableSelectors)
    {
        UseSearchableAttributes = useSearchableAttributes;
        UseFilterableAttributes = useFilterableAttributes;
        UseOrderableAttributes = useOrderableAttributes;
        SearchableSelectors = searchableSelectors;
        FilterableSelectors = filterableSelectors;
        OrderableSelectors = orderableSelectors;
    }

    public bool UseSearchableAttributes { get; }
    public bool UseFilterableAttributes { get; }
    public bool UseOrderableAttributes { get; }
    public IReadOnlyList<Expression<Func<TEntity, object?>>> SearchableSelectors { get; }
    public IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> FilterableSelectors { get; }
    public IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> OrderableSelectors { get; }

    public static CollectionProcessingOptions<TEntity, TViewModel> Default { get; } =
        new(
            useSearchableAttributes: true,
            useFilterableAttributes: true,
            useOrderableAttributes: true,
            searchableSelectors: [],
            filterableSelectors: new Dictionary<string, Expression<Func<TEntity, object?>>>(StringComparer.OrdinalIgnoreCase),
            orderableSelectors: new Dictionary<string, Expression<Func<TEntity, object?>>>(StringComparer.OrdinalIgnoreCase));
}

public sealed class CollectionProcessingOptionsBuilder<TEntity, TViewModel>
    where TEntity : class
    where TViewModel : class
{
    private bool? _useSearchableAttributes;
    private bool? _useFilterableAttributes;
    private bool? _useOrderableAttributes;

    private readonly List<Expression<Func<TEntity, object?>>> _searchableSelectors = [];
    private readonly Dictionary<string, Expression<Func<TEntity, object?>>> _filterableSelectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Expression<Func<TEntity, object?>>> _orderableSelectors = new(StringComparer.OrdinalIgnoreCase);

    public CollectionProcessingOptionsBuilder<TEntity, TViewModel> SearchableFromAttributes(bool enabled = true)
    {
        _useSearchableAttributes = enabled;
        return this;
    }

    public CollectionProcessingOptionsBuilder<TEntity, TViewModel> FilterableFromAttributes(bool enabled = true)
    {
        _useFilterableAttributes = enabled;
        return this;
    }

    public CollectionProcessingOptionsBuilder<TEntity, TViewModel> OrderableFromAttributes(bool enabled = true)
    {
        _useOrderableAttributes = enabled;
        return this;
    }

    public CollectionProcessingOptionsBuilder<TEntity, TViewModel> WithSearchable(
        params Expression<Func<TEntity, object?>>[] selectors)
    {
        _searchableSelectors.AddRange(selectors);
        return this;
    }

    public CollectionProcessingOptionsBuilder<TEntity, TViewModel> WithFilterable(
        params Expression<Func<TEntity, object?>>[] selectors)
    {
        foreach (var selector in selectors)
        {
            WithFilterable(CollectionProcessingExpressionResolver.GetFilterKey(selector), selector);
        }

        return this;
    }

    public CollectionProcessingOptionsBuilder<TEntity, TViewModel> WithFilterable(
        string key,
        Expression<Func<TEntity, object?>> selector)
    {
        CollectionProcessingExpressionResolver.GetFilterKey(selector);
        _filterableSelectors[NormalizeKey(key)] = selector;
        return this;
    }

    public CollectionProcessingOptionsBuilder<TEntity, TViewModel> WithOrderable(
        params Expression<Func<TEntity, object?>>[] selectors)
    {
        foreach (var selector in selectors)
        {
            WithOrderable(CollectionProcessingExpressionResolver.GetOrderKey(selector), selector);
        }

        return this;
    }

    public CollectionProcessingOptionsBuilder<TEntity, TViewModel> WithOrderable(
        string key,
        Expression<Func<TEntity, object?>> selector)
    {
        CollectionProcessingExpressionResolver.GetOrderKey(selector);
        _orderableSelectors[NormalizeKey(key)] = selector;
        return this;
    }

    internal CollectionProcessingOptions<TEntity, TViewModel> Build()
    {
        return new CollectionProcessingOptions<TEntity, TViewModel>(
            useSearchableAttributes: _useSearchableAttributes ?? !_searchableSelectors.Any(),
            useFilterableAttributes: _useFilterableAttributes ?? !_filterableSelectors.Any(),
            useOrderableAttributes: _useOrderableAttributes ?? !_orderableSelectors.Any(),
            searchableSelectors: _searchableSelectors.ToArray(),
            filterableSelectors: new Dictionary<string, Expression<Func<TEntity, object?>>>(_filterableSelectors, StringComparer.OrdinalIgnoreCase),
            orderableSelectors: new Dictionary<string, Expression<Func<TEntity, object?>>>(_orderableSelectors, StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Collection processing key cannot be empty.", nameof(key));
        }

        return key.Trim();
    }

    private static string GetMemberPath(Expression<Func<TEntity, object?>> selector)
    {
        var members = new Stack<string>();
        Expression? expression = StripConvert(selector.Body);

        while (expression is MemberExpression memberExpression)
        {
            members.Push(memberExpression.Member.Name);
            expression = StripConvert(memberExpression.Expression!);
        }

        if (expression is not ParameterExpression || !members.Any())
        {
            throw new ArgumentException("A collection processing key can only be inferred from a member access expression.", nameof(selector));
        }

        return string.Join(".", members);
    }

    private static Expression? StripConvert(Expression? expression)
    {
        while (expression is UnaryExpression unaryExpression &&
               (unaryExpression.NodeType == ExpressionType.Convert ||
                unaryExpression.NodeType == ExpressionType.ConvertChecked))
        {
            expression = unaryExpression.Operand;
        }

        return expression;
    }
}
