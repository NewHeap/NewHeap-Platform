---
id: nh-observability-logging
title: "Provider-neutral logging and observability"
area: operations
reference: observability-logging
summary: "Let NewHeap compose structured ILogger output, OpenTelemetry traces and metrics once, while consumers supply only exporter configuration and domain context."
sample-cases: ["SPM-105", "SPM-159"]
public-symbols: ["AddNewHeapObservability", "AddNewHeapAspNetObservability", "NewHeapObservabilityOptions", "NewHeapOtlpExporterMode", "UseNhCommonConfiguration", "UseNewHeapAspnetCommonConfiguration", "UseNewHeapTraceIdentifier", "NH_ERROR_HANDLERS", "NhErrorHandlerService"]
skills: ["newheap-backend-development", "newheap-frontend-development", "newheap-runtime-configuration"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Use `UseNhCommonConfiguration(args)` for workers and `UseNewHeapAspnetCommonConfiguration(args)` for ASP.NET Core applications. These entry points register structured OpenTelemetry logging, runtime and HTTP-client metrics, application and HTTP-client traces, and ASP.NET Core server instrumentation exactly once. A consumer `ServiceDefaults` project may retain health checks, service discovery and HTTP resilience, but must not register the same OpenTelemetry instrumentation again.

The OTLP exporter is enabled automatically only when `OTEL_EXPORTER_OTLP_ENDPOINT` is present. Use standard `OTEL_*` environment variables for deployment configuration. When code-level customization is necessary, call `AddNewHeapObservability` before the normal NewHeap configuration entry point and configure `NewHeapObservabilityOptions`; keep vendor-specific exporters and credentials in the consumer composition root.

Use `deployment.environment.name` as the stable deployment-environment resource attribute in queries, alerts and dashboards. NewHeap derives it from the host environment name and normalizes the well-known `Development`, `Staging`, `Production` and `Test` names to lowercase. NewHeap also emits the exact host environment name as `deployment.environment` temporarily for compatibility with existing dashboards. Values supplied through `OTEL_RESOURCE_ATTRIBUTES`, earlier resource configuration or `NewHeapObservabilityOptions.ConfigureResource` remain consumer-owned; the consumer callback runs last and can deliberately override either default.

Inject `ILogger<T>` into services, workers, middleware and external I/O adapters. Use stable message templates and named properties. Add an operation scope when several log entries belong to the same domain action. Log an exception once where it is handled, translated into a safe result, or abandoned after retries; let exceptions that are rethrown reach the outer boundary without duplicate logging.

Use `Information` for meaningful state transitions, `Warning` for recoverable degraded outcomes, `Error` for failed operations requiring attention, and `Debug` for bounded diagnostics. Do not log access tokens, passwords, connection strings, cookies, authorization headers, complete request or response bodies, email content, raw AI prompts, personal data, or exception text returned to an API client. Prefer opaque identifiers and counts over names and payloads.

Enable `UseNewHeapTraceIdentifier` in the HTTP pipeline. The middleware accepts only bounded safe characters in `X-Correlation-ID`, places the value in the `correlation_id` logging scope, and returns it in the response. Correlation identifiers are diagnostic context, never authentication or authorization evidence.

On Angular, register provider-neutral implementations under the multi-provider `NH_ERROR_HANDLERS`. `NhErrorHandlerService` forwards errors to each provider and isolates a provider failure. Keep remote browser telemetry opt-in at the consumer boundary, apply redaction there, and never store raw error messages by default.

## Avoid

- Registering `AddOpenTelemetry`, ASP.NET Core instrumentation, runtime instrumentation, or an OTLP exporter again in consumer `ServiceDefaults`.
- Interpolated log strings, anonymous unbounded objects, or high-cardinality payloads as structured properties.
- Logging an exception in every layer while rethrowing it unchanged.
- Returning `exception.Message` or `exception.ToString()` in a `TaskResult` or HTTP response.
- Treating correlation, tenant, workspace, user, or resource identifiers from the browser as trusted authorization context.
- Adding a browser telemetry vendor to the reusable Angular package.

## Verification

Test registration idempotency with and without `OTEL_EXPORTER_OTLP_ENDPOINT`. Verify that logs, traces and metrics carry `deployment.environment.name`, that the temporary `deployment.environment` compatibility value retains the host's original casing, and that preconfigured values are preserved. Verify one server span, one client span and one structured completion log for an executable operation. Exercise valid, invalid and oversized correlation identifiers. Capture logs in tests and assert property names, levels and scopes without asserting sensitive values. Trigger one handled failure and prove it is logged once with its exception. For Angular, register two error handlers, make the first throw, and verify that the second still receives the original error.
