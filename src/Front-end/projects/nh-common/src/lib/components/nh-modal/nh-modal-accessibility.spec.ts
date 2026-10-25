import {Component, ViewContainerRef, inject, signal, viewChild} from '@angular/core';
import {ComponentFixture, TestBed, fakeAsync, tick} from '@angular/core/testing';
import {TranslateModule, TranslateService, provideTranslateService} from '@ngx-translate/core';

import {NhModalContentDirective} from '../../directives/nh-modal.directives';
import {NhModalComponentRef, NhModalOptions, NhModalService} from '../../services/nh-modal.service';
import {NhModalConfirmComponent} from '../nh-confirm-modal/component';
import {NhLoaderComponent} from '../nh-loader/component';
import {NhModalMutateBaseTypeComponent} from '../nh-modal-mutate-base-component/component';
import {NhModalComponent, NhModalComponentImpl} from './component';

@Component({
  selector: 'nh-test-modal-form',
  template: `
    <label>Name <input id="name" /></label>
    <fieldset disabled><input id="in-disabled-fieldset" /></fieldset>
    <button type="button" id="hidden" style="display: none">Hidden</button>
    <button type="button" id="open-next">Open next</button>
    <button type="button" id="save">Save</button>
    <button type="button" id="disabled" disabled>Disabled</button>
  `,
  standalone: false
})
class FormContentComponent extends NhModalComponentImpl<FormContentComponent> {
}

@Component({
  selector: 'nh-test-modal-autofocus',
  template: '<input id="first" /><input id="chosen" autofocus />',
  standalone: false
})
class AutofocusContentComponent extends NhModalComponentImpl<AutofocusContentComponent> {
}

@Component({
  selector: 'nh-test-modal-text',
  template: '<p>Processing projects.</p>',
  standalone: false
})
class TextContentComponent extends NhModalComponentImpl<TextContentComponent> {
}

@Component({
  selector: 'nh-test-modal-loading-form',
  template: '@if (loaded()) { <input id="loaded-field" /> } @else { <p>Loading</p> }',
  standalone: false
})
class LoadingFormContentComponent extends NhModalComponentImpl<LoadingFormContentComponent> {
  readonly loaded = signal(false);
}

/** Lets the DOM observers of the modal run. */
function observersRun(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

/** A page with an opener button, whose view container hosts the modals it opens. */
@Component({
  selector: 'nh-test-modal-page',
  template: '<button type="button" id="opener">Edit</button>',
  standalone: false
})
class ModalPageComponent {
  readonly viewContainerRef = inject(ViewContainerRef);
}

/** The application shell with a routed page, which a route change destroys. */
@Component({
  selector: 'nh-test-modal-shell',
  template: '<button type="button" id="shell-opener">Menu</button>@if (pageShown()) { <nh-test-modal-page /> }',
  standalone: false
})
class ModalShellComponent {
  readonly viewContainerRef = inject(ViewContainerRef);
  readonly pageShown = signal(true);
  readonly page = viewChild(ModalPageComponent);
}

function keydown(key: string, shiftKey = false): KeyboardEvent {
  const event = new KeyboardEvent('keydown', {key, shiftKey, bubbles: true, cancelable: true});
  (document.activeElement ?? document.body).dispatchEvent(event);
  return event;
}

function active(): string {
  const element = document.activeElement as HTMLElement | null;
  return element?.id || element?.className || element?.tagName || '';
}

describe('NhModalComponent accessibility', () => {
  let service: NhModalService;
  let fixtures: ComponentFixture<ModalPageComponent>[];

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TranslateModule],
      declarations: [
        NhModalComponent,
        NhModalContentDirective,
        NhLoaderComponent,
        NhModalConfirmComponent,
        FormContentComponent,
        AutofocusContentComponent,
        TextContentComponent,
        LoadingFormContentComponent,
        ModalPageComponent,
        ModalShellComponent
      ],
      providers: [provideTranslateService({fallbackLang: 'en'})]
    });
    service = TestBed.inject(NhModalService);
    fixtures = [];
  });

  afterEach(() => {
    for (const fixture of fixtures) {
      fixture.destroy();
    }
    document.body.classList.remove('nh-modal-open');
  });

  function page(): ComponentFixture<ModalPageComponent> {
    const fixture = TestBed.createComponent(ModalPageComponent);
    fixtures.push(fixture);
    fixture.detectChanges();
    return fixture;
  }

  function element(fixture: ComponentFixture<ModalPageComponent>, selector: string): HTMLElement {
    return (fixture.nativeElement as HTMLElement).parentElement!.querySelector(selector) as HTMLElement;
  }

  function open<C extends NhModalComponentImpl<C>>(
    fixture: ComponentFixture<ModalPageComponent>,
    content: new () => C,
    options: {title?: string; closeable?: boolean} = {}
  ): NhModalComponentRef<C> {
    const modal = service.open(content, {title: 'Edit project', ...options, viewContainerRef: fixture.componentInstance.viewContainerRef});
    fixture.detectChanges();
    return modal;
  }

  function holderOf(modal: NhModalComponentRef<unknown>): HTMLElement {
    return (modal.componentRef.location.nativeElement as HTMLElement).querySelector('.nh-modal-holder') as HTMLElement;
  }

  it('is a modal dialog named by its title, with a labelled close button', fakeAsync(() => {
    const fixture = page();
    const modal = open(fixture, TextContentComponent);
    const holder = holderOf(modal);
    const title = holder.querySelector('h3') as HTMLElement;
    const close = holder.querySelector('.close-nh-modal') as HTMLButtonElement;

    expect(holder.getAttribute('role')).toBe('dialog');
    expect(holder.getAttribute('aria-modal')).toBe('true');
    expect(title.id).toMatch(/^nh-modal-title-\d+$/);
    expect(holder.getAttribute('aria-labelledby')).toBe(title.id);
    expect(title.textContent?.trim()).toBe('Edit project');
    expect(close.type).toBe('button');
    expect(close.getAttribute('aria-label')).toBe('Close');
    expect(close.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
    tick(100);
  }));

  it('translates the close label from the bundle, and the application wins', fakeAsync(() => {
    const translate = TestBed.inject(TranslateService);
    const fixture = page();
    const modal = open(fixture, TextContentComponent);
    const close = holderOf(modal).querySelector('.close-nh-modal') as HTMLElement;

    translate.use('nl-NL');
    fixture.detectChanges();
    expect(close.getAttribute('aria-label')).toBe('Sluiten');

    translate.setTranslation('nl-NL', {'nh-common': {modal: {close: 'Venster sluiten'}}});
    fixture.detectChanges();
    expect(close.getAttribute('aria-label')).toBe('Venster sluiten');
    tick(100);
  }));

  it('names no title when the modal has none', fakeAsync(() => {
    const fixture = page();
    const modal = open(fixture, TextContentComponent, {title: ''});

    expect(holderOf(modal).hasAttribute('aria-labelledby')).toBeFalse();
    tick(100);
  }));

  it('moves focus to the first field, and back to the opener when it closes', fakeAsync(() => {
    const fixture = page();
    element(fixture, '#opener').focus();
    const modal = open(fixture, FormContentComponent);

    expect(active()).toBe('name');

    modal.modalComponent.close();
    expect(active()).toBe('opener');
    tick(600);
    expect(service.modalRefs.length).toBe(0);
  }));

  it('prefers a field with autofocus', fakeAsync(() => {
    const fixture = page();
    open(fixture, AutofocusContentComponent);

    expect(active()).toBe('chosen');
    tick(100);
  }));

  it('focuses the first field of content that renders it after loading', async () => {
    const fixture = page();
    const modal = open(fixture, LoadingFormContentComponent);
    expect(active()).toContain('close-nh-modal');

    modal.contentComponent!.loaded.set(true);
    fixture.detectChanges();
    await observersRun();

    expect(active()).toBe('loaded-field');
  });

  it('leaves focus where the user moved it while the content loads', async () => {
    const fixture = page();
    const modal = open(fixture, LoadingFormContentComponent);
    holderOf(modal).focus();

    modal.contentComponent!.loaded.set(true);
    fixture.detectChanges();
    await observersRun();

    expect(document.activeElement).toBe(holderOf(modal));
  });

  it('keeps Tab and Shift+Tab inside the modal and skips disabled and hidden controls', fakeAsync(() => {
    const fixture = page();
    const modal = open(fixture, FormContentComponent);
    const holder = holderOf(modal);

    // The browser moves Tab between controls inside the modal.
    expect(keydown('Tab').defaultPrevented).toBeFalse();

    // From the last enabled control Tab wraps to the close button, the first control.
    (holder.querySelector('#save') as HTMLElement).focus();
    expect(keydown('Tab').defaultPrevented).toBeTrue();
    expect(active()).toContain('close-nh-modal');

    // Shift+Tab from the first control wraps to the last enabled one.
    expect(keydown('Tab', true).defaultPrevented).toBeTrue();
    expect(active()).toBe('save');

    // From outside the modal, Tab comes back in.
    element(fixture, '#opener').focus();
    expect(keydown('Tab').defaultPrevented).toBeTrue();
    expect(active()).toContain('close-nh-modal');
    tick(100);
  }));

  it('keeps focus on the dialog when it has no control at all', fakeAsync(() => {
    const fixture = page();
    const modal = open(fixture, TextContentComponent, {closeable: false});
    const holder = holderOf(modal);

    expect(document.activeElement).toBe(holder);
    expect(keydown('Tab').defaultPrevented).toBeTrue();
    expect(keydown('Tab', true).defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(holder);
    tick(100);
  }));

  it('closes on Escape when it can close', fakeAsync(() => {
    const fixture = page();
    element(fixture, '#opener').focus();
    open(fixture, FormContentComponent);

    expect(keydown('Escape').defaultPrevented).toBeTrue();
    expect(active()).toBe('opener');
    tick(600);
    expect(service.modalRefs.length).toBe(0);
    expect(document.body.classList.contains('nh-modal-open')).toBeFalse();
  }));

  it('stays open on Escape when it cannot close', fakeAsync(() => {
    const fixture = page();
    const modal = open(fixture, TextContentComponent, {closeable: false});

    expect(holderOf(modal).querySelector('.close-nh-modal')).toBeNull();
    expect(keydown('Escape').defaultPrevented).toBeFalse();
    tick(600);
    expect(service.modalRefs).toEqual([modal]);
    expect(holderOf(modal).isConnected).toBeTrue();
  }));

  it('leaves an Escape that the content already handled alone', fakeAsync(() => {
    const fixture = page();
    const modal = open(fixture, FormContentComponent);
    const input = holderOf(modal).querySelector('#name') as HTMLElement;
    input.addEventListener('keydown', event => event.preventDefault());

    input.dispatchEvent(new KeyboardEvent('keydown', {key: 'Escape', bubbles: true, cancelable: true}));
    tick(600);
    expect(service.modalRefs).toEqual([modal]);
  }));

  it('closes stacked modals one at a time from the top, returning focus each time', fakeAsync(() => {
    const fixture = page();
    element(fixture, '#opener').focus();
    const lower = open(fixture, FormContentComponent);
    (holderOf(lower).querySelector('#open-next') as HTMLElement).focus();
    const upper = open(fixture, AutofocusContentComponent, {title: 'Pick a member'});
    expect(active()).toBe('chosen');

    // Tab wraps inside the upper modal only.
    (holderOf(upper).querySelector('#chosen') as HTMLElement).focus();
    keydown('Tab');
    expect(holderOf(upper).contains(document.activeElement)).toBeTrue();

    keydown('Escape');
    expect(active()).toBe('open-next');
    tick(600);
    expect(service.modalRefs).toEqual([lower]);
    expect(document.body.classList.contains('nh-modal-open')).toBeTrue();

    keydown('Escape');
    expect(active()).toBe('opener');
    tick(600);
    expect(service.modalRefs.length).toBe(0);
    expect(document.body.classList.contains('nh-modal-open')).toBeFalse();
  }));

  it('lets confirmation content close through its closeDialog on Escape', fakeAsync(() => {
    const fixture = page();
    const modal = service.confirmDialog('Delete the project?', () => undefined, undefined, new NhModalOptions({
      viewContainerRef: fixture.componentInstance.viewContainerRef
    }));
    fixture.detectChanges();
    const closeDialog = spyOn(modal.contentComponent!, 'closeDialog').and.callThrough();

    // The confirmation's first control, "no", has focus.
    expect(document.activeElement?.textContent?.trim()).toBe('general.no');

    keydown('Escape');
    expect(closeDialog).toHaveBeenCalledTimes(1);
    expect(service.modalRefs.length).toBe(0);
    tick(100);
  }));

  it('leaves Escape of mutating content to closeDialog', () => {
    const closeDialog = jasmine.createSpy('closeDialog');

    NhModalMutateBaseTypeComponent.prototype.onModalEscape.call({closeDialog} as never);

    expect(closeDialog).toHaveBeenCalledOnceWith({});
  });

  it('cleans up when the page that hosts the modal is destroyed, and the modal below takes over', fakeAsync(() => {
    const shell = TestBed.createComponent(ModalShellComponent);
    shell.detectChanges();
    const shellElement = shell.nativeElement as HTMLElement;
    (shellElement.querySelector('#shell-opener') as HTMLElement).focus();
    const lower = service.open(FormContentComponent, {title: 'Edit project', viewContainerRef: shell.componentInstance.viewContainerRef});
    shell.detectChanges();
    (holderOf(lower).querySelector('#save') as HTMLElement).focus();
    const upper = service.open(TextContentComponent, {title: 'Details', viewContainerRef: shell.componentInstance.page()!.viewContainerRef});
    shell.detectChanges();
    const upperHolder = holderOf(upper);
    expect(holderOf(upper).contains(document.activeElement)).toBeTrue();

    // The route changes: its page and the modal it hosts are destroyed, focus goes back below.
    shell.componentInstance.pageShown.set(false);
    shell.detectChanges();
    expect(upperHolder.isConnected).toBeFalse();
    expect(active()).toBe('save');
    expect(document.body.classList.contains('nh-modal-open')).toBeTrue();

    // Escape and Tab now belong to the modal below.
    keydown('Escape');
    expect(active()).toBe('shell-opener');
    tick(600);
    expect(document.body.classList.contains('nh-modal-open')).toBeFalse();
    expect(() => keydown('Escape')).not.toThrow();
    shell.destroy();
  }));
});
