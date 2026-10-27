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
 * Adds the access token as a bearer `Authorization` header to requests under `apiBaseUrl` or
 * `authApiBaseUrl` (see `nhIsRequestToBaseUrl`), unless `addAuthTokensToRequests` is `false`.
 * Requests to other origins never receive the token.
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

    if(this.moduleConfig?.authentication?.addAuthTokensToRequests === true && this.isApiRequest(req.url)) {
      const authorization = this.authService.getAuthorization();
      if((authorization?.token?.length ?? 0) > 0) {
        if(!headers.get('Authorization')) {
          headers = headers.append('Authorization', `Bearer ${authorization?.token ?? ''}`);
        }
      }
    }

    return next.handle(req.clone({headers}));
  }

  private isApiRequest(url: string): boolean {
    return nhIsRequestToBaseUrl(url, this.moduleConfig.apiBaseUrl, this.document)
      || nhIsRequestToBaseUrl(url, this.moduleConfig.authApiBaseUrl, this.document);
  }
}
