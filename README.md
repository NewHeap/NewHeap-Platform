<p align="center">
  <img src="src/Back-end/Assets/NH_logo.png" width="112" alt="NewHeap logo">
</p>

<h1 align="center">NewHeap Platform</h1>

Reusable .NET 10 and Angular 20 libraries for business applications: APIs,
data access, authentication, UI components, background work, media and AI.
Adopt the packages you need; your application owns its domain, permissions
and migrations.

## Start here

0. [Install and configure the platform](docs/guides/platform-setup.md): install
   packages, configure the database, API and Angular, and verify sign-in and the
   connection before implementing a feature.
1. [Build a working lister](docs/guides/first-project-lister.md): follow the project
   data from the database through the API to an Angular list with search, sorting
   and paging.
2. [Add CRUD](docs/guides/project-crud.md): extend the same feature with create,
   edit and delete, including validation and list refresh.

The guides link to the relevant sample implementation and include checks
for the complete request flow.

## Main components

Choose a component for a short guide with setup steps and practical examples.
See [package installation](docs/how-to/consume-public-packages.md) for NuGet and
npm setup.

| Component | What it provides | Guide |
| --- | --- | --- |
| .NET foundations | Shared build settings and package management | [Set up an application](docs/guides/dotnet-foundation.md) |
| APIs and domain modules | Controllers, services, view/mutate models and mapping | [Build an API module](docs/guides/backend-modules.md) |
| Data access | Repositories, SQL Server, PostgreSQL and transactions | [Query and update data](docs/guides/data-access.md) |
| Authentication and authorization | Sign-in and application, division and resource permissions | [Protect an API](docs/guides/authentication.md) |
| Angular collections | Server-side filtering, sorting and paging | [Filter a collection](docs/guides/collections.md) |
| Angular forms, pages and modals | Validation, lifecycle hooks and edit dialogs | [Edit a record](docs/guides/forms-and-modals.md) |
| Media | Authorized files and folders, thumbnails and storage providers | [Store and retrieve files](docs/guides/media.md) |
| Configuration | Runtime settings and automation overrides | [Load settings and secrets](docs/guides/configuration.md) |
| Testing | Reusable contexts, factories and assertions for consumers | [Test application code](docs/guides/testing.md) |
| Background processing | Events, jobs, notifications and resumable operations | [Run background work](docs/guides/background-processing.md) |
| Reverse proxy | Redirects, forwarding rules and an administration panel | [Configure a proxy](src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy/README.md) |
| AI | Model profiles, scoped retrieval and approval-controlled actions | [Use a model profile](docs/guides/ai.md) |

For detailed contracts and edge cases, use the
[technical reference](docs/consumer-guide/index.md).

## Run the example application

`SampleProjectManagement` combines a real API, PostgreSQL, RabbitMQ and two
Angular applications through Aspire. Install .NET 10, Node.js 22 or later,
npm and Docker, then follow the [sample setup](examples/SampleProjectManagement/README.md#run-the-sample),
including its secrets configuration and development sign-in details.

![SampleProjectManagement management portal](docs/assets/readme/sample-management.png)

Use the [sample catalog](examples/SampleProjectManagement/docs/sample-catalog.md)
to find executable examples of each capability.

## Contributing, releases and support

See [CONTRIBUTING.md](CONTRIBUTING.md) for development,
[AGENTS.md](AGENTS.md#verification-checklist) for verification and the
[release guide](docs/how-to/release-newheap-libraries.md) for the protected
publishing workflow. For help, read [SUPPORT.md](SUPPORT.md); report
vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

Unless otherwise noted, NewHeap-authored software is licensed under the
[Apache License 2.0](LICENSE). See [NOTICE](NOTICE),
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and
[TRADEMARKS.md](TRADEMARKS.md).
