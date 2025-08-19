export const baseEnvironment = {
  production: false,
  name: <'development' | 'staging' | 'production'> 'development',
  appName: 'Test app',
  defaultCulture: 'nl-NL',
  defaultLanguage: 'nl',
  supportedLanguages: ['nl', 'en'],
  cookieDomain: 'test-app.local',
  baseUrl: 'https://localhost:4200',
  apiBaseUrl: 'https://localhost:5301',
  errorLogging: {
    sentry: {
      errorLoggingEnabled: false,
      tracingEnabled: false,
      options: {
        dsn: 'https://3adb6cb05d1484c7d27a045db81a13b0@o4509869924941824.ingest.de.sentry.io/4509869927891024', //https://<key>@sentry.io/<project>
        // We recommend adjusting this value in production, or using tracesSampler
        // for finer control
        tracesSampleRate: 1.0,
        tracePropagationTargets: ['localhost']
      },
      errorHandlerOptions: {
        showDialog: true
      }
    }
  }
};

/*
 * For easier debugging in development mode, you can import the following file
 * to ignore zone related error stack frames such as `zone.run`, `zoneDelegate.invokeTask`.
 *
 * This import should be commented out in production mode because it will have a negative impact
 * on performance if an error is thrown.
 */
// import 'zone.js/plugins/zone-error';  // Included with Angular CLI.
