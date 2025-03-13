// https://learn.microsoft.com/en-us/dotnet/aspire/get-started/build-aspire-apps-with-nodejs


import { env } from 'node:process';
import { NodeSDK } from '@opentelemetry/sdk-node';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-grpc';
import { OTLPMetricExporter } from '@opentelemetry/exporter-metrics-otlp-grpc';
import { OTLPLogExporter } from '@opentelemetry/exporter-logs-otlp-grpc';
import { SimpleLogRecordProcessor } from '@opentelemetry/sdk-logs';
import { PeriodicExportingMetricReader } from '@opentelemetry/sdk-metrics';
import { HttpInstrumentation } from '@opentelemetry/instrumentation-http';
import { ExpressInstrumentation } from '@opentelemetry/instrumentation-express';
import { RedisInstrumentation } from '@opentelemetry/instrumentation-redis-4';
import { credentials } from '@grpc/grpc-js';

export const setupOpenTelemetryServer = () => {
  const environment = (<any>process).env.NODE_ENV || 'development';

// For troubleshooting, set the log level to DiagLogLevel.DEBUG
//diag.setLogger(new DiagConsoleLogger(), environment === 'development' ? DiagLogLevel.INFO : DiagLogLevel.WARN);

  const otlpServer = (<any>env).OTEL_EXPORTER_OTLP_ENDPOINT;

  if (otlpServer) {
    console.log(`OTLP endpoint: ${otlpServer}`);

    const isHttps = otlpServer.startsWith('https://');
    const collectorOptions = {
      credentials: !isHttps
        ? credentials.createInsecure()
        : credentials.createSsl()
    };

    const sdk = new NodeSDK({
      traceExporter: new OTLPTraceExporter(collectorOptions),
      metricReader: new PeriodicExportingMetricReader({
        exportIntervalMillis: environment === 'development' ? 5000 : 10000,
        exporter: new OTLPMetricExporter(collectorOptions),
      }),
      logRecordProcessors: [
        new SimpleLogRecordProcessor(new OTLPLogExporter(collectorOptions))
      ],
      instrumentations: [
        new HttpInstrumentation(),
        new ExpressInstrumentation(),
        new RedisInstrumentation()
      ],
    });

    sdk.start();
  }
};

