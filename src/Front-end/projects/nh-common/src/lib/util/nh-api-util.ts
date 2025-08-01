import {TaskResult} from '../models/misc.models';
import {HttpErrorResponse} from "@angular/common/http";
import {error} from "ng-packagr/lib/utils/log";
import {CollectionHttpRequestOptions, FilterRequestOptions} from "../models/http.models";
import {NhCollectionBaseComponent} from "../components/nh-collection-base-component/component";

export class NhApiUtil {
  public static Constants = class {
    public static HttpHeaderKeys = class {
      //public static TenantId: string = 'X-TenantId';
    };
  };

  public static ParseCollectionRequestOptions(value?: string | null): CollectionHttpRequestOptions {
    const options = <CollectionHttpRequestOptions>JSON.parse(value ?? '');
    options.filter = options.filter.map(x => NhApiUtil.processFilter(x));
    return options;
  }

  private static processFilter(filter: FilterRequestOptions) {
    const f = new FilterRequestOptions(filter);
    f.ands = f.ands.map(x => NhApiUtil.processFilter(x));
    f.ors = f.ors.map(x => NhApiUtil.processFilter(x));
    return f;
  }

  public static taskResultFromResponse(response: any): TaskResult<any> {
    const taskResult = new TaskResult<any>();

    if (response !== undefined) {
      const checkResponse = response;
      const isHttpResponseError = checkResponse instanceof HttpErrorResponse;

      if (isHttpResponseError) {
        const responseContentType = response.headers.get('Content-Type');

        if (responseContentType && responseContentType.includes('json')) {
          if (response.error_description !== undefined) {
            taskResult.addError('', response.error_description);
          }

          if (response.ModelState !== undefined) {
            for (const key in response.ModelState) {

              const errors = response.ModelState[key];
              taskResult.addError(key, errors);
            }
          }

          if (response.Message !== undefined) {
            taskResult.addError('', response.Message);
          }

          if (response.error && response.error.errors) {
            if (response.error.errors instanceof Array) {
              taskResult.addError('', response.error.errors);
            } else if (response.error.errors instanceof Object) {
              for (const key in response.error.errors) {

                const errors = response.error.errors[key];
                taskResult.addError(key, errors);
              }
            } else {
              throw new Error('Unknown error type');
            }
          } else if (response.error) {
            if (Array.isArray(response.error)) {
              taskResult.addError('', response.error);
            } else {
              Object.keys(response.error).forEach(function (key) {
                if (Array.isArray(response.error[key])) {
                  taskResult.addError(key, response.error[key]);
                } else if (response.error[key] === Object(response.error[key])) {
                  taskResult.addError(key, response.error[key]);
                } else {
                  taskResult.addError(key, response.error[key]);
                }
              });
            }

          }
        } else {
          taskResult.addError('', response.error);
        }
      }
    }

    if (taskResult.isSuccess) {
      taskResult.addError('', 'An unknown error occurred');
    }

    return taskResult;
  }
}
