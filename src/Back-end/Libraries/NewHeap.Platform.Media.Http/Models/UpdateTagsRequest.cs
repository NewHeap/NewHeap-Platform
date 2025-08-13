namespace NhMedia.Http.Models;

public class UpdateTagsRequest
{
    public string Path { get; set; } = "/";
    public string FileName { get; set; } = string.Empty;
    public string[] Tags { get; set; } = [];
}