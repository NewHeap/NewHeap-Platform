namespace NewHeap.Media.Models;

public class SearchOptions
{
    /// <summary>
    /// Language to search
    /// </summary>
    public string? Language { get; set; }
    
    /// <summary>
    /// Tags to search
    /// </summary>
    public string[]? Tags { get; set; }
    
    /// <summary>
    /// When set only file extensions included in this set will be returned 
    /// </summary>
    public string[]? IncludedExtensions { get; set; }
    
    /// <summary>
    /// When set files with given extensions will not be returned
    /// </summary>
    public string[]? ExcludedExtensions { get; set; }

    public int PageSize { get; set; } = 20;
    public int PageIndex { get; set; } = 0;
}