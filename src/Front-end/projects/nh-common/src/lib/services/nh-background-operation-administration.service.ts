import { HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  NhBackgroundOperationAdministration,
  NhBackgroundOperationAdministrationCollectionHttpRequestOptions
} from '../models/background-operation.models';
import { CollectionHttpResponse, HttpRequestOptions } from '../models/http.models';
import { NhCommonModuleConfig } from '../models/config.models';
import { NhApiService } from './nh-api.service';

/**
 * Client for the cross-owner background-operation administration endpoints. The
 * server enables them with `UseAdministrationPolicy` and authorizes every request
 * against that policy; the active division scopes the visible operations. The
 * administration view does not receive owner-scoped live updates, so refresh it
 * by polling.
 */
@Injectable({ providedIn: 'root' })
export class NhBackgroundOperationAdministrationService {
  private readonly config = inject(NhCommonModuleConfig);
  private readonly api = inject(NhApiService);
  private readonly baseUrl = this.joinUrl(
    this.config.apiBaseUrl,
    this.config.backgroundOperations.administrationUrlSuffix
  );

  list(
    request = new NhBackgroundOperationAdministrationCollectionHttpRequestOptions({
      page: 1,
      itemsPerPage: this.config.backgroundOperations.listPageSize
    })
  ): Observable<CollectionHttpResponse<NhBackgroundOperationAdministration>> {
    return this.api.getCollection<NhBackgroundOperationAdministration>(this.baseUrl, request);
  }

  get(operationId: string, eventsAfterSequence?: number): Observable<NhBackgroundOperationAdministration> {
    let params = new HttpParams();
    if (eventsAfterSequence !== undefined) {
      params = params.set('eventsAfterSequence', eventsAfterSequence.toString());
    }

    return this.api.get<NhBackgroundOperationAdministration>(
      `${this.baseUrl}/${encodeURIComponent(operationId)}`,
      new HttpRequestOptions({ params })
    );
  }

  cancel(operationId: string): Observable<NhBackgroundOperationAdministration> {
    return this.api.post<NhBackgroundOperationAdministration>(
      `${this.baseUrl}/${encodeURIComponent(operationId)}/cancel`,
      {}
    );
  }

  retry(operationId: string): Observable<NhBackgroundOperationAdministration> {
    return this.api.post<NhBackgroundOperationAdministration>(
      `${this.baseUrl}/${encodeURIComponent(operationId)}/retry`,
      {}
    );
  }

  private joinUrl(base: string, suffix: string): string {
    return `${base.replace(/\/$/, '')}/${suffix.replace(/^\//, '')}`;
  }
}
