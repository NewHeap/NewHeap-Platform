import { HttpClient, HttpEvent, HttpHeaders, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, OnDestroy } from '@angular/core';
import {lastValueFrom, map, Observable, share, Subscription} from 'rxjs';
import {
  CollectionHttpRequestOptions,
  CollectionHttpResponse,
  HttpDownloadRequestOptions,
  HttpRequestOptions,
  ICollectionHttpRequestOptions, IHttpDownloadRequestOptions, IHttpRequestOptions,
  ISimpleCollectionHttpRequestOptions, SimpleCollectionHttpRequestOptions, SimpleCollectionHttpResponse
} from '../models/http.models';
import { NhApiUtil } from '../util/nh-api-util';
import { NhAuthService } from './nh-auth.service';
import {NhCommonModuleConfig} from "../models/config.models";
import {NhAuthorization} from "../models/auth.models";
import {TaskResult} from "../models/misc.models";
import {decode as msgpackDecode} from "@msgpack/msgpack";

@Injectable({
  providedIn: 'root',
})
export class NhApiService implements OnDestroy {
  authSubscription: Subscription;
  authorization: NhAuthorization|undefined;
  baseUrl: string|undefined;

  public static ActiveDivisionHeaderKey = 'X-NH-ActiveDivisionId';

  private static defaultHeaders: HttpHeaders = new HttpHeaders({
    'Content-Type': 'application/json; charset=utf-8',
    'Accept': 'application/json; charset=utf-8',
  });

  private static skipObjectParamParseKeys: string[] = [
    'headers',
    'params',
    'observe',
    'reportProgress',
    'responseType',
    'withCredentials'
  ];

  constructor(
    private moduleConfig: NhCommonModuleConfig,
    private httpClient: HttpClient,
    private authService: NhAuthService
  ) {
    this.baseUrl = this.moduleConfig.apiBaseUrl;
    this.authSubscription = this.authService.authSubject.subscribe(async (authorization) => {
      await this.authChanged(authorization);
    });
  }

  private async authChanged(authorization: NhAuthorization|undefined) {
    this.authorization = authorization;
  }

  ngOnDestroy(): void {
    if(this.authSubscription) {
      this.authSubscription.unsubscribe();
    }
  }

  public static taskResultFromResponse(response: any): TaskResult<any> {
    return NhApiUtil.taskResultFromResponse(response);
  }

  public prepareHeaders(httpHeaders?: HttpHeaders): HttpHeaders {

    httpHeaders = httpHeaders || new HttpHeaders();

    let header = 'Content-Type';
    if (!httpHeaders.has(header) && NhApiService.defaultHeaders.has(header)) {
      httpHeaders = httpHeaders.set(header, NhApiService.defaultHeaders.get(header) || '');
    } else if (httpHeaders.has(header) && (httpHeaders.get(header)) === '') {
      httpHeaders = httpHeaders.delete(header);
    }

    header = 'Accept';
    if (!httpHeaders.has(header) && NhApiService.defaultHeaders.has(header)) {
      httpHeaders = httpHeaders.set(header, NhApiService.defaultHeaders.get(header) || '');
    }

    return httpHeaders;
  }

  public setCultureHttpParam(httpParams?: HttpParams): HttpParams {
    httpParams = httpParams || new HttpParams();

    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    httpParams = httpParams.set('culture', this.moduleConfig.culture);

    return httpParams;
  }

  // TODO: dit is een front-end API functie voor non management requesten
  public setObjectParams(httpParams: HttpParams, requestOptions?: HttpRequestOptions|HttpDownloadRequestOptions): HttpParams {
    const customPropertyNames = Object.getOwnPropertyNames(requestOptions).filter(x => x).filter(x => !NhApiService.skipObjectParamParseKeys.find(c => c === x));
    for (const propertyName of customPropertyNames) {
      if (!httpParams.get(propertyName)) {
        const propertyValue = (requestOptions as any)[propertyName];
        if (Array.isArray(propertyValue)) {
          for (let i = 0; i < propertyValue.length; i++) {
            const arrayValue = propertyValue[i];
            httpParams = httpParams.set(propertyName + '[' + i + ']', arrayValue);
          }
        } else {
          httpParams = httpParams.set(propertyName, propertyValue);
        }
      }
    }
    return httpParams;
  }

  public async task<T>(observable$: Observable<T>): Promise<TaskResult<T>> {
    const taskResult = new TaskResult<T>();

    try {
      taskResult.data = await lastValueFrom(observable$);
    }catch (ex) {
      const errResult = NhApiService.taskResultFromResponse(ex);
      errResult.copyTo(taskResult);
    }

    return taskResult;
  }

  public get<T>(url: string, requestOptions?: IHttpRequestOptions): Observable<T> {
    requestOptions = requestOptions || new HttpRequestOptions();
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.get<T>(url, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'json',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public getText(url: string, requestOptions?: IHttpRequestOptions): Observable<string> {
    requestOptions = requestOptions || new HttpRequestOptions();
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.get(url, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'text',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public getResult<T>(url: string,  requestOptions?: IHttpRequestOptions): Promise<TaskResult<T>> {
    return this.task(this.get<T>(url, requestOptions));
  }

  public getCollection<T>(url: string, requestOptions?: ICollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.getCollectionT<T, ICollectionHttpRequestOptions, CollectionHttpResponse<T>>(url, requestOptions);
  }

  public getCollectionT<T, TRequestOptions extends ICollectionHttpRequestOptions, TCollectionResult extends CollectionHttpResponse<T>>(
    url: string,
    requestOptions?: TRequestOptions
  ): Observable<TCollectionResult>
  {
    requestOptions = requestOptions || <TRequestOptions><unknown>(new CollectionHttpRequestOptions());
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    httpParams = httpParams.set('page', requestOptions.page);
    httpParams = httpParams.set('itemsPerPage', requestOptions.itemsPerPage);

    if (null != requestOptions.orderBy && requestOptions.orderBy.length > 0) {
      httpParams = httpParams.set('orderBy', JSON.stringify(requestOptions.orderBy));
    }

    if (null != requestOptions.filter && requestOptions.filter.length > 0) {
      httpParams = httpParams.set('filter', JSON.stringify(requestOptions.filter));
    }

    if ((requestOptions.search?.length ?? 0) > 0) {
      httpParams = httpParams.set('search', requestOptions.search ?? '');
    }

    const customPropertyNames = Object.getOwnPropertyNames(requestOptions).filter(x => x);
    for (const propertyName of customPropertyNames) {
      if(NhApiService.skipObjectParamParseKeys.indexOf(propertyName) > -1) {
        continue;
      }
      if (!httpParams.get(propertyName)) {
        const propertyValue = (<any>requestOptions)[propertyName];
        if (Array.isArray(propertyValue)) {
          for (let i = 0; i < propertyValue.length; i++) {
            const arrayValue = propertyValue[i];
            if(arrayValue === undefined) {
              continue;
            }
            httpParams = httpParams.set(propertyName + '[' + i + ']', arrayValue);
          }
        } else {
          if(propertyValue === undefined) {
            continue;
          }
          httpParams = httpParams.set(propertyName, propertyValue);
        }
      }
    }

    switch (requestOptions.responseType) {
      case 'arraybuffer':
        return this.httpClient.get(url, {
          headers: headers,
          observe: 'body',
          params: httpParams,
          reportProgress: requestOptions.reportProgress,
          responseType: 'arraybuffer',
          withCredentials: requestOptions.withCredentials
        })
          .pipe(map(r => msgpackDecode(r) as TCollectionResult))
          .pipe(share());
      default:
        return this.httpClient.get<TCollectionResult>(url, {
          headers: headers,
          observe: 'body',
          params: httpParams,
          reportProgress: requestOptions.reportProgress,
          responseType: 'json',
          withCredentials: requestOptions.withCredentials
        }).pipe(share());
    }
  }

  public getSimpleCollection<T>(url: string, requestOptions?: ISimpleCollectionHttpRequestOptions): Observable<SimpleCollectionHttpResponse<T>> {
    return this.getSimpleCollectionT<T, ISimpleCollectionHttpRequestOptions, SimpleCollectionHttpResponse<T>>(url, requestOptions);
  }

  public getSimpleCollectionT<T, TRequestOptions extends ISimpleCollectionHttpRequestOptions, TCollectionResult extends SimpleCollectionHttpResponse<T>>(url: string, requestOptions?: TRequestOptions): Observable<TCollectionResult> {
    requestOptions = requestOptions || <TRequestOptions><unknown>(new CollectionHttpRequestOptions());
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    httpParams = httpParams.set('page', requestOptions.page);
    httpParams = httpParams.set('itemsPerPage', requestOptions.itemsPerPage);

    return this.httpClient.get<TCollectionResult>(url, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'json',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public download(url: string, requestOptions?: IHttpDownloadRequestOptions): Observable<Blob> {
    requestOptions = requestOptions || new HttpDownloadRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.get(url, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'blob',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public downloadResponse(url: string, requestOptions?: IHttpDownloadRequestOptions): Observable<HttpResponse<Blob>> {
    requestOptions = requestOptions || new HttpDownloadRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.get(url, {
      headers: headers,
      observe: 'response',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'blob',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public postDownload(url: string, body: any, requestOptions?: IHttpDownloadRequestOptions): Observable<Blob> {
    requestOptions = requestOptions || new HttpDownloadRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.post(url, body, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'blob',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public postDownloadResponse(url: string, body: any, requestOptions?: IHttpDownloadRequestOptions): Observable<HttpResponse<Blob>> {
    requestOptions = requestOptions || new HttpDownloadRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.post(url, body, {
      headers: headers,
      observe: 'response',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'blob',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public putDownload(url: string, body: any, requestOptions?: IHttpDownloadRequestOptions): Observable<Blob> {
    requestOptions = requestOptions || new HttpDownloadRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.put(url, body, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'blob',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public putDownloadResponse(url: string, body: any, requestOptions?: IHttpDownloadRequestOptions): Observable<HttpResponse<Blob>> {
    requestOptions = requestOptions || new HttpDownloadRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.put(url, body, {
      headers: headers,
      observe: 'response',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'blob',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public delete<T>(url: string, requestOptions?: IHttpRequestOptions): Observable<T> {
    requestOptions = requestOptions || new HttpRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.delete<T>(url, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'json',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public async deleteResult<T>(url: string,  requestOptions?: HttpRequestOptions): Promise<TaskResult<T>> {
    return this.task(this.delete<T>(url, requestOptions));
  }

  public deleteWithBody<T>(url: string, body: any, requestOptions?: IHttpRequestOptions): Observable<T> {
    requestOptions = requestOptions || new HttpRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.delete<T>(url, {
      body: body,
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'json',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }


  public post<T>(url: string, body: any, requestOptions?: IHttpRequestOptions): Observable<T> {
    requestOptions = requestOptions || new HttpRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.post<T>(url, body, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'json',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public async postResult<T>(url: string, body: any, requestOptions?: IHttpRequestOptions): Promise<TaskResult<T>> {
    return this.task(this.post<T>(url, body,requestOptions));
  }

  public put<T>(url: string, body: any, requestOptions?: IHttpRequestOptions): Observable<T> {
    requestOptions = requestOptions || new HttpRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.put<T>(url, body, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'json',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public async putResult<T>(url: string, body: any, requestOptions?: IHttpRequestOptions): Promise<TaskResult<T>> {
    return this.task(this.put<T>(url, body,requestOptions));
  }

  public patch<T>(url: string, body: any, requestOptions?: IHttpRequestOptions): Observable<T> {
    requestOptions = requestOptions || new HttpRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);
    //httpParams = this.setObjectParams(httpParams, requestOptions);

    return this.httpClient.patch<T>(url, body, {
      headers: headers,
      observe: 'body',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'json',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }

  public async patchResult<T>(url: string, body: any, requestOptions?: IHttpRequestOptions): Promise<TaskResult<T>> {
    return this.task(this.patch<T>(url, body,requestOptions));
  }

  public postHttpEvent<T>(url: string, body: any, requestOptions?: IHttpRequestOptions): Observable<HttpEvent<T>> {
    requestOptions = requestOptions || new HttpRequestOptions();
    requestOptions.withCredentials = true;
    let httpParams = requestOptions.params || new HttpParams();
    let headers = requestOptions.headers || new HttpHeaders();

    if(requestOptions.withCredentials === undefined) {
      requestOptions.withCredentials = true;
    }

    headers = this.prepareHeaders(headers);
    httpParams = this.setCultureHttpParam(httpParams);

    return this.httpClient.post<T>(url, body, {
      headers: headers,
      observe: 'events',
      params: httpParams,
      reportProgress: requestOptions.reportProgress,
      responseType: 'json',
      withCredentials: requestOptions.withCredentials
    }).pipe(share());
  }
}
