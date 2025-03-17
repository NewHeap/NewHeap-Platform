import {HttpHeaders, HttpParams} from "@angular/common/http";

export class HttpRequestOptions {
  headers?: HttpHeaders;
  observe?: 'body';
  params?: HttpParams;
  reportProgress?: boolean;
  responseType?: 'json';
  withCredentials?: boolean;

  public constructor(init?: Partial<HttpRequestOptions>) {
    Object.assign(this, init);
  }
}

export class HttpDownloadRequestOptions {
  headers?: HttpHeaders;
  observe?: 'body';
  params?: HttpParams;
  reportProgress?: boolean;
  responseType?: 'blob';
  withCredentials?: boolean;

  public constructor(init?: Partial<HttpRequestOptions>) {
    Object.assign(this, init);
  }
}

export class CollectionHttpRequestOptions extends HttpRequestOptions {
  page: number = 1;
  itemsPerPage: number = 30;
  orderBy: OrderByRequestOptions[] = [];
  filter: FilterRequestOptions[] = [];
  search: string | undefined;

  public constructor(init?: Partial<CollectionHttpRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }
}

export class CollectionHttpResponse<T> {

  page = 1;
  itemsPerPage = 10;
  resultCount: number = 0;
  totalCount: number = 0;
  orderBy: OrderByRequestOptions[] = [];
  filter: FilterRequestOptions[] = [];
  search = '';
  items: T[] = [];

  public constructor(init?: Partial<CollectionHttpResponse<T>>) {
    Object.assign(this, init);
  }
}

export class OrderByRequestOptions {
  key: string = '';
  direction: 'ASC'|'DESC' = 'ASC';

  public constructor(init?: Partial<OrderByRequestOptions>) {
    Object.assign(this, init);
  }
}

export class FilterRequestOptions {
  tag: string | undefined;
  key: string = '';
  value: any;
  operator: string = '==';
  ands: Array<FilterRequestOptions> = [];
  ors: Array<FilterRequestOptions> = [];

  public static mergeToAndFilters(filters: Array<FilterRequestOptions>): FilterRequestOptions|null {
    let mainFilter = null;
    for (const filter of filters) {
      if (!filter.value || filter.value == null || filter.value.length < 1) {
        continue;
      }

      if (mainFilter == null) {
        mainFilter = filter;
      } else {
        mainFilter.ands.push(filter);
      }
    }

    return mainFilter;
  }

  public static mergeToOrFilters(filters: Array<FilterRequestOptions>): FilterRequestOptions|null {
    let mainFilter = null;

    for (const filter of filters) {
      if (!filter.value || filter.value == null || filter.value.length < 1) {
        continue;
      }

      if (mainFilter == null) {
        mainFilter = filter;
      } else {
        mainFilter.ors.push(filter);
      }
    }

    return mainFilter;
  }

  public constructor(init?: Partial<FilterRequestOptions>) {
    Object.assign(this, init);
  }
}
