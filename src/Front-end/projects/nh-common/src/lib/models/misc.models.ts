import {KeyValue} from "@angular/common";

export type SuccessResult<T> = TaskResult<T> & { isSuccess: true, data: T };
export type ErrorResult<T> = TaskResult<T> & { isSuccess: false, data: T | undefined };
export type TypedResult<T> = SuccessResult<T> | ErrorResult<T>;

export class TaskResultItem {
  name: string = '';
  errorMessages: string[] = [];

  public constructor(init?: Partial<TaskResultItem>) {
    Object.assign(this, init);
  }
}

export class TaskResult<T> {
  isSuccess: boolean = true;
  items: TaskResultItem[] = [];
  data: T | undefined;

  public constructor(init?: Partial<TaskResult<T>>) {
    Object.assign(this, init);
  }

  withError(name: string, errorMessages: string | string[]): TaskResult<T> {
    this.addError(name, errorMessages);
    return this;
  }

  addError(name: string, errorMessages: string | string[]) {
    this.isSuccess = false;
    let item = this.items.find(x => x.name === name);

    if (!item) {
      item = new TaskResultItem({name: name});
      this.items.push(item);
    }

    if (!Array.isArray(errorMessages)) {
      errorMessages = [errorMessages];
    }

    if (errorMessages && errorMessages.length > 0) {
      for (const errorMessage of errorMessages) {
        item.errorMessages.push(errorMessage);
      }
    }
  }

  addErrors(errors: TaskResultItem[]) {
    this.isSuccess = false;

    for (const error of errors) {
      let item = this.items.find(x => x.name === error.name);

      if (!item) {
        item = new TaskResultItem({name: error.name});
        this.items.push(item);
      }

      for (const errorMessage of error.errorMessages) {
        if (!item.errorMessages.includes(errorMessage)) {
          item.errorMessages.push(errorMessage);
        }
      }
    }
  }

  copyTo<T2>(target: TaskResult<T2>) {
    if (target) {
      for (const item of this.items) {
        for (const err of item.errorMessages) {
          target.addError(item.name, err);
        }
      }
    }
  }

  getAllErrorMessages(): string[] {
    return this.items.flatMap(x => x.errorMessages);
  }

  asTypedResult(): TypedResult<T> {
    return <TypedResult<T>>this;
  }

}

export enum ListSortDirection {
  Ascending = 'Ascending',
  Descending = 'Descending',
}

export class PreConnectUrlItem {
  preConnect: boolean = true;
  dnsPrefetch: boolean = true;
  url!: string;
  withCrossOrigin: boolean = false;
  crossOrigin?: string;
  additionalAttributes: KeyValue<string, string>[] = [];

  public constructor(init?: Partial<PreConnectUrlItem>) {
    Object.assign(this, init);
  }
}

export class PreLoadUrlItem {
  url!: string;
  withCrossOrigin: boolean = false;
  crossOrigin?: string;
  as!: string;
  type!: string;
  additionalAttributes: KeyValue<string, string>[] = [];

  public constructor(init?: Partial<PreLoadUrlItem>) {
    Object.assign(this, init);
  }
}

export type NhRouterLink = {
  id: string;
  arguments?: any;
  language?: string | null | undefined; // Undefined will use the current language
  scrollToTop?: boolean;
};
