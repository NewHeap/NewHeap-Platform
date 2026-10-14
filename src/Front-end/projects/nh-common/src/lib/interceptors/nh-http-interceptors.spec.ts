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
import {NhApiAuthInterceptor} from './nh-api.auth.interceptor';
import {NhDeduplicateGetRequestsInterceptor} from './nh-deduplicate-get-requests.interceptor';
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
});
