import {inject, Injectable} from '@angular/core';
import {Observable} from "rxjs";
import {NhApiService, NhCommonModuleConfig} from "nh-common";

export class ChangePasswordUserMutateModel {
  currentPassword: string = '';
  password: string = '';
  confirmPassword: string = '';

  public constructor(init?: Partial<ChangePasswordUserMutateModel>) {
    Object.assign(this, init);
  }
}

@Injectable({
  providedIn: 'root'
})
export class AccountService {
  protected apiService: NhApiService = inject(NhApiService);
  protected moduleConfig: NhCommonModuleConfig = inject(NhCommonModuleConfig);
  protected readonly baseUrl: string;

  constructor() {
    this.baseUrl = `${this.moduleConfig.apiBaseUrl}/account`;
  }

  passwordChange(item: ChangePasswordUserMutateModel): Observable<any> {
    return this.apiService.post<any>(`${this.baseUrl}/password/change`, item);
  }
}
