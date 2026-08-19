namespace NewHeap.Platform.Common.Models;


public partial interface IBaseCollectionRequestModel
{
    int Page { get; set; }
    int ItemsPerPage { get; set; }
}


public abstract partial class BaseCollectionRequestModel : IBaseCollectionRequestModel
{
    public int Page { get; set; } = 1;
    public int ItemsPerPage { get; set; } = 20;
}

public partial interface ISearchableBaseCollectionRequestModel : IBaseCollectionRequestModel
{
    string? Search { get; set; }
}

public abstract partial class SearchableBaseCollectionRequestModel : BaseCollectionRequestModel, ISearchableBaseCollectionRequestModel
{
    public string? Search { get; set; }
}

public partial interface ICollectionRequestModel : ISearchableBaseCollectionRequestModel
{
    List<OrderByCollectionRequestModel> OrderBy { get; set; }
    List<FilterCollectionRequestModel> Filter { get; set; }
}

public partial class CollectionRequestModel : SearchableBaseCollectionRequestModel, ICollectionRequestModel
{
    public List<OrderByCollectionRequestModel> OrderBy { get; set; } = [];
    public List<FilterCollectionRequestModel> Filter { get; set; } = [];
}

public enum OrderByMethod
{
    Default = 0,
    Natural = 1
}

public partial class OrderByCollectionRequestModel
{
    public string Key { get; set; } = string.Empty;
    public string Direction { get; set; } = "ASC";
    public OrderByMethod Method { get; set; }
}

public partial class FilterCollectionRequestModel
{
    public string Key { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public object? Value { get; set; }

    public List<FilterCollectionRequestModel>? Ors { get; set; } = [];
    public List<FilterCollectionRequestModel>? Ands { get; set; } = [];
}

public partial class SimpleCollectionResultModel<T>
{
    public int Page { get; set; }
    public int ItemsPerPage { get; set; }
    public long TotalCount { get; set; }
    public int ResultCount { get; set; }
    public List<T> Items { get; set; } = [];

    public static SimpleCollectionResultModel<T> Create(
        List<T> items,
        IBaseCollectionRequestModel requestModel)
    {
        return new SimpleCollectionResultModel<T>
        {
            Items = items,
            TotalCount = items.Count,
            ResultCount = items.Count,
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage
        };
    }

    public static SimpleCollectionResultModel<T> Create(
        List<T> items,
        ISearchableBaseCollectionRequestModel requestModel)
    {
        return new SimpleCollectionResultModel<T>
        {
            Items = items,
            TotalCount = items.Count,
            ResultCount = items.Count,
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage
        };
    }

    public static SimpleCollectionResultModel<T> Create(
        List<T> items,
        int page = 1,
        int? itemsPerPage = null)
    {
        return new SimpleCollectionResultModel<T>
        {
            Items = items,
            TotalCount = items.Count,
            ResultCount = items.Count,
            Page = page,
            ItemsPerPage = itemsPerPage ?? items.Count
        };
    }
}

public partial class CollectionResultModel<T> : SimpleCollectionResultModel<T>
{
    public List<OrderByCollectionRequestModel> OrderBy { get; set; } = [];
    public List<FilterCollectionRequestModel> Filter { get; set; } = [new()];
    public string? Search { get; set; }

    public static CollectionResultModel<T> Create(
        List<T> items,
        IBaseCollectionRequestModel requestModel,
        List<FilterCollectionRequestModel>? filters = null,
        List<OrderByCollectionRequestModel>? orderBys = null
        )
    {
        return new CollectionResultModel<T>
        {
            Items = items,
            TotalCount = items.Count,
            ResultCount = items.Count,
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage,
            Filter = filters ?? [],
            OrderBy = orderBys ?? []
        };
    }

    public static CollectionResultModel<T> Create(
        List<T> items,
        ISearchableBaseCollectionRequestModel requestModel,
        List<FilterCollectionRequestModel>? filters = null,
        List<OrderByCollectionRequestModel>? orderBys = null
        )
    {
        return new CollectionResultModel<T>
        {
            Items = items,
            TotalCount = items.Count,
            ResultCount = items.Count,
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage,
            Filter = filters ?? [],
            OrderBy = orderBys ?? []
        };
    }

    public static CollectionResultModel<T> Create(
        List<T> items,
        ICollectionRequestModel requestModel
    )
    {
        return new CollectionResultModel<T>
        {
            Items = items,
            TotalCount = items.Count,
            ResultCount = items.Count,
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage,
            Filter = requestModel.Filter,
            OrderBy = requestModel.OrderBy
        };
    }

    public static CollectionResultModel<T> Create(
        List<T> items,
        int page = 1,
        int? itemsPerPage = null,
        List<FilterCollectionRequestModel>? filters = null,
        List<OrderByCollectionRequestModel>? orderBys = null
        )
    {
        return new CollectionResultModel<T>
        {
            Items = items,
            TotalCount = items.Count,
            ResultCount = items.Count,
            Page = page,
            ItemsPerPage = itemsPerPage ?? items.Count,
            Filter = filters ?? [],
            OrderBy = orderBys ?? [],
        };
    }
}