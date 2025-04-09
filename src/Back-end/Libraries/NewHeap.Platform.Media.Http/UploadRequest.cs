using Microsoft.AspNetCore.Http;

namespace NhMedia.Http;

public class UploadRequest
{
    public string FileName { get; set; } = null!;
    public string Path { get; set; } = null!;
    public string[]? Tags { get; set; }
    public string? AltText { get; set; }
    public string? Description { get; set; }
    public string? Title { get; set; }
    public string? Creator { get; set; }
    public Dictionary<string,object>? MetaData { get; set; }
}