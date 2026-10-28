import {TestBed} from '@angular/core/testing';
import {ActivatedRoute, Router} from '@angular/router';
import {NhToastrService, provideNhToastr} from '@newheap/nh-toastr';
import {provideTranslateService} from '@ngx-translate/core';
import {of} from 'rxjs';
import {CollectionHttpRequestOptions, CollectionHttpResponse} from '../models/http.models';
import {TaskResult} from '../models/misc.models';
import {NhAppService} from '../services/nh-app.service';
import {NhAuthService} from '../services/nh-auth.service';
import {NhModalService} from '../services/nh-modal.service';
import {NhRouterService} from '../services/nh-router.service';
import {NhServerSideFormValidationService} from '../services/nh-server-side-form-validator.service';
import {NhTaskResultFormValidationService} from '../services/nh-task-result-form.validator';
import {NhCollectionBaseComponent} from './nh-collection-base-component/component';
import {NhMutateBaseComponent} from './nh-mutate-base-component/component';

class CollectionPage extends NhCollectionBaseComponent<string> {
  notify() { return this.toastrService.success('Collection saved'); }
  async onLoad() { return of(new CollectionHttpResponse<string>()); }
  getInitialRequestOptions() { return new CollectionHttpRequestOptions(); }
  getLocalStoragePartialKey() { return 'test-collection'; }
}

class MutatePage extends NhMutateBaseComponent<string, string> {
  notify() { return this.toastrService.success('Form saved'); }
  async onNewFormData() { return ''; }
  async onSubmitCreate() { return new TaskResult<string>(); }
  async onSubmitUpdate() { return new TaskResult<string>(); }
}

describe('Base component toast notifications', () => {
  it('uses the same configured NH toast service for collection and mutate pages', () => {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhToastr({disableTimeOut: true, positionClass: 'toast-top-right'}),
        ...[
          NhAppService, NhAuthService, NhModalService, NhRouterService,
          NhServerSideFormValidationService, NhTaskResultFormValidationService,
          ActivatedRoute, Router
        ].map(provide => ({provide, useValue: {}}))
      ]
    });

    const collection = TestBed.runInInjectionContext(() => new CollectionPage());
    const mutate = TestBed.runInInjectionContext(() => new MutatePage());
    const toastr = TestBed.inject(NhToastrService);

    expect(collection.notify()?.position).toBe('toast-top-right');
    expect(mutate.notify()?.position).toBe('toast-top-right');
    expect(toastr.toasts().map(toast => toast.message)).toEqual(['Form saved', 'Collection saved']);
    toastr.clear();
  });
});
