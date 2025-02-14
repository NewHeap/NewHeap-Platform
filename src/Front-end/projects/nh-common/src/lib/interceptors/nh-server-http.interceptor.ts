import {
  HttpInterceptor,
  HttpRequest,
  HttpEvent,
  HttpHandler,
  HttpParams,
  HttpParameterCodec, HttpResponse
} from '@angular/common/http';
import {Inject, Injectable, Optional, PLATFORM_ID, REQUEST_CONTEXT} from '@angular/core';
import {Observable, tap} from 'rxjs';
import {DOCUMENT, isPlatformServer} from "@angular/common";
import { Cookie } from 'tough-cookie';
import {NhCommonConfig, NhCommonModuleConfig} from "../models/config.models";
import {NhCookieService} from "../services/nh-cookie.service";
import {NhAuthService} from "../services/nh-auth.service";

@Injectable({
  providedIn: 'root'
})
export class NhRequestScopedServerHttpInterceptorService {
  didSetCookies = false;

  constructor(
    private moduleConfig: NhCommonModuleConfig,
    @Inject(PLATFORM_ID) private platformId: Object,
    @Optional() @Inject(REQUEST_CONTEXT) private requestContext: any
  ) {

  }
}

@Injectable()
export class NhServerHttpInterceptor implements HttpInterceptor {
  constructor(
    private moduleConfig: NhCommonModuleConfig,
    @Inject(PLATFORM_ID) private platformId: Object,
    @Inject(DOCUMENT) private document: Document,
    private requestScopedServerHttpInterceptorService: NhRequestScopedServerHttpInterceptorService,
    private cookieService: NhCookieService,
    private authService: NhAuthService
  ) {

  }

  intercept(req: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
    const normalizedReqUrl = req.url?.trim()?.toLowerCase() ?? '';
    const isApiRequest = normalizedReqUrl.startsWith(this.moduleConfig.apiBaseUrl);

    const handleExpiredHeaderDetected = (event: HttpResponse<any>) => {
      if (event.headers.has('X-Expired-Authentication')) {
        console.log('X-Expired-Authentication header detected');
        this.authService.clearAuthorization();
      }
    };

    if (isApiRequest && isPlatformServer(this.platformId)) {

      const requestCookies = this.cookieService
        .getRequestServerCookies()
        .filter(x => x.key === `${NhCookieService.HTTP_COOKIE_PREFIX}${NhCookieService.NOP_COOKIE_AUTHENTICATION}`
          || x.key === `${NhCookieService.HTTP_COOKIE_PREFIX}${NhCookieService.NOP_COOKIE_CUSTOMER}`
          || x.key === `${NhCookieService.HTTP_COOKIE_PREFIX}${NhCookieService.Nh_COOKIE_AUTHENTICATION}`)
      ;

      for (const cookie of requestCookies) {
        cookie.key = cookie.key.replace(NhCookieService.HTTP_COOKIE_PREFIX, '');
      }

      const requestCookieString = requestCookies.map(x => x.toString()).join('; ') ?? '';
      const serverReq = req.clone({
        setHeaders: {
          Cookie: requestCookieString
        },
      });

      return next.handle(serverReq).pipe(
        tap(async event => {
          if (event instanceof HttpResponse) {

            handleExpiredHeaderDetected(event);

            if (this.requestScopedServerHttpInterceptorService.didSetCookies) {
              return;
            }

            const rawSetCookieHeader = event?.headers?.get('Set-Cookie')?.trim() ?? '';

            if ((rawSetCookieHeader?.length ?? 0) > 0) {
              const cookies = this.cookieService.responseRawCookiesHeaderToCookies(rawSetCookieHeader);
              const nopAuthenticationCookie = cookies.find(x => x?.key === NhCookieService.NOP_COOKIE_AUTHENTICATION && x.httpOnly);
              const nopCustomerCookie = cookies.find(x => x?.key === NhCookieService.NOP_COOKIE_CUSTOMER && x.httpOnly);
              const nhAuthenticationCookie = cookies.find(x => x?.key === NhCookieService.Nh_COOKIE_AUTHENTICATION && x.httpOnly);

              const serverCookies: Cookie[] = [];

              if (nopAuthenticationCookie) {
                serverCookies.push(nopAuthenticationCookie);
              }

              if (nopCustomerCookie) {
                serverCookies.push(nopCustomerCookie);
              }

              if (nhAuthenticationCookie) {
                serverCookies.push(nhAuthenticationCookie);
              }

              for (const serverCookie of serverCookies) {
                serverCookie.domain = this.moduleConfig.cookieDomain;
              }

              this.cookieService.setServerCookies(serverCookies);
              this.requestScopedServerHttpInterceptorService.didSetCookies = true;
            }
          }
        })
      );
    }

    return next.handle(req).pipe(
      tap(async event => {
        if (event instanceof HttpResponse) {
          handleExpiredHeaderDetected(event);
        }
      })
    );
  }
}
