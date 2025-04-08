namespace NhMedia.Http.Models;

public class UpdateTagsRequest
{
    public string Path { get; set; }
    public string FileName { get; set; }
    public string[] Tags { get; set; } = [];
}