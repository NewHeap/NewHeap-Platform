# Usage
``` csharp
builder.Services.AddNhMedia(ctx =>
{
    //Configure Filesystem storage provider
    ...
    // Configure filestructure storage provider
    ctx.UseSqlServerFileStructureStorage("connectionstring");
});