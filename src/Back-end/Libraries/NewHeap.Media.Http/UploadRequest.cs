using Microsoft.AspNetCore.Http;

namespace NhMedia.Http;

public class UploadRequest
{
    public string FileName { get; set; } = null!;
    public IFormFile File { get; set; } = null!;
}