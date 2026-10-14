import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandler,
  HttpInterceptor,
  HttpRequest
} from '@angular/common/http';
import {Injectable} from '@angular/core';
import {catchError, Observable, throwError} from 'rxjs';
import {NhCommonModuleConfig} from '../models/config.models';
import {NH_SKIP_SERVER_SESSION_CHECK, NhAuthService} from '../services/nh-auth.service';

/**
 * Ends a session that the NewHeap API no longer accepts. A 401 from the API while the browser
 * still holds a session asks the auth service to check that session with the server. Only a 401
 * from the account endpoint ends it, so an endpoint that answers 401 for another reason never
 * signs the user out. The original error still reaches the caller.
 */
@Injectable()
export class NhSessionRejectionInterceptor implements HttpInterceptor {
  constructor(
    private moduleConfig: NhCommonModuleConfig,
    private authService: NhAuthService
  ) {
  }

  intercept(req: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
    if (!this.appliesTo(req)) {
      return next.handle(req);
    }

    return next.handle(req).pipe(
      catchError((error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 401 && this.authService.isAuthenticated()) {
          void this.authService.verifyServerSession();
        }

        return throwError(() => error);
      })
    );
  }

  private appliesTo(req: HttpRequest<unknown>): boolean {
    if (this.moduleConfig.authentication?.endSessionRejectedByServer === false || req.context.get(NH_SKIP_SERVER_SESSION_CHECK)) {
      return false;
    }

    return this.isUnder(req.url, this.moduleConfig.apiBaseUrl) || this.isUnder(req.url, this.moduleConfig.authApiBaseUrl);
  }

  private isUnder(url: string, baseUrl: string | undefined): boolean {
    return (baseUrl?.length ?? 0) > 0 && url.startsWith(baseUrl!);
  }
}
