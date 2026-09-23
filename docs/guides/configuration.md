# Load configuration and secrets

NewHeap reads application settings, substitutes values from a separate secrets
file, and lets environment variables or command-line arguments override settings.

## Configure an ASP.NET Core host

Reference `NewHeap.Platform.AspNet.Common`. In `Program.cs`, configure the builder
before registering services that read settings:

```csharp
using NewHeap.Platform.AspNet.Common;

var builder = WebApplication.CreateBuilder(args);
builder.UseNewHeapAspnetCommonConfiguration(args);
```

Set the secrets directory and refer to its values in `appsettings.json`:

```json
{
  "NewHeap": {
    "PlatformCommon": {
      "AppSecretsDirectoryPath": "C:/NewHeapAppSecrets/Example"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "${Secrets:ConnectionStrings:DefaultConnection}"
  },
  "Automation": {
    "Mode": "normal"
  }
}
```

Replace the path with a directory on your machine. Create `secrets.json` there,
outside the repository, with your actual database connection string:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "<your connection string>"
  }
}
```

Read the resolved value through standard configuration:

```csharp
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
```

`connectionString` now contains the value from `secrets.json`.
The [sample setup](../../examples/SampleProjectManagement/README.md#secrets) provides
a complete secrets template for its API, authentication and broker.

## Override a setting when launching

From the API project directory, run:

```sh
dotnet run -- --Automation:Mode=preview
```

`builder.Configuration["Automation:Mode"]` now returns `preview`. Passing `args`
to `UseNewHeapAspnetCommonConfiguration` is what preserves that override. An
environment variable uses double underscores: `Automation__Mode=preview`.
Command-line values take precedence over environment variables.

For deployment, set `NewHeap__PlatformCommon__AppSecretsDirectoryPath` to that
host's secrets directory. Avoid passing actual secrets on the command line.

## Further reading

[The executable configuration example](../../examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests/ConfigurationOverrideSamplesTests.cs)
demonstrates both a secrets-directory override and a final setting override.
The [configuration reference](../consumer-guide/runtime-configuration.md#configuration-overrides-for-runtime-and-automation)
covers additional runtime details.
