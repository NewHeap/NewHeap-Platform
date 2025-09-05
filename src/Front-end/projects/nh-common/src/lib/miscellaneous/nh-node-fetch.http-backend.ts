// import {
//   HttpBackend, HttpErrorResponse,
//   HttpEvent,
//   HttpHeaders,
//   HttpRequest, HttpResponse
// } from "@angular/common/http";
// import fetch, { RequestInit, Response, HeadersInit, Headers } from 'node-fetch';
// import {inject, Injectable, NgZone, Provider} from "@angular/core";
// import {Observable, Observer} from "rxjs";
//
// const XSSI_PREFIX = /^\)\]\}',?\n/;
//
// @Injectable()
// export class NhNodeFetchHttpBackend implements HttpBackend  {
//   private readonly ngZone = inject(NgZone);
//
//   handle(req: HttpRequest<any>): Observable<HttpEvent<any>> {
//
//     return new Observable((observer) => {
//       const aborter = new AbortController();
//       this.doRequest(req, aborter.signal, observer).then(noop, (error) =>
//         observer.error(new HttpErrorResponse({error})),
//       );
//       return () => aborter.abort();
//     });
//   }
//
//   private async doRequest(
//     req: HttpRequest<any>,
//     signal: AbortSignal,
//     observer: Observer<HttpEvent<any>>) {
//     const init: RequestInit = {
//       method: req.method,
//       headers: this.getHeaders(req.headers),
//       body: req.body
//     };
//
//     if(init.body) {
//       if(req.headers.get('content-type')?.toLowerCase().includes('application/json')) {
//         init.body = JSON.stringify(init.body);
//       }
//     }
//
//     let response: Response;
//
//     try {
//       response = await this.ngZone.runOutsideAngular(async () => {
//         return await fetch(req.urlWithParams, init);
//       });
//     } catch (error: any) {
//       observer.error(
//         new HttpErrorResponse({
//           error,
//           status: error.status ?? 0,
//           statusText: error.statusText,
//           url: req.urlWithParams,
//           headers: error.headers,
//         }),
//       );
//       return;
//     }
//
//     if(!response) {
//       throw new Error('No response from fetch');
//     }
//
//     const headers = this.createHeaders(response.headers);
//     const body = this.parseBody(req, new Uint8Array(await response.arrayBuffer()), headers.get('content-type')!);
//
//     const ok = response.status >= 200 && response.status < 300;
//
//     if (ok) {
//       observer.next(
//         new HttpResponse({
//           body,
//           headers,
//           status: response.status,
//           statusText: response.statusText,
//           url: req.url,
//         }),
//       );
//
//       // The full body has been received and delivered, no further events
//       // are possible. This request is complete.
//       observer.complete();
//     } else {
//       observer.error(
//         new HttpErrorResponse({
//           error: body,
//           headers,
//           status: response.status,
//           statusText: response.statusText,
//           url: req.url,
//         }),
//       );
//     }
//   }
//
//   private getHeaders(headers: HttpHeaders): HeadersInit | undefined {
//     const result: Headers = new Headers();
//     headers.keys().forEach(key => result.append(key, headers.get(key)!));
//     return result;
//   }
//
//   private createHeaders(headers: Headers): HttpHeaders {
//     const result: { [name: string]: string | string[] } = {};
//
//     for(const [key, value] of headers.entries()) {
//       if(result[key]) {
//         if(Array.isArray(result[key])) {
//           const array = result[key] as string[];
//           array.push(value);
//         } else {
//           result[key] = [result[key] as string, value];
//         }
//       } else {
//         result[key] = value;
//       }
//     }
//     return new HttpHeaders(result);
//   }
//
//   private parseBody(
//     request: HttpRequest<any>,
//     binContent: Uint8Array,
//     contentType: string,
//   ): string | ArrayBuffer | Blob | object | null {
//     switch (request.responseType) {
//       case 'json':
//         // stripping the XSSI when present
//         const text = new TextDecoder().decode(binContent).replace(XSSI_PREFIX, '');
//         return text === '' ? null : (JSON.parse(text) as object);
//       case 'text':
//         return new TextDecoder().decode(binContent);
//       case 'blob':
//         return new Blob([binContent], {type: contentType});
//       case 'arraybuffer':
//         return binContent.buffer;
//     }
//   }
// }
//
// function noop(): void {}
//
// export function nhWithNodeFetchHttpBackend(): Provider {
//   return {
//     provide: HttpBackend,
//     useClass: NhNodeFetchHttpBackend,
//   };
// }
