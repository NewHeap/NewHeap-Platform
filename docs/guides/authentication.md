# Sign in and protect an API

Use NewHeap's standard authentication endpoints for sign-in and refresh tokens.
Use ASP.NET authorization policies to decide which actions a signed-in user may
perform.

## Enable password sign-in

In an application with NewHeap Identity and its database already configured,
add authentication to the `AddNewHeapPlatformAspNetCommon(...)` registration
chain. The view models below come from
`NewHeap.Platform.AspNet.Common.Models.View`:

```csharp
.AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(options =>
{
    options.AddUserNamePasswordAuthentication(authentication =>
    {
        authentication.EnableRefreshToken = true;
        authentication.EnableDivisions = true;
        authentication.Enabled = true;
    });
})
```

Configure the JWT signing key through secrets and create users with ASP.NET
Identity. The [sample setup](../../examples/SampleProjectManagement/README.md)
includes the configuration template and development accounts. Its
[Program.cs](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Program.cs)
shows the surrounding Identity registration and middleware.

## Require a permission

In `ConfigureAuthorization` on `NewHeapAspNetCommonOptions.Builder(...)`, register
a policy. Import `NewHeap.Platform.Common.Identity.Claims`:

```csharp
options.AddPolicy(
    "app.project.manage",
    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.project.manage"));
```

Grant that claim to the user's role. The sample's
[identity seeder](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Services/SampleDevelopmentIdentitySeeder.cs)
creates a manager role with `app.project.manage`, assigns it to the manager
account, and gives the viewer only `app.project.view`.

Apply `[Authorize(Policy = "app.project.manage")]` to the create, update and
delete actions. The [API module example](backend-modules.md#expose-a-create-action)
shows a complete protected action. Requests without authentication receive `401`;
signed-in users without the required permission receive `403`.

## Reflect permissions in Angular

After configuring the NewHeap root module and auth service, import plain
`NhCommonModule` in the feature component. The permission pipe can control the
visibility of an existing edit section:

```html
@if (['app.project.manage'] | isOnePermissionGranted) {
  <p>{{ 'project.edit-project' | translate }}</p>
}
```

Use the API policy to enforce access and the pipe to show available actions.

## Try it and extend it

Run the sample and sign in as `sample@example.test`, then as
`viewer@example.test`, using the [development sign-in details](../../examples/SampleProjectManagement/README.md#run-the-sample).
The manager can change projects; the viewer can read them.
The [authentication playground](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/auth-playground/auth-playground.component.ts)
shows the frontend sign-in and permission checks together.

For more specific access, continue with
[division and resource permissions](../consumer-guide/authorization-permissions.md).
Use [authentication overrides](../consumer-guide/authentication-overrides.md)
when you need custom token claims or current claims loaded per request.
