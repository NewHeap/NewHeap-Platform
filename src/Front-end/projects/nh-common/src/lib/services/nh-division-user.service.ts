import { Injectable } from '@angular/core';
import { Observable } from "rxjs";
import { NhCommonModuleConfig } from '../models/config.models';
import { NhApiService } from './nh-api.service';
import {CollectionHttpRequestOptions, CollectionHttpResponse} from '../models/http.models';
import {UserCollectionHttpRequestOptions} from "../models/user.models";
import {
  DivisionCollectionRequestModel,
  DivisionUserCollectionRequestModel
} from "../models/division.models";
import { DivisionUser } from '../models/auth.models';

@Injectable()
export class NhDivisionUserService {

  protected baseUrl: string;
  protected apiBaseUrl: string;

  constructor(
    private commonModuleConfig: NhCommonModuleConfig,
    private apiService: NhApiService
  ) {
    this.apiBaseUrl = commonModuleConfig.apiBaseUrl;
    this.baseUrl = commonModuleConfig.apiBaseUrl + '/DivisionUser';
  }

  public userGetCollection<T>(options: UserCollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.apiService.getCollection<T>(this.apiBaseUrl + '/user', options);
  }

  public divisionGetCollection<T>(options: DivisionCollectionRequestModel): Observable<CollectionHttpResponse<T>> {
    return this.apiService.getCollection<T>(this.apiBaseUrl + '/division', options);
  }

  public roleGetCollection<T>(options: CollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.apiService.getCollection<T>(this.apiBaseUrl + '/division/roles', options);
  }

  public getCollection<T>(options: DivisionUserCollectionRequestModel): Observable<CollectionHttpResponse<T>> {
    return this.apiService.getCollection<T>(this.baseUrl, options);
  }

  public get<T>(id: string): Observable<T> {
    return this.apiService.get<T>(this.baseUrl + '/' + id);
  }

  public create<T>(item: DivisionUser): Observable<T> {
    return this.apiService.post<T>(this.baseUrl, item);
  }

  public update<T>(item: DivisionUser): Observable<T> {
    return this.apiService.put<T>(this.baseUrl + '/' + item.id, item);
  }

  public delete(id: string): Observable<any> {
    return this.apiService.delete<any>(this.baseUrl + '/' + id);
  }
}
