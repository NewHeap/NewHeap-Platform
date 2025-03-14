import { Injectable } from '@angular/core';
import { CollectionHttpRequestOptions, CollectionHttpResponse, HttpService } from "../../../core/services/http.service";
import { environment } from "../../../../environments/environment";
import { Observable } from "rxjs";
import {TranslateService} from "@ngx-translate/core";

@Injectable()
export class DivisionService {

  public static ENDPOINT_URL = environment.api.baseUrl + '/Division';
  constructor(
    private httpService: HttpService,
    private translateService: TranslateService
  ) {

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
    return this.httpService.getCollection<T>(this.constructor['ENDPOINT_URL'] + '/roles', options);
  }

  public getCollection<T>(options: CollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.httpService.getCollection<T>(this.constructor['ENDPOINT_URL'], options);
  }

  public get<T>(id: string): Observable<T> {
    return this.httpService.get<T>(this.constructor['ENDPOINT_URL'] + '/' + id);
  }

  public getDriveSyncUrl(): Observable<any> {
    return this.httpService.get<any>(this.constructor['ENDPOINT_URL'] + '/OneDriveSyncUrl');
  }

  public create<T>(item: any): Observable<T> {
    return this.httpService.post<T>(this.constructor['ENDPOINT_URL'], item);
  }

  public update<T>(item: any): Observable<T> {
    return this.httpService.put<T>(this.constructor['ENDPOINT_URL'] + '/' + item.id, item);
  }

  public delete(id: string): Observable<any> {
    return this.httpService.delete<any>(this.constructor['ENDPOINT_URL'] + '/' + id);
  }
}
