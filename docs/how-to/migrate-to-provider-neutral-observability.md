# Migrate to provider-neutral observability

This release moves logging, tracing and metrics behind NewHeap-owned, provider-neutral entry points. It removes the former Sentry backend and Angular APIs, so the migration is intentionally a major-version change.

## Backend

Keep the normal host setup:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.UseNewHeapAspnetCommonConfiguration(args);
```

For a non-HTTP worker, use `UseNhCommonConfiguration(args)`. Remove duplicate `AddOpenTelemetry`, `AddRuntimeInstrumentation`, `AddAspNetCoreInstrumentation`, `AddHttpClientInstrumentation` and `UseOtlpExporter` calls from consumer `ServiceDefaults` projects. Health checks, service discovery and HTTP resilience remain consumer orchestration concerns.

Configure export through `OTEL_EXPORTER_OTLP_ENDPOINT` and the other standard `OTEL_*` variables. For an explicit code-level choice, call `AddNewHeapObservability` before the normal configuration entry point:

```csharp
builder.AddNewHeapObservability(options =>
{
    options.OtlpExporterMode = NewHeapOtlpExporterMode.Enabled;
    options.ServiceName = "orders-api";
});
builder.UseNewHeapAspnetCommonConfiguration(args);
```

NewHeap adds `deployment.environment.name` to log, trace and metric resources from `IHostEnvironment.EnvironmentName`; use this stable semantic-convention attribute for new dashboards. Well-known environment names are normalized to lowercase. The legacy `deployment.environment` attribute is also emitted with the host's exact casing for temporary compatibility with existing Grafana dashboards. Existing values from `OTEL_RESOURCE_ATTRIBUTES` or earlier resource configuration are preserved, and `NewHeapObservabilityOptions.ConfigureResource` remains the final deliberate override point.

Remove calls to `WithSentry` and `UseNewHeapSentry`. Replace vendor scope tags with structured `ILogger.BeginScope` properties or `Activity` tags. Do not return exception messages to clients.

`UseOtlpUseExporter` remains temporarily as an obsolete spelling alias; migrate it to `UseOtlpExporter` or standard environment configuration.

## Angular

Remove the Sentry npm packages, Sentry configuration from `NhCommonModuleConfig`, and imports of the removed Sentry services, trace helpers, hooks and injection tokens.

Register an application-specific error observer through the existing provider-neutral multi-provider:

```ts
{
  provide: NH_ERROR_HANDLERS,
  useClass: ApplicationErrorHandler,
  multi: true
}
```

The reusable package does not select a remote browser telemetry vendor. If a consumer needs one, its handler owns consent, sampling, redaction, release metadata and transport configuration.

## Verification

Build without any Sentry NuGet or npm dependency, verify the lock files, and run the observability and error-handler tests. In a deployed environment, confirm that logs, traces and metrics reach the configured OTLP collector and that invalid external correlation identifiers are rejected.
