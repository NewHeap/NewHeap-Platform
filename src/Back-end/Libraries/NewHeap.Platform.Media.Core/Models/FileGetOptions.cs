using System.Text.Json.Serialization;

namespace NewHeap.Media.Models;

public class FileGetOptions
{
    public int? PageSize { get; set; }
    public int Page { get; set; } = 0;
    public List<SortOption> OrderBy { get; set; } = [];
}

public class SortOption
{
    public string? Key { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Direction Direction { get; set; }
}

public enum Direction
{
    Ascending,
    Descending
}