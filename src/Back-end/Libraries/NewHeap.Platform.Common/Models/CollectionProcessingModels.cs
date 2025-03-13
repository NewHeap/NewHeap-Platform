namespace NewHeap.Platform.Common.Models;


public partial interface IBaseCollectionRequestModel
{
    int Page { get; set; }
    int ItemsPerPage { get; set; }
}


public abstract partial class BaseCollectionRequestModel : IBaseCollectionRequestModel
{
    public int Page { get; set; }
    public int ItemsPerPage { get; set; }
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
}

public partial class CollectionResultModel<T> : SimpleCollectionResultModel<T>
{
    public List<OrderByCollectionRequestModel> OrderBy { get; set; } = [];
    public List<FilterCollectionRequestModel> Filter { get; set; } = [new()];
    public string? Search { get; set; }
}