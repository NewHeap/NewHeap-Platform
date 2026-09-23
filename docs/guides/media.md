# Store and retrieve files

The media library combines file metadata, binary storage and application-owned
authorization. Use `IMediaLibraryService` for normal file and folder operations.

## Register storage

Reference `NewHeap.Platform.Media.Core`, one file-structure provider, and one
binary-storage provider. This example uses
`NewHeap.Platform.Media.FileStructureStorage.PostgreSql` and
`NewHeap.Platform.Media.MediaStorage.FileSystem`.

In the API's `Program.cs`, import `NewHeap.Media` and configure the providers:

```csharp
builder.Services.AddNhMedia(media =>
{
    media.UsePostgreSqlFileStructureStorage(connectionString, options =>
    {
        options.Scheme = "samplemedia";
        options.RunMigrations = true;
    });
    media.UseFileSystemMediaStorage(storagePath, createDirectoryIfNotExists: true);
    media.AddAuthentication<ApplicationMediaAuthorization>();
});
```

`connectionString` is the PostgreSQL connection string; `storagePath` is a
persistent directory outside the webroot. `RunMigrations` applies the media
provider's schema at startup. In deployments that run migrations separately,
disable it after arranging that migration step.

`ApplicationMediaAuthorization` is your implementation of
`IAuthorizationModule` from `NewHeap.Media.Modules`. Set `context.Authorized`
from the authenticated user's permissions and access to `context.Path`, taking
`context.Action` into account. The sample's
[authorization module](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Services/ProjectMediaAuthorizationModule.cs)
shows a division-scoped path check. Its additional `X-Sample-Media-Permissions`
header is a demo aid; an application must use trusted claims instead.

## Upload a file

Inject `IMediaLibraryService` into your service. Import `NewHeap.Media.Modules`
for the file models and `NewHeap.Platform.Common.Models` for `TaskResult`.
For an existing folder the caller is authorized to use:

```csharp
public Task<TaskResult<FileReference>> UploadAsync(string folderPath, Stream content)
{
    return _mediaLibraryService.CreateFileAsync(
        new FileModel
        {
            Path = folderPath,
            Name = "project-notes.txt",
            Title = "Project notes"
        },
        content);
}
```

The caller owns the input stream and checks the returned result before reporting
success. Apply upload-size and content validation at the HTTP boundary. Use
`CreateFolderAsync(parentPath, name)` if you first need a folder, and stop if its
result fails.

## Try it

Run the sample and open its
[media playground](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/media-playground/media-playground.component.ts).
Create a folder under the selected division, upload a small text file, then
download it and compare the contents.
The [frontend media service](../../examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/project-media-api.service.ts)
shows the requests. The [download service](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Services/ProjectMediaSampleService.cs)
shows how to transfer ownership of a downloaded stream to an HTTP response.

## Next steps

Use the [media reference](../consumer-guide/media.md) for SQL Server, S3,
thumbnails, tags and events. The [storage examples](../../examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests/MediaLibrarySamplesTests.cs)
include a filesystem round trip and S3 registration.
