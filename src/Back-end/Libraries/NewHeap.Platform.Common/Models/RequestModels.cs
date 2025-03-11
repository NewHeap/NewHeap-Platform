namespace NewHeap.Platform.Common.Models;


public abstract partial class BaseCollectionRequestModel
{
    public int Page { get; set; }
    public int ItemsPerPage { get; set; }
}

public abstract partial class SearchableBaseCollectionRequestModel : BaseCollectionRequestModel
{
    public string? Search { get; set; }
}

public partial class CollectionRequestModel : SearchableBaseCollectionRequestModel
{
    public List<OrderByRequestModel> OrderBy { get; set; } = [];
    public List<FilterRequestModel> Filter { get; set; } = [];
}

public enum OrderByMethod
{
    Default = 0,
    Natural = 1
}

public partial class OrderByRequestModel
{
    public string Key { get; set; } = string.Empty;
    public string Direction { get; set; } = "ASC";
    public OrderByMethod Method { get; set; }
}

public partial class FilterRequestModel
{
    public string Key { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public object? Value { get; set; }

    public List<FilterRequestModel>? Ors { get; set; } = [];
    public List<FilterRequestModel>? Ands { get; set; } = [];
}