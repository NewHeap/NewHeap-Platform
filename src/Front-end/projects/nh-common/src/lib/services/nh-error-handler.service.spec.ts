import {ErrorHandler, Injector} from '@angular/core';
import {TestBed} from '@angular/core/testing';

import {NH_ERROR_HANDLERS, NhErrorHandlerService} from './nh-error-handler.service';

describe('NhErrorHandlerService', () => {
  it('forwards an error to every registered provider', () => {
    const first = jasmine.createSpyObj<ErrorHandler>('first', ['handleError']);
    const second = jasmine.createSpyObj<ErrorHandler>('second', ['handleError']);
    const service = createService([first, second]);
    const error = new Error('sample');

    service.handleError(error);

    expect(first.handleError).toHaveBeenCalledOnceWith(error);
    expect(second.handleError).toHaveBeenCalledOnceWith(error);
  });

  it('isolates a failing provider and continues with the remaining providers', () => {
    const failing = jasmine.createSpyObj<ErrorHandler>('failing', ['handleError']);
    failing.handleError.and.throwError('provider failed');
    const succeeding = jasmine.createSpyObj<ErrorHandler>('succeeding', ['handleError']);
    const service = createService([failing, succeeding]);
    const error = new Error('sample');

    expect(() => service.handleError(error)).not.toThrow();
    expect(succeeding.handleError).toHaveBeenCalledOnceWith(error);
  });

  function createService(handlers: ErrorHandler[]): NhErrorHandlerService {
    TestBed.configureTestingModule({
      providers: [
        NhErrorHandlerService,
        {provide: NH_ERROR_HANDLERS, useValue: handlers}
      ]
    });

    return new NhErrorHandlerService(TestBed.inject(Injector));
  }
});
