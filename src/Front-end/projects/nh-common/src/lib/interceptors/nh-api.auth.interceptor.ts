import {
  HttpInterceptor,
  HttpRequest,
  HttpHandler,
  HttpParams, HttpEvent
} from '@angular/common/http';
import {Injectable} from '@angular/core';
import {Observable} from 'rxjs';
import { NhCommonModuleConfig } from '../models/config.models';
import { NhAuthService } from '../services/nh-auth.service';
import { NhApiService } from '../services/nh-api.service';

@Injectable()
export class NhApiAuthInterceptor implements HttpInterceptor {
  constructor(
    private moduleConfig: NhCommonModuleConfig,
    private authService: NhAuthService
  ) {
  }

  intercept(req: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
    let headers = req.headers;

    if(this.moduleConfig?.authentication?.addAuthTokensToRequests === true && req.url.startsWith(this.moduleConfig.apiBaseUrl) || req.url.startsWith(this.moduleConfig.authApiBaseUrl)) {
      const authorization = this.authService.getAuthorization();
      if((authorization?.token?.length ?? 0) > 0) {
        if(!headers.get('Authorization')) {
          headers = headers.append('Authorization', `Bearer ${authorization?.token ?? ''}`);
        }
      }
    }

    return next.handle(req.clone({headers}));
  }
}
