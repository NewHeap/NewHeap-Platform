import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

export class NhJsonLdDataItem {
  id: string = '';
  data: any;
  placeholderKeys: string[] = [];
  resolvePlaceholderKey: string | undefined;

  public constructor(init?: Partial<NhJsonLdDataItem>) {
    Object.assign(this, init);
  }
}
export class NhJsonLdData {
  items: NhJsonLdDataItem[] = [];

  public constructor(init?: Partial<NhJsonLdData>) {
    Object.assign(this, init);
  }
}

export class JsonLdDataChanged {
  data: NhJsonLdData = new NhJsonLdData();

  public constructor(init?: Partial<JsonLdDataChanged>) {
    Object.assign(this, init);
  }
}

const RESOLVE_PREFIX = '%%';
const RESOLVE_SUFFIX = RESOLVE_PREFIX;
export const REVIEW_AGGREGATE_RATING_KEY = (id: string | undefined) => `${RESOLVE_PREFIX}reviewAggregate-${id}${RESOLVE_SUFFIX}`;
export const REVIEW_KEY = (id: string | undefined) => `${RESOLVE_PREFIX}review-${id}${RESOLVE_SUFFIX}`;

@Injectable({
  providedIn: 'root'
})
export class NhJsonLdService {
  private data: NhJsonLdData;
  public readonly dataSubject: BehaviorSubject<JsonLdDataChanged>;

  constructor() {
    this.data = new NhJsonLdData();
    this.dataSubject = new BehaviorSubject<JsonLdDataChanged>(new JsonLdDataChanged({
      data: this.data
    }));
  }

  getData(): NhJsonLdData {
    return this.data;
  }

  private _addItem(item: NhJsonLdDataItem) {
    this._removeItem(item.id);
    this.data.items.push(item);
  }

  private _removeItem(itemId: string) {
    this.data.items = this.data.items.filter(x => x.id !== itemId);
  }

  addItem(item: NhJsonLdDataItem) {
    this._addItem(item);
    this.dataSubject.next(new JsonLdDataChanged({
      data: this.data
    }));
  }

  removeItem(itemId: string) {
    this._removeItem(itemId);
    this.dataSubject.next(new JsonLdDataChanged({
      data: this.data
    }));
  }

  clear() {
    this.data.items = [];
    this.dataSubject.next(new JsonLdDataChanged({
      data: this.data
    }));
  }

  build(): any {
    const items: any[] = [];

    for (const item of this.data.items) {
      if (!item.data) {
        continue;
      }

      if ((item.resolvePlaceholderKey?.length ?? 0) > 0) {
        // Not a root item
        continue;
      }

      this._resolvePlaceholderKeys(item);
      items.push(item.data);
    }

    return {
      '@context': 'https://schema.org/',
      '@graph': items
    };
  }

  private _resolvePlaceholderKeys(item: NhJsonLdDataItem): any {
    if (!item.placeholderKeys.length) {
      return;
    }

    for (const placeholderKey of item.placeholderKeys) {
      let placeholderItem = this.data.items.find(x => x.resolvePlaceholderKey === placeholderKey);
      if (!placeholderItem) {
        // Add empty item so that the temp key is replaced
        placeholderItem = new NhJsonLdDataItem();
      }

      if (placeholderItem.placeholderKeys.length) {
        this._resolvePlaceholderKeys(placeholderItem);
      }

      let foundMatch = false;
      for (const dataKey in item.data) {
        const dataValue = item.data[dataKey];
        if (dataValue !== placeholderKey) {
          continue;
        }

        item.data[dataKey] = placeholderItem.data;
        foundMatch = true;
        break;
      }

      if (foundMatch) {
        continue;
      }

      // Did not find a match, so must be added at root level of the item data
      item.data = {...item.data, ...placeholderItem.data};
    }
  }
}
