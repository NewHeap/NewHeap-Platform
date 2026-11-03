import {HttpClient, HttpErrorResponse, provideHttpClient} from '@angular/common/http';
import {HttpTestingController, provideHttpClientTesting} from '@angular/common/http/testing';
import {TransferState, makeStateKey} from '@angular/core';
import {TestBed} from '@angular/core/testing';

import {NhCommonModuleConfig, NhTranslationNhCommonModuleConfig} from '../../models/config.models';
import {NhTranslateBrowserLoader, nhTranslateBrowserLoaderFactory} from './translate-browser.loader';

describe('NhTranslateBrowserLoader', () => {
  let loader: NhTranslateBrowserLoader;
  let transferState: TransferState;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: NhCommonModuleConfig,
          useValue: new NhCommonModuleConfig({
            translation: new NhTranslationNhCommonModuleConfig({browserLoaderPrefix: './i18n'})
          })
        }
      ]
    });

    transferState = TestBed.inject(TransferState);
    httpTesting = TestBed.inject(HttpTestingController);
    loader = TestBed.runInInjectionContext(() =>
      nhTranslateBrowserLoaderFactory(TestBed.inject(HttpClient), transferState));
  });

  afterEach(() => httpTesting.verify());

  function load(lang: string): {texts?: unknown; error?: unknown} {
    const result: {texts?: unknown; error?: unknown} = {};
    loader.getTranslation(lang).subscribe({
      next: texts => result.texts = texts,
      error: error => result.error = error
    });
    return result;
  }

  it('loads the texts of the base language for a regional language without a file of its own', () => {
    for (const [regional, base] of [['nl-NL', 'nl'], ['nl_BE', 'nl'], ['en-GB', 'en']]) {
      const result = load(regional);

      httpTesting.expectOne(`./i18n/${regional}.json`).flush('Not found', {status: 404, statusText: 'Not Found'});
      httpTesting.expectOne(`./i18n/${base}.json`).flush({project: {title: base}});

      expect(result.error).toBeUndefined();
      expect(result.texts).toEqual({project: {title: base}});
    }
  });

  it('uses a regional file that exists as it is', () => {
    const result = load('nl-BE');

    httpTesting.expectOne('./i18n/nl-BE.json').flush({project: {title: 'Vlaams'}});

    expect(result.texts).toEqual({project: {title: 'Vlaams'}});
    httpTesting.expectNone('./i18n/nl.json');
  });

  it('keeps a language without a region unchanged, a missing file included', () => {
    const present = load('nl');
    httpTesting.expectOne('./i18n/nl.json').flush({project: {title: 'nl'}});
    expect(present.texts).toEqual({project: {title: 'nl'}});

    const missing = load('nl');
    httpTesting.expectOne('./i18n/nl.json').flush('Not found', {status: 404, statusText: 'Not Found'});
    expect(missing.texts).toBeUndefined();
    expect((missing.error as HttpErrorResponse).status).toBe(404);
  });

  it('propagates other errors of a regional file without loading the base language', () => {
    const result = load('nl-NL');

    httpTesting.expectOne('./i18n/nl-NL.json').flush('Server error', {status: 500, statusText: 'Internal Server Error'});

    expect((result.error as HttpErrorResponse).status).toBe(500);
    httpTesting.expectNone('./i18n/nl.json');
  });

  it('reads the transfer state of each language before loading its file', () => {
    transferState.set(makeStateKey<unknown>('transfer-translate-nl-NL'), {project: {title: 'regional state'}});
    expect(load('nl-NL').texts).toEqual({project: {title: 'regional state'}});

    transferState.set(makeStateKey<unknown>('transfer-translate-en'), {project: {title: 'base state'}});
    const fallback = load('en-GB');
    httpTesting.expectOne('./i18n/en-GB.json').flush('Not found', {status: 404, statusText: 'Not Found'});
    expect(fallback.texts).toEqual({project: {title: 'base state'}});
  });
});
