import {
  HttpEvent,
  HttpHandler,
  HttpInterceptor,
  HttpRequest
} from '@angular/common/http';
import {Injectable} from '@angular/core';
import {Observable} from 'rxjs';
import {finalize, shareReplay} from 'rxjs/operators';
import {NhCommonModuleConfig} from '../models/config.models';

@Injectable()
export class NhDeduplicateGetRequestsInterceptor implements HttpInterceptor {
  private activeRequests = new Map<string, Observable<HttpEvent<any>>>();

  constructor(
    private moduleConfig: NhCommonModuleConfig
  ) {
  }

  intercept(req: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
    if (!this.moduleConfig.http?.deduplicateGetRequests || req.method.toUpperCase() !== 'GET') {
      return next.handle(req);
    }

    const requestKey = this.getRequestKey(req);
    const activeRequest = this.activeRequests.get(requestKey);

    if (activeRequest) {
      return activeRequest;
    }

    const request$ = next.handle(req).pipe(
      finalize(() => this.activeRequests.delete(requestKey)),
      shareReplay({bufferSize: 1, refCount: true})
    );

    this.activeRequests.set(requestKey, request$);

    return request$;
  }

  private getRequestKey(req: HttpRequest<any>): string {
    const headerKey = (this.moduleConfig.http?.deduplicateGetRequestHeaderNames ?? [])
      .map(x => x.trim().toLowerCase())
      .filter(x => !!x)
      .sort()
      .map(x => `${x}:${req.headers.getAll(x)?.join(',') ?? ''}`)
      .join('|');

    return `${req.method.toUpperCase()} ${req.urlWithParams} ${headerKey}`;
  }
}
