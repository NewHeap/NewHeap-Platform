
import { inject } from "@angular/core";
import {Observable} from "rxjs";
import { NhAuthService } from "./nh-auth.service";
import { NhApiService } from "./nh-api.service";
import { NhCommonModuleConfig } from "../models/config.models";
import {CollectionHttpRequestOptions, CollectionHttpResponse, HttpRequestOptions} from "../models/http.models";

export abstract class NhBaseApiService {
  protected authService: NhAuthService = inject(NhAuthService);
  protected moduleConfig: NhCommonModuleConfig = inject(NhCommonModuleConfig);
  protected apiService: NhApiService = inject(NhApiService);

  protected readonly baseUrl: string;

  protected constructor(suffix: string) {
    this.baseUrl = this.moduleConfig.apiBaseUrl + '/' + suffix;
  }

  public getCollection<T>(options: CollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.apiService.getCollection<T>(this.baseUrl, options);
  }

  public get<T>(id: string, requestOptions?: HttpRequestOptions): Observable<T> {
    return this.apiService.get<T>(`${this.baseUrl}/${id}`, requestOptions);
  }

  public create<T>(item: any, requestOptions?: HttpRequestOptions): Observable<T> {
    return this.apiService.post<T>(`${this.baseUrl}`, item, requestOptions);
  }

  public update<T>(id: string, item: any, requestOptions?: HttpRequestOptions): Observable<T> {
    return this.apiService.put<T>(`${this.baseUrl}/${id}`, item, requestOptions);
  }

  public updatePartial<T>(id: string, partialUpdate: any, requestOptions?: HttpRequestOptions): Observable<T> {
    return this.apiService.patch<T>(`${this.baseUrl}/${id}`, partialUpdate, requestOptions);
  }

  public delete(id: string, requestOptions?: HttpRequestOptions): Observable<any> {
    return this.apiService.delete<any>(`${this.baseUrl}/${id}`, requestOptions);
  }
}
