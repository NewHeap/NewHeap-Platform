import {Component, ViewContainerRef, inject} from '@angular/core';
import {ComponentFixture, TestBed, fakeAsync, tick} from '@angular/core/testing';
import {TranslateDirective, TranslatePipe, provideTranslateService} from '@ngx-translate/core';

import {NhModalContentDirective} from '../../directives/nh-modal.directives';
import {TaskResult} from '../../models/misc.models';
import {NhModalComponentRef, NhModalService} from '../../services/nh-modal.service';
import {NhLoaderComponent} from '../nh-loader/component';
import {NhModalComponent} from '../nh-modal/component';
import {NhModalConfirmComponent} from './component';

/** The application root, whose view container hosts the modals. */
@Component({
  selector: 'nh-test-confirm-root',
  template: '',
  standalone: false
})
class ModalRootComponent {
  readonly viewContainerRef = inject(ViewContainerRef);
}

describe('NhModalConfirmComponent', () => {
  let service: NhModalService;
  let root: ComponentFixture<ModalRootComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TranslateDirective, TranslatePipe],
      declarations: [NhModalComponent, NhModalContentDirective, NhLoaderComponent, NhModalConfirmComponent, ModalRootComponent],
      providers: [provideTranslateService({fallbackLang: 'en'})]
    });

    service = TestBed.inject(NhModalService);
    root = TestBed.createComponent(ModalRootComponent);
    root.detectChanges();
    service.setViewContainerRef(root.componentInstance.viewContainerRef);
  });

  afterEach(() => {
    root.destroy();
    document.body.classList.remove('nh-modal-open');
  });

  function holderOf(modal: NhModalComponentRef<NhModalConfirmComponent>): HTMLElement {
    return (modal.componentRef.location.nativeElement as HTMLElement).querySelector('.nh-modal-holder') as HTMLElement;
  }

  /** Clicks "Yes" and lets the confirm delegate settle. */
  function clickYes(modal: NhModalComponentRef<NhModalConfirmComponent>): void {
    holderOf(modal).querySelector<HTMLButtonElement>('.btn-success')!.click();
    tick();
    root.detectChanges();
  }

  function clickNo(modal: NhModalComponentRef<NhModalConfirmComponent>): void {
    holderOf(modal).querySelector<HTMLButtonElement>('.btn-danger')!.click();
    tick();
    root.detectChanges();
  }

  function errorShownIn(holder: HTMLElement): string {
    return holder.querySelector('[role="alert"]')?.textContent ?? '';
  }

  describe('opened by the modal service', () => {
    it('runs onConfirm on "Yes", closes the dialog and calls onClose once', fakeAsync(() => {
      const onConfirm = jasmine.createSpy('onConfirm');
      const onClose = jasmine.createSpy('onClose');
      const modal = service.confirm(onConfirm, onClose);
      root.detectChanges();
      const holder = holderOf(modal);

      clickYes(modal);

      expect(onConfirm).toHaveBeenCalledTimes(1);
      expect(onClose).toHaveBeenCalledTimes(1);
      expect(holder.isConnected).toBeFalse();
      expect(service.modalRefs.length).toBe(0);
      tick(100);
    }));

    it('closes only once an asynchronous onConfirm completed, and runs it once for a double click', fakeAsync(() => {
      let finishDelete: () => void = () => undefined;
      const onConfirm = jasmine.createSpy('onConfirm').and.callFake(() => new Promise<void>(resolve => finishDelete = resolve));
      const onClose = jasmine.createSpy('onClose');
      const modal = service.confirmDialog('Delete the project?', onConfirm, onClose);
      root.detectChanges();
      expect(holderOf(modal).textContent).toContain('Delete the project?');

      clickYes(modal);
      clickYes(modal);
      expect(onConfirm).toHaveBeenCalledTimes(1);
      expect(onClose).not.toHaveBeenCalled();
      expect(service.modalRefs).toEqual([modal]);

      finishDelete();
      tick();

      expect(onClose).toHaveBeenCalledTimes(1);
      expect(service.modalRefs.length).toBe(0);
      tick(100);
    }));

    it('keeps the dialog open and shows the error when onConfirm fails', fakeAsync(() => {
      const onClose = jasmine.createSpy('onClose');
      const modal = service.confirmDialog('Delete the project?', () => Promise.reject(new Error('Project has open tasks')), onClose);
      root.detectChanges();
      const holder = holderOf(modal);

      clickYes(modal);

      expect(errorShownIn(holder)).toContain('An unknown error occurred');
      expect(onClose).not.toHaveBeenCalled();
      expect(service.modalRefs).toEqual([modal]);

      // "No" still leaves the dialog, and onClose keeps its meaning.
      clickNo(modal);
      expect(onClose).toHaveBeenCalledTimes(1);
      expect(holder.isConnected).toBeFalse();
      tick(100);
    }));

    it('closes on "No" without running onConfirm', fakeAsync(() => {
      const onConfirm = jasmine.createSpy('onConfirm');
      const onClose = jasmine.createSpy('onClose');
      const modal = service.confirmDialog('Delete the project?', onConfirm, onClose);
      root.detectChanges();

      clickNo(modal);

      expect(onConfirm).not.toHaveBeenCalled();
      expect(onClose).toHaveBeenCalledTimes(1);
      expect(service.modalRefs.length).toBe(0);
      tick(100);
    }));
  });

  describe('used directly with the onConfirm input', () => {
    function openDirect(): NhModalComponentRef<NhModalConfirmComponent> {
      const modal = service.open(NhModalConfirmComponent, {title: 'Delete project'});
      root.detectChanges();
      return modal;
    }

    it('runs the onConfirm input and emits confirmed once it completed, leaving closing to the caller', fakeAsync(() => {
      const modal = openDirect();
      const content = modal.contentComponent!;
      const confirmed = jasmine.createSpy('confirmed');
      content.confirmed.subscribe(confirmed);
      content.onConfirm = jasmine.createSpy('onConfirm');

      clickYes(modal);

      expect(content.onConfirm).toHaveBeenCalledTimes(1);
      expect(confirmed).toHaveBeenCalledTimes(1);
      expect(service.modalRefs).toEqual([modal]);
      tick(100);
    }));

    it('does not emit confirmed when onConfirm throws or shows a failed result', fakeAsync(() => {
      const modal = openDirect();
      const content = modal.contentComponent!;
      const confirmed = jasmine.createSpy('confirmed');
      content.confirmed.subscribe(confirmed);

      content.onConfirm = () => Promise.reject(new Error('Project has open tasks'));
      clickYes(modal);
      expect(errorShownIn(holderOf(modal))).toContain('An unknown error occurred');

      content.onConfirm = () => {
        const failed = new TaskResult<unknown>();
        failed.addError('', 'The project still has open tasks.');
        content.errorResult = failed;
      };
      clickYes(modal);
      expect(errorShownIn(holderOf(modal))).toContain('The project still has open tasks.');

      expect(confirmed).not.toHaveBeenCalled();
      expect(service.modalRefs).toEqual([modal]);
      tick(100);
    }));

    it('finishes quietly when onConfirm closes the dialog itself', fakeAsync(() => {
      const modal = openDirect();
      const content = modal.contentComponent!;
      const confirmed = jasmine.createSpy('confirmed');
      content.confirmed.subscribe(confirmed);
      content.onConfirm = async () => {
        modal.close();
      };
      const warn = spyOn(console, 'warn');

      clickYes(modal);

      expect(warn).not.toHaveBeenCalled();
      expect(confirmed).not.toHaveBeenCalled();
      expect(service.modalRefs.length).toBe(0);
      tick(100);
    }));
  });
});
