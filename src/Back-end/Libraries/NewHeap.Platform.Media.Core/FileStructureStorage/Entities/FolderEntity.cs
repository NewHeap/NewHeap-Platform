using System.ComponentModel.DataAnnotations;

namespace NewHeap.Media.FileStructureStorage.SqlServer.Entities;

public class FolderEntity
{
    public Guid Id { get; set; }
    
    [StringLength(10_000)]
    public string? Path { get; set; }
    
    [StringLength(2000)]
    public required string Name { get; set; }
}