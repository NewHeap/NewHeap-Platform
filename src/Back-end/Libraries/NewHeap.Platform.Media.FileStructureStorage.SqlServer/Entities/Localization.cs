using System.ComponentModel.DataAnnotations;
using NewHeap.Media.Models;

namespace NewHeap.Media.FileStructureStorage.SqlServer.Entities;

public class Localization : ILocalizationData
{
    public required string TypeName { get; set; }
    public Guid EntityId { get; set; }
    [StringLength(5)]
    public required string Language { get; set; }
    [StringLength(2000)]
    public required string Value { get; set; }

    [StringLength(100)]
    public required string PropertyName { get; set; }
}