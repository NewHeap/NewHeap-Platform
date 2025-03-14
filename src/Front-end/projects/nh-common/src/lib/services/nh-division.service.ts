import { Injectable } from '@angular/core';
import { Observable } from "rxjs";
import {TranslateService} from "@ngx-translate/core";
import { NhCommonModuleConfig } from '../models/config.models';
import { NhApiService } from './nh-api.service';
import { CollectionHttpRequestOptions, CollectionHttpResponse } from '../models/http.models';
import {DivisionCollectionRequestModel} from "../models/division.models";

@Injectable()
export class NhDivisionService {

  protected baseUrl: string;
  protected apiBaseUrl: string;

  constructor(
    private commonModuleConfig: NhCommonModuleConfig,
    private apiService: NhApiService,
    private translateService: TranslateService
  ) {
    this.apiBaseUrl = commonModuleConfig.apiBaseUrl;
    this.baseUrl = commonModuleConfig.apiBaseUrl + '/Division';
  }

  public getTimeZones(translationPrefix = 'division.time-zones.') {
    const result = [
      {id: 'W. Europe Standard Time', name: ''},
    ];

    for (const item of result) {
      item.name = this.translateService.instant(translationPrefix + item.id);
    }

    return result;
  }

  public roleGetCollection<T>(options: CollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.apiService.getCollection<T>(this.baseUrl + '/roles', options);
  }

  public getCollection<T>(options: DivisionCollectionRequestModel): Observable<CollectionHttpResponse<T>> {
    return this.apiService.getCollection<T>(this.baseUrl, options);
  }

  public get<T>(id: string): Observable<T> {
    return this.apiService.get<T>(this.baseUrl + '/' + id);
  }

  public create<T>(item: any): Observable<T> {
    return this.apiService.post<T>(this.baseUrl, item);
  }

  public update<T>(item: any): Observable<T> {
    return this.apiService.put<T>(this.baseUrl + '/' + item.id, item);
  }

  public delete(id: string): Observable<any> {
    return this.apiService.delete<any>(this.baseUrl + '/' + id);
  }
}
