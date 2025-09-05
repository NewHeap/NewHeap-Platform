namespace NewHeap.Media.Models;

public class FileGetOptions
{
    public List<SortOption> OrderBy { get; set; } = [];
}

public record SortOption
{
    public string Field { get; set; }
    public Direction Direction { get; set; }
}

public enum Direction
{
    Ascending,
    Descending
}