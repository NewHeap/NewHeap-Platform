import {lastValueFrom, Observable} from 'rxjs';
import {TaskResult, TypedResult} from "../models/misc.models";
import {NhApiUtil} from "../util/nh-api-util";

declare module "rxjs/internal/Observable" {
  interface Observable<T> {
    lastValueFrom(): Promise<T>;
    taskResultLastValueFrom(): Promise<TaskResult<T>>;
    typedResultLastValueFrom(): Promise<TypedResult<T>>;
  }
}

Observable.prototype.lastValueFrom = function () {
  return lastValueFrom(this);
}

Observable.prototype.taskResultLastValueFrom = async function <T>(): Promise<TaskResult<T>> {
  const taskResult = new TaskResult<T>();
  try {
    taskResult.data = await lastValueFrom(this);
  } catch (ex) {
    const errResult = NhApiUtil.taskResultFromResponse(ex);
    errResult.copyTo(taskResult);
  }

  return taskResult;
}

Observable.prototype.typedResultLastValueFrom = async function <T>(): Promise<TaskResult<T>> {
  const taskResult = new TaskResult<T>();
  try {
    taskResult.data = await lastValueFrom(this);
  } catch (ex) {
    const errResult = NhApiUtil.taskResultFromResponse(ex);
    errResult.copyTo(taskResult);
  }

  return taskResult.asTypedResult();
}
