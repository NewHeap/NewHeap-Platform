using NewHeap.Media.Modules;

namespace NewHeap.Media.Models;

public class SearchResults
{
    public IEnumerable<FileReference> Results { get; set; } = [];

    public long TotalCount { get; set; }

    public int ItemsPerPage { get; set; }
    public int PageIndex { get; set; }
}