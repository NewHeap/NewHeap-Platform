# NewHeap Platform Common

## API clients

`AddNhApiClient<TApi>` registers a reusable client for one logical target API.
`TApi` is an empty marker type that lets related endpoint services share the
same base address, handlers, and token cache.

Registration can be performed directly in `Program.cs`:

```csharp
builder.Services.AddNhApiClient<CommerceManagementApi>(options =>
{
    options.BaseAddress = new Uri(
        builder.Configuration["Commerce:ManagementApiUrl"]!);
});
```

Or from a configuration section:

```csharp
builder.Services.AddNhApiClient<CommerceManagementApi>(
    builder.Configuration.GetSection("ApiClients:CommerceManagement"));
```

```json
{
  "ApiClients": {
    "CommerceManagement": {
      "BaseAddress": "https://management.example.test",
      "Timeout": "00:00:30"
    }
  }
}
```

An endpoint service derives from `BaseNhApiService<TApi>`:

```csharp
public sealed class CommerceManagementApi;

public sealed class OperationsUserApiService
    : BaseNhApiService<CommerceManagementApi>
{
    public OperationsUserApiService(
        ILogger<OperationsUserApiService> logger,
        INhApiHttpClientFactory<CommerceManagementApi> httpClientFactory)
        : base(logger, httpClientFactory)
    {
    }

    public Task<TaskResult<OperationsUserViewModel>> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return DoGetAsync<OperationsUserViewModel>(
            $"/api/management/operations-user/{id}",
            cancellationToken);
    }
}
```

The base class provides helpers for GET, collection GET, POST, PUT, PATCH, and
DELETE. Every helper has a default implementation and is `protected virtual`.
JSON responses and NewHeap validation errors are returned as `TaskResult`.

### Downloads and raw responses

Use `DoGetResponseAsync` when content must not be buffered as JSON. The result
owns the response, request, and factory client, so it must be disposed:

```csharp
public async Task<TaskResult> DownloadAsync(
    Stream destination,
    CancellationToken cancellationToken = default)
{
    using var responseResult = await DoGetResponseAsync(
        "/api/management/export",
        cancellationToken);

    if (!responseResult.Success)
    {
        return TaskResult.Failed(responseResult);
    }

    await using var source = await responseResult.Data.ReadAsStreamAsync(cancellationToken);
    await source.CopyToAsync(destination, cancellationToken);
    return TaskResult.Succeeded();
}
```

For other HTTP methods, the same raw pipeline is available through
`DoSendResponseAsync`. Regular DTO methods deliberately remain `TaskResult<T>`.

### Username and password authentication

When `Authentication` is present, the library automatically registers a
separate authentication client, bearer handler, and thread-safe token cache:

```json
{
  "ApiClients": {
    "CommerceManagement": {
      "BaseAddress": "https://management.example.test",
      "Authentication": {
        "Endpoint": "/api/authentication/username-password",
        "Username": "service-account",
        "Password": "configure-via-user-secrets-or-environment",
        "Realm": "",
        "RefreshBeforeExpiration": "00:03:00"
      }
    }
  }
}
```

Do not store passwords in a committed `appsettings.json`; use user secrets,
environment variables, or a secret store. For other authentication methods, a
custom `INhApiAccessTokenProvider<TApi>` can be registered through
`AddNhApiClient<TApi, TAccessTokenProvider>()`.
