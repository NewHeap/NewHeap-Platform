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
import { NhApiService } from '../services/nh-api.service';
import { nhIsRequestToBaseUrl } from './nh-api-base-url';

/**
 * Adds the active division header to requests under `apiBaseUrl` or `authApiBaseUrl` (see
 * `nhIsRequestToBaseUrl`). Requests to other origins never receive it.
 */
@Injectable()
export class NhActiveDivisionInterceptor implements HttpInterceptor {
  constructor(
    private moduleConfig: NhCommonModuleConfig,
    private authService: NhAuthService,
    @Optional() @Inject(DOCUMENT) private document?: Document | null
  ) {
  }

  intercept(req: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
    let headers = req.headers;

    if(this.isApiRequest(req.url)) {
      const authorization = this.authService.getAuthorization();
      if((authorization?.activeDivision?.id?.length ?? 0) > 0) {
        if(!headers.get(NhApiService.ActiveDivisionHeaderKey)) {
          headers = headers.append(NhApiService.ActiveDivisionHeaderKey, authorization?.activeDivision?.id ?? '');
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
