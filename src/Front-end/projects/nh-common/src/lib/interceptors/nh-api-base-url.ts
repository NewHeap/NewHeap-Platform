/**
 * The address that relative URLs resolve against when the document has no http(s) location, for
 * example during a server render without a request URL. The `.invalid` domain is reserved and never
 * resolves, so only relative URLs share its origin.
 */
const NH_UNKNOWN_APPLICATION_URL = 'https://application.invalid/';

/**
 * Decides whether a request goes to the API at a configured base URL, before an interceptor adds
 * the access token, the active division or forwarded session cookies to it.
 *
 * The request URL and the base URL are resolved against the document the way the browser resolves
 * them. A request matches when it has the base URL's origin (scheme, host and port) and its path
 * equals the base path or continues it after a `/`; letter case in the path is ignored, as ASP.NET
 * Core routing ignores it. An empty base URL stands for the application's own origin: it matches
 * relative and same-origin requests, never another origin. A URL that cannot be parsed never
 * matches.
 */
export function nhIsRequestToBaseUrl(
  requestUrl: string | null | undefined,
  baseUrl: string | null | undefined,
  document?: Document | null
): boolean {
  const application = resolveApplicationUrls(document);
  const target = parseUrl(requestUrl?.trim() ?? '', application.resolutionBase);

  if (!target) {
    return false;
  }

  const configuredBaseUrl = baseUrl?.trim() ?? '';

  if (configuredBaseUrl.length === 0) {
    return target.origin === application.origin;
  }

  const base = parseUrl(configuredBaseUrl, application.resolutionBase);

  if (!base || target.origin !== base.origin) {
    return false;
  }

  const basePath = base.pathname.replace(/\/+$/, '').toLowerCase();
  const targetPath = target.pathname.toLowerCase();

  return basePath.length === 0 || targetPath === basePath || targetPath.startsWith(`${basePath}/`);
}

function resolveApplicationUrls(document: Document | null | undefined): { origin: string; resolutionBase: string } {
  const location = parseHttpUrl(document?.location?.href);

  if (!location) {
    return {
      origin: new URL(NH_UNKNOWN_APPLICATION_URL).origin,
      resolutionBase: NH_UNKNOWN_APPLICATION_URL
    };
  }

  const documentBase = parseHttpUrl(document?.baseURI);

  return {
    origin: location.origin,
    resolutionBase: (documentBase ?? location).href
  };
}

function parseHttpUrl(url: string | null | undefined): URL | null {
  const parsed = parseUrl(url ?? '');

  if (!parsed || (parsed.protocol !== 'http:' && parsed.protocol !== 'https:')) {
    return null;
  }

  return parsed;
}

function parseUrl(url: string, base?: string): URL | null {
  if (url.length === 0 && base === undefined) {
    return null;
  }

  try {
    return new URL(url, base);
  } catch {
    return null;
  }
}
