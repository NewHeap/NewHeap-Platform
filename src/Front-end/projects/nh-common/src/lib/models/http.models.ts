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

export class SimpleCollectionHttpRequestOptions extends HttpRequestOptions {
  page: number = 1;
  itemsPerPage: number = 30;

  public constructor(init?: Partial<SimpleCollectionHttpRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }
}

export class SearchableCollectionHttpRequestOptions extends SimpleCollectionHttpRequestOptions {
  search: string | undefined;

  public constructor(init?: Partial<SearchableCollectionHttpRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }
}

export class CollectionHttpRequestOptions extends SearchableCollectionHttpRequestOptions {
  orderBy: OrderByRequestOptions[] = [];
  filter: FilterRequestOptions[] = [];

  public constructor(init?: Partial<CollectionHttpRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }

  public or(condition: FilterRequestOptions) : this {
    if(this.filter.length == 0) {
      this.filter = [condition];
      return this;
    }

    if(this.filter.length == 1) {
      this.filter[0].ors.push(condition);
    } else {
      this.filter[0]
        .andArray(this.filter.slice(1))
        .or(condition)
      ;
    }
    return this;
  }

  public and(condition: FilterRequestOptions) : this {
    this.filter.push(condition);
    return this;
  }

  public equals(key: string, value: any) : this {
    this.and(FilterRequestOptions.equals(key, value));
    return this;
  }
  public notEquals(key: string, value: any) : this {
    this.and(FilterRequestOptions.notEquals(key, value));
    return this;
  }

  public isIn(key: string, value: any[]) : this {
    this.and(FilterRequestOptions.in(key, value));
    return this;
  }

  public isNotIn(key: string, value: any[]) : this {
    this.and(FilterRequestOptions.notIn(key, value));
    return this;
  }

  public lessThan(key: string, value: any) : this {
    this.and(FilterRequestOptions.lessThan(key, value));
    return this;
  }
  public lessThanOrEqual(key: string, value: any) : this {
    this.and(FilterRequestOptions.lessThanOrEqual(key, value));
    return this;
  }

  public greaterThan(key: string, value: any) : this {
    this.and(FilterRequestOptions.greaterThan(key, value));
    return this;
  }
  public greaterThanOrEqual(key: string, value: any) : this {
    this.and(FilterRequestOptions.greaterThanOrEqual(key, value));
    return this;
  }

  public order(key: string, direction: 'ASC' | 'DESC') : this {
    this.orderBy.push(new OrderByRequestOptions({key, direction}));
    return this;
  }

  public orderAsc(key: string) : this {
    return this.order(key, 'ASC');
  }

  public orderDesc(key: string) : this {
    return this.order(key, 'DESC');
  }
}

export class SimpleCollectionHttpResponse<T> {
  page = 1;
  itemsPerPage = 10;
  resultCount: number = 0;
  totalCount: number = 0;

  public constructor(init?: Partial<SimpleCollectionHttpResponse<T>>) {
    Object.assign(this, init);
  }
}

export class CollectionHttpResponse<T> extends SimpleCollectionHttpResponse<T> {
  orderBy: OrderByRequestOptions[] = [];
  filter: FilterRequestOptions[] = [];
  search = '';
  items: T[] = [];

  public constructor(init?: Partial<CollectionHttpResponse<T>>) {
    super(init);
    Object.assign(this, init);
  }
}

export class OrderByRequestOptions {
  key: string = '';
  direction: 'ASC' | 'DESC' = 'ASC';

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

  public static mergeToAndFilters(filters: Array<FilterRequestOptions>): FilterRequestOptions | null {
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

  public static mergeToOrFilters(filters: Array<FilterRequestOptions>): FilterRequestOptions | null {
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

  public and(options: FilterRequestOptions) {
    this.ands.push(options);
    return this;
  }

  public andArray(options: FilterRequestOptions[]) {
    this.ands.push(...options);
    return this;
  }

  public or(options: FilterRequestOptions) {
    this.ors.push(options);
    return this;
  }
  public orArray(options: FilterRequestOptions[]) {
    this.ors.push(...options);
    return this;
  }

  public static like(key: string, value: string) {
    return new FilterRequestOptions({
      key: key,
      operator: 'LIKE',
      value: value
    });
  }

  public static in(key: string, value: any[]) {
    return new FilterRequestOptions({
      key: key,
      operator: 'IN',
      value: value
    });
  }

  public static notIn(key: string, value: any[]) {
    return new FilterRequestOptions({
      key: key,
      operator: 'NOT IN',
      value: value
    });
  }

  public static lessThan(key: string, value: any) {
    return new FilterRequestOptions({
      key: key,
      operator: '<',
      value: value
    });
  }
  public static lessThanOrEqual(key: string, value: any) {
    return new FilterRequestOptions({
      key: key,
      operator: '<=',
      value: value
    });
  }

  public static greaterThan(key: string, value: any) {
    return new FilterRequestOptions({
      key: key,
      operator: '>',
      value: value
    });
  }
  public static greaterThanOrEqual(key: string, value: any) {
    return new FilterRequestOptions({
      key: key,
      operator: '>=',
      value: value
    });
  }

  public static equals(key:string, value: any) {
    return new FilterRequestOptions({
      key: key,
      operator: '==',
      value: value
    })
  }
  public static notEquals(key:string, value: any) {
    return new FilterRequestOptions({
      key: key,
      operator: '!=',
      value: value
    })
  }





  public constructor(init?: Partial<FilterRequestOptions>) {
    Object.assign(this, init);
  }
}
