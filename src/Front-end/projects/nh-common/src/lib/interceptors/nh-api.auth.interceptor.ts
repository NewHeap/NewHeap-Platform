import {DOCUMENT} from '@angular/common';
import {
  HttpInterceptor,
  HttpRequest,
  HttpHandler,
  HttpEvent
} from '@angular/common/http';
import {Inject, Injectable, Optional} from '@angular/core';
import {Observable} from 'rxjs';
import { NhCommonModuleConfig } from '../models/config.models';
import { NhAuthService } from '../services/nh-auth.service';
import { nhIsRequestToBaseUrl } from './nh-api-base-url';

/**
 * Adds the access token as a bearer `Authorization` header when `authType` is `'header'`: to
 * requests under `authApiBaseUrl` always, because the library's own logout, impersonation,
 * two-factor and passkey calls rely on it, and to requests under `apiBaseUrl` while
 * `addAuthTokensToRequests` is `true`. Both use `nhIsRequestToBaseUrl`, so requests to other
 * origins never receive the token.
 */
@Injectable()
export class NhApiAuthInterceptor implements HttpInterceptor {
  constructor(
    private moduleConfig: NhCommonModuleConfig,
    private authService: NhAuthService,
    @Optional() @Inject(DOCUMENT) private document?: Document | null
  ) {
  }

  intercept(req: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
    let headers = req.headers;
    if(this.moduleConfig.authType !== 'header') {
      return next.handle(req.clone({headers}));
    }

    if(this.shouldAddToken(req.url)) {
      const authorization = this.authService.getAuthorization();
      if((authorization?.token?.length ?? 0) > 0) {
        if(!headers.get('Authorization')) {
          headers = headers.append('Authorization', `Bearer ${authorization?.token ?? ''}`);
        }
      }
    }

    return next.handle(req.clone({headers}));
  }

  private shouldAddToken(url: string): boolean {
    if (nhIsRequestToBaseUrl(url, this.moduleConfig.authApiBaseUrl, this.document)) {
      return true;
    }

    return this.moduleConfig?.authentication?.addAuthTokensToRequests === true
      && nhIsRequestToBaseUrl(url, this.moduleConfig.apiBaseUrl, this.document);
  }
}
