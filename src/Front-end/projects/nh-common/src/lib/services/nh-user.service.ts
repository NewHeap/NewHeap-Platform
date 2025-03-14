import {Injectable} from '@angular/core';
import {CollectionHttpRequestOptions, CollectionHttpResponse, HttpService} from '../../../core/services/http.service';
import {Observable} from 'rxjs';
import {environment} from '../../../../environments/environment';
import {User, UserCreate, UserProfileUpdate, UserUpdate} from '../models/user.model';

@Injectable()
export class UserService {

  public static getIdentifiableName(user: User): string {
    if (!user) {
      return '';
    }

    const personName = (user && user.person) ? (user.person.fullName + ' ') : '';

    return personName + '(' + user.code + ')' + ' (' + user.email + ')';
  }

  constructor(private httpService: HttpService) {

  }

  public getCollection<T>(options: CollectionHttpRequestOptions): Observable<CollectionHttpResponse<T>> {
    return this.httpService.getCollection<T>(environment.api.baseUrl + '/user', options);
  }

  public get<T>(id: string): Observable<T> {
    return this.httpService.get<T>(environment.api.baseUrl + '/user/' + id);
  }

  public updateProfile<T>(item: UserProfileUpdate): Observable<T> {
    return this.httpService.put<T>(environment.api.baseUrl + '/user/profile/update', item);
  }

  public getProfile<T>(): Observable<T> {
    return this.httpService.get<T>(environment.api.baseUrl + '/user/profile/get');
  }

  public resendRegistrationInvitationEmail(userId: string, registerUrl: string): Observable<any> {
    return this.httpService.put<any>(
      environment.api.baseUrl + '/user/Registration/Invite/Resend/' + userId,
      {registerUrl: registerUrl}
    );
  }

  public create<T>(item: UserCreate): Observable<User> {
    return this.httpService.post<User>(environment.api.baseUrl + '/user', item);
  }

  public update(item: UserUpdate): Observable<User> {
    return this.httpService.put<User>(environment.api.baseUrl + '/user/' + item.id, item);
  }

  public delete(id: string): Observable<any> {
    return this.httpService.delete<any>(environment.api.baseUrl + '/user/' + id);
  }
}
