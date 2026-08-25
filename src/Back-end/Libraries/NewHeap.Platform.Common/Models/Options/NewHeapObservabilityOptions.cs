using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NewHeap.Platform.Common.Models.Options;

public enum NewHeapOtlpExporterMode
{
    Auto,
    Enabled,
    Disabled
}

public sealed class NewHeapObservabilityOptions
{
    public NewHeapOtlpExporterMode OtlpExporterMode { get; set; } = NewHeapOtlpExporterMode.Auto;

    public string? ServiceName { get; set; }

    public bool IncludeFormattedMessage { get; set; } = true;

    public bool IncludeScopes { get; set; } = true;

    public bool ParseStateValues { get; set; } = true;

    public Action<OpenTelemetryLoggerOptions>? ConfigureLogging { get; set; }

    public Action<MeterProviderBuilder>? ConfigureMetrics { get; set; }

    public Action<TracerProviderBuilder>? ConfigureTracing { get; set; }

    public Action<ResourceBuilder>? ConfigureResource { get; set; }
}
