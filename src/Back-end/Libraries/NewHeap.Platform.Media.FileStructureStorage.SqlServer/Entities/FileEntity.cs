using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Media.FileStructureStorage.SqlServer.Entities;

public class FileEntity
{
    public Guid Id { get; set; }

    [StringLength(10_000)]
    public required string? Path { get; set; }

    [StringLength(100)]
    public string? AltText { get; set; }

    [StringLength(100)]
    [Orderable]
    public string? Title { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(150)]
    public string? Creator { get; set; }

    [StringLength(2000)]
    [Orderable]
    public required string Name { get; set; }

    [Orderable]
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Tags { get; set; } = [];

    [Column(TypeName = "NVARCHAR(MAX)")]
    // ReSharper disable once EntityFramework.ModelValidation.UnlimitedStringLength
    public string? MetaData { get; set; }
}