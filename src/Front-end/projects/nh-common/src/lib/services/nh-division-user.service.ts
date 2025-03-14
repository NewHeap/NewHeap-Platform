import { Injectable } from '@angular/core';
import { CollectionHttpRequestOptions, CollectionHttpResponse, HttpService } from "../../../core/services/http.service";
import { environment } from "../../../../environments/environment";
import { Observable } from "rxjs";
import {UserCollectionHttpRequestOptions} from "../../user/models/user.model";
import {DivisionUser} from "../../division/models/division.model";

@Injectable()
export class DivisionUserService {

  public static ENDPOINT_URL = environment.api.baseUrl + '/DivisionUser';

  constructor(
    private httpService: HttpService
  ) {

  }

  public userGetCollection<T>(options: UserCollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.httpService.getCollection<T>(environment.api.baseUrl + '/user', options);
  }

  public divisionGetCollection<T>(options: CollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.httpService.getCollection<T>(environment.api.baseUrl + '/division', options);
  }

  public roleGetCollection<T>(options: CollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.httpService.getCollection<T>(environment.api.baseUrl + '/division/roles', options);
  }

  public getCollection<T>(options: CollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.httpService.getCollection<T>(this.constructor['ENDPOINT_URL'], options);
  }

  public get<T>(id: string): Observable<T> {
    return this.httpService.get<T>(this.constructor['ENDPOINT_URL'] + '/' + id);
  }

  public create<T>(item: DivisionUser): Observable<T> {
    return this.httpService.post<T>(this.constructor['ENDPOINT_URL'], item);
  }

  public update<T>(item: DivisionUser): Observable<T> {
    return this.httpService.put<T>(this.constructor['ENDPOINT_URL'] + '/' + item.id, item);
  }

  public delete(id: string): Observable<any> {
    return this.httpService.delete<any>(this.constructor['ENDPOINT_URL'] + '/' + id);
  }
}
