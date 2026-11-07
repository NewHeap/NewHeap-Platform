import './prototype-extensions/observable.extensions';
import {Component, createNgModule, EnvironmentInjector, ErrorHandler, importProvidersFrom, inject, Injectable, NgModule} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {HTTP_INTERCEPTORS, HttpClient, HttpEvent, HttpHandler, HttpInterceptor, HttpRequest} from '@angular/common/http';
import {provideTranslateService} from '@ngx-translate/core';
import {Observable} from 'rxjs';
import {NhCommonModuleConfig} from './models/config.models';
import {NhCommonModule} from './nh-common.module';
import {NhCookieService} from './services/nh-cookie.service';
import {NhInternetConnectionService} from './services/nh-internet-connection.service';
import {NhTaskResultFormValidationService} from './services/nh-task-result-form.validator';
import {NhServerSideFormValidationService} from './services/nh-server-side-form-validator.service';
import {NhCanCancelNavigationGuard} from './guards/nh-cancel-navigation.guard';
import {NhErrorHandlerService} from './services/nh-error-handler.service';

const NEWHEAP_INTERCEPTORS = [
  'NhActiveDivisionInterceptor',
  'NhApiAuthInterceptor',
  'NhSessionRejectionInterceptor',
  'NhEncodeHttpParamsInterceptor',
  'NhServerHttpInterceptor',
  'NhDeduplicateGetRequestsInterceptor'
];

@Injectable()
class InterceptorBeforeNewHeap implements HttpInterceptor {
  intercept(req: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    return next.handle(req);
  }
}

@Injectable()
class InterceptorAfterNewHeap implements HttpInterceptor {
  intercept(req: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    return next.handle(req);
  }
}

class StubAuthService {
}

function rootRegistration() {
  return NhCommonModule.forRoot(new NhCommonModuleConfig(), StubAuthService as never);
}

function interceptorNames(injector: EnvironmentInjector): string[] {
  return injector.get(HTTP_INTERCEPTORS).map(interceptor => interceptor.constructor.name);
}

@Component({selector: 'nh-feature-with-module', imports: [NhCommonModule], template: ''})
class FeatureWithModuleComponent {
  readonly http = inject(HttpClient);
  readonly environment = inject(EnvironmentInjector);
  readonly cookies = inject(NhCookieService);
  readonly connection = inject(NhInternetConnectionService);
  readonly errorHandler = inject(ErrorHandler);
}

@NgModule({imports: [NhCommonModule]})
class LazyFeatureModule {
}

describe('NhCommonModule provider scope', () => {
  describe('registered with importProvidersFrom', () => {
    beforeEach(() => {
      TestBed.configureTestingModule({
        providers: [
          provideTranslateService(),
          {provide: StubAuthService, useValue: new StubAuthService()},
          {provide: HTTP_INTERCEPTORS, useClass: InterceptorBeforeNewHeap, multi: true},
          importProvidersFrom(rootRegistration()),
          {provide: HTTP_INTERCEPTORS, useClass: InterceptorAfterNewHeap, multi: true}
        ]
      });
    });

    it('keeps the NewHeap interceptors at the position of the root registration', () => {
      expect(interceptorNames(TestBed.inject(EnvironmentInjector))).toEqual([
        'InterceptorBeforeNewHeap',
        ...NEWHEAP_INTERCEPTORS,
        'InterceptorAfterNewHeap'
      ]);
    });

    it('maps ErrorHandler to NhErrorHandlerService at the root', () => {
      expect(TestBed.inject(ErrorHandler)).toBe(TestBed.inject(NhErrorHandlerService));
    });

    it('lets a standalone component that imports NhCommonModule share the root HttpClient and services', () => {
      const root = TestBed.inject(EnvironmentInjector);
      const feature = TestBed.createComponent(FeatureWithModuleComponent).componentInstance;

      expect(feature.http).toBe(TestBed.inject(HttpClient));
      expect(feature.environment.get(HTTP_INTERCEPTORS)).toEqual(root.get(HTTP_INTERCEPTORS));
      expect(feature.cookies).toBe(TestBed.inject(NhCookieService));
      expect(feature.connection).toBe(TestBed.inject(NhInternetConnectionService));
      expect(feature.errorHandler).toBe(TestBed.inject(ErrorHandler));
    });

    it('lets a lazy NgModule that imports NhCommonModule share the root HttpClient and interceptors', () => {
      const root = TestBed.inject(EnvironmentInjector);
      const lazy = createNgModule(LazyFeatureModule, root).injector;

      expect(lazy.get(HttpClient)).toBe(TestBed.inject(HttpClient));
      expect(lazy.get(HTTP_INTERCEPTORS)).toEqual(root.get(HTTP_INTERCEPTORS));
      expect(lazy.get(NhCookieService)).toBe(TestBed.inject(NhCookieService));
    });
  });

  describe('registered through NgModule imports', () => {
    @NgModule({providers: [{provide: HTTP_INTERCEPTORS, useClass: InterceptorBeforeNewHeap, multi: true}]})
    class BeforeModule {
    }

    @NgModule({providers: [{provide: HTTP_INTERCEPTORS, useClass: InterceptorAfterNewHeap, multi: true}]})
    class AfterModule {
    }

    @NgModule({
      imports: [BeforeModule, rootRegistration(), AfterModule],
      providers: [provideTranslateService(), {provide: StubAuthService, useValue: new StubAuthService()}]
    })
    class RootModule {
    }

    it('keeps the NewHeap interceptors at the position of forRoot among the imports', () => {
      TestBed.configureTestingModule({imports: [RootModule]});

      expect(interceptorNames(TestBed.inject(EnvironmentInjector))).toEqual([
        'InterceptorBeforeNewHeap',
        ...NEWHEAP_INTERCEPTORS,
        'InterceptorAfterNewHeap'
      ]);
    });

    it('registers the NewHeap interceptors once when forRoot is imported twice', () => {
      @NgModule({imports: [rootRegistration()]})
      class CoreModule {
      }

      TestBed.configureTestingModule({imports: [RootModule, CoreModule]});

      expect(interceptorNames(TestBed.inject(EnvironmentInjector))).toEqual([
        'InterceptorBeforeNewHeap',
        ...NEWHEAP_INTERCEPTORS,
        'InterceptorAfterNewHeap'
      ]);
    });
  });

  describe('without a root registration', () => {
    it('registers no providers through plain NhCommonModule', () => {
      const moduleInjectorProviders = (NhCommonModule as never as {
        ɵinj: {providers: unknown[]}
      }).ɵinj.providers;

      expect(moduleInjectorProviders).toEqual([]);
    });

    it('provides the shared services from the root injector', () => {
      TestBed.configureTestingModule({});

      expect(TestBed.inject(NhCookieService)).toBeInstanceOf(NhCookieService);
      expect(TestBed.inject(NhInternetConnectionService)).toBeInstanceOf(NhInternetConnectionService);
      expect(TestBed.inject(NhTaskResultFormValidationService)).toBeInstanceOf(NhTaskResultFormValidationService);
      expect(TestBed.inject(NhServerSideFormValidationService)).toBeInstanceOf(NhServerSideFormValidationService);
      expect(TestBed.inject(NhCanCancelNavigationGuard)).toBeInstanceOf(NhCanCancelNavigationGuard);
    });
  });
});
