import {Injectable} from '@angular/core';
import { User } from '../models/auth.models';
import { NhCommonModuleConfig } from '../models/config.models';
import { NhApiService } from './nh-api.service';
import {UserCollectionHttpRequestOptions, UserMutateModel} from "../models/user.models";
import { CollectionHttpResponse } from '../models/http.models';
import {Observable} from "rxjs";

@Injectable()
export class NhUserService {
  protected baseUrl: string;
  protected apiBaseUrl: string;

  constructor(
    private commonModuleConfig: NhCommonModuleConfig,
    private apiService: NhApiService
  ) {
    this.apiBaseUrl = commonModuleConfig.apiBaseUrl;
    this.baseUrl = commonModuleConfig.apiBaseUrl + '/User';
  }

  public getCollection<T>(options: UserCollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.apiService.getCollection<T>(this.baseUrl, options);
  }

  public get<T>(id: string): Observable<T> {
    return this.apiService.get<T>(this.baseUrl + id);
  }

  public resendRegistrationInvitationEmail(userId: string, registerUrl: string): Observable<any> {
    return this.apiService.put<any>(
      this.baseUrl + '/Registration/Invite/Resend/' + userId,
      {registerUrl: registerUrl}
    );
  }

  public create(item: UserMutateModel): Observable<User> {
    return this.apiService.post<User>(this.baseUrl, item);
  }

  public update(id: string, item: UserMutateModel): Observable<void> {
    return this.apiService.put<void>(this.baseUrl + `/${id}`, item);
  }

  public delete(id: string): Observable<any> {
    return this.apiService.delete<any>(this.baseUrl + `/${id}`);
  }
}
