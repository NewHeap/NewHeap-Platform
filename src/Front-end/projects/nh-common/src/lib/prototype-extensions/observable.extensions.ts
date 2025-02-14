import {lastValueFrom, Observable} from 'rxjs';

declare module "rxjs/internal/Observable" {
  interface Observable<T> {
    lastValueFrom(): Promise<T>;
  }
}

Observable.prototype.lastValueFrom = function () {
  return lastValueFrom(this);
}
