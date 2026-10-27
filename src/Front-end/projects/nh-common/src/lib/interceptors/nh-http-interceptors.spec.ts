import {
  HttpContext,
  HttpErrorResponse,
  HttpHandler,
  HttpHeaders,
  HttpRequest,
  HttpResponse
} from '@angular/common/http';
import {Subject, of, throwError} from 'rxjs';

import '../prototype-extensions/observable.extensions';
import {NhAuthorization, NhDivision} from '../models/auth.models';
import {
  AuthenticationNhCommonModuleConfig,
  NhCommonModuleConfig,
  NhHttpNhCommonModuleConfig
} from '../models/config.models';
import {NH_SKIP_SERVER_SESSION_CHECK} from '../services/nh-auth.service';
import {NhActiveDivisionInterceptor} from './nh-active-division.interceptor';
import {nhIsRequestToBaseUrl} from './nh-api-base-url';
import {NhApiAuthInterceptor} from './nh-api.auth.interceptor';
import {NhDeduplicateGetRequestsInterceptor} from './nh-deduplicate-get-requests.interceptor';
import {NhServerHttpInterceptor} from './nh-server-http.interceptor';
import {NhSessionRejectionInterceptor} from './nh-session-rejection.interceptor';

describe('NewHeap HTTP interceptors', () => {
  it('reuses an identical GET only while its first request is in flight', () => {
    const interceptor = new NhDeduplicateGetRequestsInterceptor(new NhCommonModuleConfig({
      http: new NhHttpNhCommonModuleConfig({deduplicateGetRequests: true})
    }));
    const response = new Subject<HttpResponse<unknown>>();
    let networkExecutions = 0;
    const handler: HttpHandler = {
      handle: () => {
        networkExecutions++;
        return response;
      }
    };
    const request = new HttpRequest('GET', '/api/projects', null, {
      headers: new HttpHeaders({authorization: 'Bearer token'})
    });

    const first = interceptor.intercept(request, handler);
    const second = interceptor.intercept(request, handler);
    first.subscribe();
    second.subscribe();

    expect(second).toBe(first);
    expect(networkExecutions).toBe(1);

    response.next(new HttpResponse({status: 200}));
    response.complete();
    interceptor.intercept(request, handler);

    expect(networkExecutions).toBe(2);
  });

  it('keeps GET deduplication disabled unless an application opts in', () => {
    const interceptor = new NhDeduplicateGetRequestsInterceptor(new NhCommonModuleConfig());
    let networkExecutions = 0;
    const handler: HttpHandler = {
      handle: () => {
        networkExecutions++;
        return of(new HttpResponse({status: 200}));
      }
    };

    interceptor.intercept(new HttpRequest('GET', '/api/projects'), handler);
    interceptor.intercept(new HttpRequest('GET', '/api/projects'), handler);

    expect(networkExecutions).toBe(2);
  });

  it('does not share in-flight requests across different authorization headers', () => {
    const interceptor = new NhDeduplicateGetRequestsInterceptor(new NhCommonModuleConfig({
      http: new NhHttpNhCommonModuleConfig({deduplicateGetRequests: true})
    }));
    let networkExecutions = 0;
    const responses: Subject<HttpResponse<unknown>>[] = [];
    const handler: HttpHandler = {
      handle: () => {
        networkExecutions++;
        const response = new Subject<HttpResponse<unknown>>();
        responses.push(response);
        return response;
      }
    };

    interceptor.intercept(new HttpRequest('GET', '/api/projects', null, {
      headers: new HttpHeaders({authorization: 'Bearer user-a'})
    }), handler).subscribe();
    interceptor.intercept(new HttpRequest('GET', '/api/projects', null, {
      headers: new HttpHeaders({authorization: 'Bearer user-b'})
    }), handler).subscribe();

    expect(networkExecutions).toBe(2);
    responses.forEach(response => response.complete());
  });

  it('adds auth and division headers for configured API URLs', () => {
    const authorization = new NhAuthorization({
      token: 'sample-token',
      activeDivision: new NhDivision({id: 'division-a'})
    });
    const authService = {getAuthorization: () => authorization} as never;
    const config = new NhCommonModuleConfig({
      apiBaseUrl: '/api',
      authApiBaseUrl: '/authentication'
    });
    const authInterceptor = new NhApiAuthInterceptor(config, authService);
    const divisionInterceptor = new NhActiveDivisionInterceptor(config, authService);
    let handledRequest: HttpRequest<unknown> | undefined;
    const handler: HttpHandler = {
      handle: request => {
        handledRequest = request;
        return of(new HttpResponse({status: 200}));
      }
    };

    authInterceptor.intercept(new HttpRequest('GET', '/api/projects'), {
      handle: request => divisionInterceptor.intercept(request, handler)
    }).subscribe();

    expect(handledRequest?.headers.get('Authorization')).toBe('Bearer sample-token');
    expect(handledRequest?.headers.get('X-NH-ActiveDivisionId')).toBe('division-a');

    authInterceptor.intercept(new HttpRequest('GET', 'https://external.example/projects'), {
      handle: request => divisionInterceptor.intercept(request, handler)
    }).subscribe();

    expect(handledRequest?.headers.has('Authorization')).toBeFalse();
    expect(handledRequest?.headers.has('X-NH-ActiveDivisionId')).toBeFalse();

  });

  it('asks the server about the session after an API request answered 401', () => {
    let authenticated = true;
    let checks = 0;
    const authService = {
      isAuthenticated: () => authenticated,
      verifyServerSession: () => {
        checks++;
        return Promise.resolve(false);
      }
    } as never;
    const config = new NhCommonModuleConfig({apiBaseUrl: '/api', authApiBaseUrl: '/auth-api'});
    const interceptor = new NhSessionRejectionInterceptor(config, authService);
    const respond = (status: number): HttpHandler => ({
      handle: request => throwError(() => new HttpErrorResponse({status, url: request.url}))
    });
    const send = (request: HttpRequest<unknown>, status = 401, target = interceptor) => {
      let failure: unknown;
      target.intercept(request, respond(status)).subscribe({error: error => failure = error});
      return failure as HttpErrorResponse;
    };

    expect(send(new HttpRequest('GET', '/api/projects')).status).toBe(401);
    expect(send(new HttpRequest('GET', '/auth-api/account')).status).toBe(401);
    expect(checks).toBe(2);

    send(new HttpRequest('GET', '/api/projects'), 403);
    send(new HttpRequest('GET', 'https://external.example/projects'));
    send(new HttpRequest('GET', '/api/projects', null, {
      context: new HttpContext().set(NH_SKIP_SERVER_SESSION_CHECK, true)
    }));
    send(new HttpRequest('GET', '/api/projects'), 401, new NhSessionRejectionInterceptor(new NhCommonModuleConfig({
      apiBaseUrl: '/api',
      authentication: new AuthenticationNhCommonModuleConfig({endSessionRejectedByServer: false})
    }), authService));
    authenticated = false;
    send(new HttpRequest('POST', '/auth-api/authentication/login', {}));

    expect(checks).toBe(2);
  });

  describe('API base URL matching', () => {
    const authorization = new NhAuthorization({
      token: 'dummy-token',
      activeDivision: new NhDivision({id: 'division-a'})
    });
    const authService = {getAuthorization: () => authorization} as never;

    const headersSentTo = (url: string, config: NhCommonModuleConfig): HttpHeaders => {
      const authInterceptor = new NhApiAuthInterceptor(config, authService, document);
      const divisionInterceptor = new NhActiveDivisionInterceptor(config, authService, document);
      let handledRequest: HttpRequest<unknown> | undefined;
      const handler: HttpHandler = {
        handle: request => {
          handledRequest = request;
          return of(new HttpResponse({status: 200}));
        }
      };

      authInterceptor.intercept(new HttpRequest('GET', url), {
        handle: request => divisionInterceptor.intercept(request, handler)
      }).subscribe();

      return handledRequest!.headers;
    };

    it('treats only relative URLs as the application origin when no document location is known', () => {
      expect(nhIsRequestToBaseUrl('/projects', '', null)).toBeTrue();
      expect(nhIsRequestToBaseUrl('/api/projects', '/api', null)).toBeTrue();
      expect(nhIsRequestToBaseUrl('https://api.example.com/projects', 'https://api.example.com', null)).toBeTrue();
      expect(nhIsRequestToBaseUrl('https://external.example/projects', '', null)).toBeFalse();
      expect(nhIsRequestToBaseUrl('https://external.example/api/projects', '/api', null)).toBeFalse();
      expect(nhIsRequestToBaseUrl('http://[unparseable/projects', '', document)).toBeFalse();
      expect(nhIsRequestToBaseUrl('/projects', 'http://[unparseable', document)).toBeFalse();
    });

    const expectCredentials = (url: string, config: NhCommonModuleConfig) => {
      const headers = headersSentTo(url, config);

      expect(headers.get('Authorization')).withContext(url).toBe('Bearer dummy-token');
      expect(headers.get('X-NH-ActiveDivisionId')).withContext(url).toBe('division-a');
    };

    const expectNoCredentials = (url: string, config: NhCommonModuleConfig) => {
      const headers = headersSentTo(url, config);

      expect(headers.has('Authorization')).withContext(url).toBeFalse();
      expect(headers.has('X-NH-ActiveDivisionId')).withContext(url).toBeFalse();
    };

    it('does not attach credentials to other origins when the base URLs are empty', () => {
      const config = new NhCommonModuleConfig();

      expectCredentials('/projects', config);
      expectCredentials('projects', config);
      expectCredentials(`${document.location.origin}/projects`, config);

      expectNoCredentials('https://external.example/projects', config);
      expectNoCredentials('//external.example/projects', config);
      expectNoCredentials('https://cdn.example/i18n/en.json', config);
    });

    it('does not attach credentials to look-alike hosts of an absolute base URL', () => {
      const config = new NhCommonModuleConfig({
        apiBaseUrl: 'https://api.example.com',
        authApiBaseUrl: 'https://api.example.com'
      });

      expectCredentials('https://api.example.com/projects', config);
      expectCredentials('https://API.example.com:443/projects', config);

      expectNoCredentials('https://api.example.com.attacker.example/collect', config);
      expectNoCredentials('https://api.example.com@attacker.example/collect', config);
      expectNoCredentials('http://api.example.com/projects', config);
      expectNoCredentials('https://api.example.com:8443/projects', config);
      expectNoCredentials('/projects', config);
      expectNoCredentials('https://external.example/projects', config);
    });

    it('matches a base URL path only at a path-segment boundary', () => {
      const relative = new NhCommonModuleConfig({apiBaseUrl: '/api', authApiBaseUrl: '/api'});

      expectCredentials('/api', relative);
      expectCredentials('/api/projects', relative);
      expectCredentials('/api?language=en', relative);
      expectCredentials(`${document.location.origin}/api/projects`, relative);
      expectNoCredentials('/apiary/projects', relative);
      expectNoCredentials('/api-docs', relative);
      expectNoCredentials('https://external.example/api/projects', relative);
      expectNoCredentials('//external.example/api/projects', relative);

      const versioned = new NhCommonModuleConfig({
        apiBaseUrl: 'https://api.example.com/v1',
        authApiBaseUrl: 'https://api.example.com/v1/'
      });

      expectCredentials('https://api.example.com/v1/projects', versioned);
      expectNoCredentials('https://api.example.com/v10/projects', versioned);
      expectNoCredentials('https://api.example.com/projects', versioned);
    });

    it('adds no access token to auth API requests when addAuthTokensToRequests is false', () => {
      const optedOut = new NhCommonModuleConfig({
        apiBaseUrl: '/api',
        authApiBaseUrl: '/auth-api',
        authentication: new AuthenticationNhCommonModuleConfig({addAuthTokensToRequests: false})
      });

      expect(headersSentTo('/api/projects', optedOut).has('Authorization')).toBeFalse();
      expect(headersSentTo('/auth-api/authentication/logout', optedOut).has('Authorization')).toBeFalse();
      expect(headersSentTo('/api/projects', optedOut).get('X-NH-ActiveDivisionId')).toBe('division-a');

      const optedOutWithEmptyAuthBase = new NhCommonModuleConfig({
        apiBaseUrl: '/api',
        authentication: new AuthenticationNhCommonModuleConfig({addAuthTokensToRequests: false})
      });

      expect(headersSentTo('https://external.example/projects', optedOutWithEmptyAuthBase).has('Authorization')).toBeFalse();
    });

    it('forwards server-side request cookies only to the configured API', () => {
      const forwardedCookie = {key: '.Nh.Authentication', toString: () => '.Nh.Authentication=dummy-session'};
      const relayedCookies: unknown[] = [];
      const cookieService = {
        getRequestServerCookies: () => [{...forwardedCookie}],
        responseRawCookiesHeaderToCookies: () => [{key: '.Nh.Authentication', httpOnly: true}],
        setServerCookies: (cookies: unknown[]) => relayedCookies.push(...cookies)
      } as never;
      const sendFromServer = (url: string, config: NhCommonModuleConfig): HttpRequest<unknown> => {
        const interceptor = new NhServerHttpInterceptor(
          config,
          'server',
          document,
          {didSetCookies: false} as never,
          cookieService,
          {clearAuthorization: () => undefined} as never
        );
        let handledRequest: HttpRequest<unknown> | undefined;
        interceptor.intercept(new HttpRequest('GET', url), {
          handle: request => {
            handledRequest = request;
            return of(new HttpResponse({
              status: 200,
              headers: new HttpHeaders({'Set-Cookie': '.Nh.Authentication=other-origin; HttpOnly'})
            }));
          }
        }).subscribe();

        return handledRequest!;
      };

      expect(sendFromServer('https://external.example/content', new NhCommonModuleConfig()).headers.has('Cookie')).toBeFalse();
      expect(sendFromServer('https://api.example.com.attacker.example/content', new NhCommonModuleConfig({
        apiBaseUrl: 'https://api.example.com'
      })).headers.has('Cookie')).toBeFalse();
      expect(relayedCookies.length).toBe(0);

      const apiRequest = sendFromServer('https://api.example.com/content', new NhCommonModuleConfig({
        apiBaseUrl: 'https://api.example.com'
      }));

      expect(apiRequest.headers.get('Cookie')).toBe('.Nh.Authentication=dummy-session');
      expect(relayedCookies.length).toBe(1);
    });
  });
});
