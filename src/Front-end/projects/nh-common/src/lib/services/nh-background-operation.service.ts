import { HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  NhBackgroundOperation,
  NhBackgroundOperationCollectionHttpRequestOptions
} from '../models/background-operation.models';
import { CollectionHttpResponse, HttpRequestOptions } from '../models/http.models';
import { NhCommonModuleConfig } from '../models/config.models';
import { NhApiService } from './nh-api.service';

@Injectable({ providedIn: 'root' })
export class NhBackgroundOperationService {
  private readonly config = inject(NhCommonModuleConfig);
  private readonly api = inject(NhApiService);
  private readonly baseUrl = this.joinUrl(this.config.apiBaseUrl, this.config.backgroundOperations.urlSuffix);

  list(
    request = new NhBackgroundOperationCollectionHttpRequestOptions({
      page: 1,
      itemsPerPage: this.config.backgroundOperations.listPageSize
    })
  ): Observable<CollectionHttpResponse<NhBackgroundOperation>> {
    return this.api.getCollection<NhBackgroundOperation>(this.baseUrl, request);
  }

  get(operationId: string, eventsAfterSequence?: number): Observable<NhBackgroundOperation> {
    let params = new HttpParams();
    if (eventsAfterSequence !== undefined) {
      params = params.set('eventsAfterSequence', eventsAfterSequence.toString());
    }

    return this.api.get<NhBackgroundOperation>(
      `${this.baseUrl}/${encodeURIComponent(operationId)}`,
      new HttpRequestOptions({ params })
    );
  }

  cancel(operationId: string): Observable<NhBackgroundOperation> {
    return this.api.post<NhBackgroundOperation>(
      `${this.baseUrl}/${encodeURIComponent(operationId)}/cancel`,
      {}
    );
  }

  retry(operationId: string): Observable<NhBackgroundOperation> {
    return this.api.post<NhBackgroundOperation>(
      `${this.baseUrl}/${encodeURIComponent(operationId)}/retry`,
      {}
    );
  }

  private joinUrl(base: string, suffix: string): string {
    return `${base.replace(/\/$/, '')}/${suffix.replace(/^\//, '')}`;
  }
}
