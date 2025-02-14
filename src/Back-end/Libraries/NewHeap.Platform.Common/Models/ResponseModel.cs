using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Models;

public partial class CollectionResponseModel<T>
{
    public int Page { get; set; }
    public int ItemsPerPage { get; set; }
    public long TotalCount { get; set; }
    public int ResultCount { get; set; }
    public List<OrderByRequestModel> OrderBy { get; set; } = [];
    public List<FilterRequestModel> Filter { get; set; } = [new()];
    public string? Search { get; set; }
    public IEnumerable<T> Items { get; set; } = Enumerable.Empty<T>();
}
