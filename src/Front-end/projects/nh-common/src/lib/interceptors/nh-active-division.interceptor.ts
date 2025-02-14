import {
  HttpInterceptor,
  HttpRequest,
  HttpHandler,
  HttpParams, HttpEvent
} from '@angular/common/http';
import {Injectable} from '@angular/core';
import {Observable} from 'rxjs';
import {NhApiService, NhAuthService, NhCommonModuleConfig} from "nh-common";

@Injectable()
export class NhActiveDivisionInterceptor implements HttpInterceptor {
  constructor(
    private moduleConfig: NhCommonModuleConfig,
    private authService: NhAuthService
  ) {
  }

  intercept(req: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
    let params = new HttpParams();
    let headers = req.headers;

    if(req.url.startsWith(this.moduleConfig.apiBaseUrl) || req.url.startsWith(this.moduleConfig.authApiBaseUrl)) {
      const authorization = this.authService.getAuthorization();
      if((authorization?.activeDivision?.id?.length ?? 0) > 0) {
        if(!headers.get(NhApiService.ActiveDivisionHeaderKey)) {
          headers = headers.append(NhApiService.ActiveDivisionHeaderKey, authorization?.activeDivision?.id ?? '');
        }
      }
    }

    return next.handle(req.clone({params, headers}));
  }
}
