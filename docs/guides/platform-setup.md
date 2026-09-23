# Step 0: install and configure the platform

Set up the API, database, Angular application and sign-in before building the
[lister in Step 1](first-project-lister.md).

## 1. Install the packages

Use .NET 10 and an Angular 20 workspace with a compatible Node.js version.

For an API with PostgreSQL, run these commands in the API project directory.
Replace `<version>` with the compatible released version of each package:

```sh
dotnet add package NewHeap.Platform.AspNet.Common --version <version>
dotnet add package NewHeap.Platform.AspNet.Common.PostgreSql --version <version>
dotnet restore
```

For SQL Server, select `NewHeap.Platform.AspNet.Common.SqlServer` instead. Keep
provider selection in the API project. The [.NET foundation guide](dotnet-foundation.md)
explains DAL/domain project references and shared build settings.

In your Angular workspace:

```sh
npm install @newheap/platform-common
```

See
[package installation](../how-to/consume-public-packages.md) for registry settings
and version management.

## 2. Configure the API and database

Create the configuration before registering NewHeap services. Start with
`builder.UseNewHeapAspnetCommonConfiguration(args)` in `Program.cs` and follow
[settings and secrets](configuration.md) to load a `secrets.json` file outside
the repository.

Set these values for your application. The paths are configuration keys, using
colons to separate nested JSON objects:

| Key | Value to configure |
| --- | --- |
| `NewHeap:PlatformCommon:AppSecretsDirectoryPath` | Directory containing your `secrets.json` |
| `ConnectionStrings:DefaultConnection` | `${Secrets:ConnectionStrings:DefaultConnection}`; put the actual connection string in secrets |
| `NewHeap:PlatformAspNetCommon:Settings:SelfBaseUrl` | Your API's externally reachable base URL |
| `NewHeap:PlatformAspNetCommon:Settings:AllowedOrigins` | An array of your Angular application's allowed origins |
| `NewHeap:PlatformAspNetCommon:Settings:DefaultCulture` | For example, `en-US` |
| `NewHeap:PlatformAspNetCommon:Settings:SupportedCultures` | For example, `["en-US"]` |
| `NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key` | `${Secrets:NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key}` |
| `NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer` | The issuer used by your API when creating tokens |
| `NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:ValidAudience` | The audience accepted by your API |
| `NewHeap:PlatformAspNetCommon:DbLogServiceSettings:RootDirectory` | A writable log directory |

Use a unique signing key; the sample instructions below show how to generate one.
For the standard single-API setup, use the API base URL for both issuer and
audience. [Sample appsettings](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/appsettings.json)
shows the JSON structure; replace its application URLs and secrets path.

Wire the host in this order:

1. Create your application's DbContext using the NewHeap Identity context, as in
   the [sample DbContext](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.DAL/SampleProjectManagementDbContext.cs).
   Preserve `base.OnModelCreating(builder)` so the Identity model is configured.
2. Build `NewHeapAspNetCommonOptions` with your configuration, mapping profiles
   and authorization policies, then call `AddNewHeapPlatformAspNetCommon<...>`
   with your DbContext. The [API startup](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Program.cs)
   supplies the complete registration signature.
3. Enable [standard password authentication](authentication.md#enable-password-sign-in).
   Add `.WithIdentityEntityFramework(...)` with `UseNewHeapPostgreSql(connectionString)`
   or `UseNewHeapSqlServer(connectionString)`, then `.WithIdentity(_ => { })`
   and `.WithDbLogService(...)`. Read your connection string with
   `builder.Configuration.GetConnectionString("DefaultConnection")`.
4. After `builder.Build()`, configure `UseNewHeapPlatformAspNetCommon(...)` and
   `UseNhAuthentication<...>(authentication => authentication.AddUserNamePasswordEndpoint())`,
   as shown near the end of the API startup file.
5. Generate and apply your application's EF migrations against the selected
   database. Store the migrations in your DAL project.
6. Create your initial user through Identity and assign its role, claims and
   division membership. The [development identity seeder](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Services/SampleDevelopmentIdentitySeeder.cs)
   demonstrates this, including confirmed accounts and project permissions.
   Keep development accounts restricted to the development environment.

**Checkpoint:** the API starts, the configured database is reachable and its
Identity tables exist. If you enable the sample's OpenAPI/Scalar setup, both
`/openapi/v1.json` and `/scalar` should load before you configure Angular.

## 3. Configure Angular and the API connection

Use the [sample root providers](../../examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/sample-project-management.providers.ts)
as the registration reference. In your application's root configuration:

1. Register your authentication service and pass it to
   `NhCommonModule.forRoot(config, YourAuthService)` exactly once. The
   [sample auth service](../../examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/sample-auth.service.ts)
   shows the `BaseNhAuthService<NhAuthorization>` implementation. Feature components
   import plain `NhCommonModule`.
2. Set `baseUrl` to the Angular origin, and `apiBaseUrl` and `authApiBaseUrl` to
   `/api`. Configure the login route, language, culture and translation loader.
   Provide the translation files under `public/i18n` and the toast/error handlers
   used by your application; the sample providers include their imports.
3. Explicitly enable these options in your `NhCommonModuleConfig`:

   ```ts
   http: new NhHttpNhCommonModuleConfig({ deduplicateGetRequests: true }),
   formDropdown: new NhFormDropDownNhCommonModuleConfig({ deferLazyLoadUntilOpened: true })
   ```

4. Configure the development proxy so `/api` reaches your API and the prefix is
   removed. The [sample proxy](../../examples/SampleProjectManagement/src/Front-end/proxy.conf.cjs)
   shows the routing; replace its target with your API URL and pass
   `--proxy-config proxy.conf.cjs` to `ng serve`.
5. Add sign-in and a protected application route. Follow the
   [sample login component](../../examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/sample-login.component.ts)
   for the authentication request and authorization-profile refresh. Match the
   configured login path to the actual Angular route.

**Checkpoint:** Angular loads translations without errors, sign-in succeeds
against the API and the protected route opens. Reload the page to check session
restoration.

## 4. Run the complete reference setup

To run SampleProjectManagement, install .NET 10, Node.js 22 or later, npm and
Docker. The AppHost installs frontend dependencies and starts PostgreSQL,
RabbitMQ, the API and Angular applications.

From the repository root in PowerShell, create a secrets file outside the checkout:

```powershell
$env:SAMPLE_PROJECT_MANAGEMENT_APP_SECRETS_ROOT = Join-Path $env:LOCALAPPDATA 'NewHeapSampleSecrets'
$sampleSecretsDirectory = Join-Path $env:SAMPLE_PROJECT_MANAGEMENT_APP_SECRETS_ROOT 'SampleProjectManagement'
New-Item -ItemType Directory -Force -Path $sampleSecretsDirectory
$sampleSecretsFile = Join-Path $sampleSecretsDirectory 'secrets.json'
if (-not (Test-Path -LiteralPath $sampleSecretsFile)) {
    Copy-Item examples/SampleProjectManagement/secrets.template.json $sampleSecretsFile
}
```

Edit that file. Fill `Parameters:rabbitmq-password` with a local development
password and `NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key` with a
random 64-byte Base64 value. Generate the latter in PowerShell:

```powershell
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(64))
```

With Docker running, start the AppHost from the same terminal:

```sh
dotnet run --project examples/SampleProjectManagement/src/Back-end/Orchestration/SampleProjectManagement.AppHost
```

In the Aspire dashboard, wait for the API and management application to be ready.
Open the API's `/scalar` page, then the management application's URL. Sign in
with `sample@example.test` / `Sample123!` and open `/management/interactions`.

## 5. Check the foundation before building a feature

For the sample, check `/api/projects` in the browser Network panel. It must return
`200` with `items`, `totalCount`, `page` and `itemsPerPage`. Check the response:
some sample screens display demonstration data after a failed request. In your
own application, verify sign-in and an authenticated API request.

| Symptom | Check first |
| --- | --- |
| API fails at startup | Resolved secrets, connection string, database readiness and migrations in the API logs |
| Angular cannot reach the API | Proxy target, API port and local HTTPS certificate trust |
| `404` on an API request | Proxy prefix removal and the backend route |
| `401` after sign-in | Authentication response, cookie/token transmission and JWT settings |
| `403` on a protected endpoint | User's role/claims and the endpoint policy |
| Translation keys appear as text | Translation loader URL and module-scoped keys in the JSON file |

Continue with [Step 1: build the lister](first-project-lister.md) when the
database, API, Angular application and sign-in are working together.
