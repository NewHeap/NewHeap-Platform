namespace NhMedia.Http.Models;

public class UpdateFolderRequest
{
    public string? Path { get; set; }
    public required string  FolderName { get; set; }
    public string? NewPath { get; set; }
    public required string NewName { get; set; }
}