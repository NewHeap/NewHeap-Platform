import {Component, Type, ViewContainerRef, inject} from '@angular/core';
import {ComponentFixture, TestBed, fakeAsync, tick} from '@angular/core/testing';
import {Location} from '@angular/common';
import {SpyLocation, provideLocationMocks} from '@angular/common/testing';
import {Router, provideRouter} from '@angular/router';
import {TranslateDirective, TranslatePipe, provideTranslateService} from '@ngx-translate/core';

import {NhModalContentDirective} from '../../directives/nh-modal.directives';
import {INhModalComponent, NhModalComponentRef, NhModalOptions, NhModalService} from '../../services/nh-modal.service';
import {NhModalConfirmComponent} from '../nh-confirm-modal/component';
import {NhLoaderComponent} from '../nh-loader/component';
import {NhModalMutateBaseTypeComponent} from '../nh-modal-mutate-base-component/component';
import {NhModalComponent, NhModalComponentImpl} from './component';

/** The order in which content was asked to leave for a back navigation. */
let dismissals: string[] = [];

@Component({
  selector: 'nh-test-back-text',
  template: '<p>Project details.</p>',
  standalone: false
})
class TextContentComponent extends NhModalComponentImpl<TextContentComponent> {
}

@Component({
  selector: 'nh-test-back-recording',
  template: '<p>Recording content.</p>',
  standalone: false
})
class RecordingContentComponent extends NhModalComponentImpl<RecordingContentComponent> {
  name = '';

  onModalBackNavigation(): void {
    dismissals.push(this.name);
    this.close();
  }
}

@Component({
  selector: 'nh-test-back-escape',
  template: '<p>Content that handles Escape.</p>',
  standalone: false
})
class EscapeContentComponent extends NhModalComponentImpl<EscapeContentComponent> {
  onModalEscape(): void {
    dismissals.push('escape');
    this.close();
  }
}

/** Content with unsaved changes that asks before it leaves, as an override of `closeDialog` may. */
@Component({
  selector: 'nh-test-back-guarded',
  template: '<p>Unsaved changes.</p>',
  standalone: false
})
class GuardedContentComponent extends NhModalComponentImpl<GuardedContentComponent> {
  private readonly modalService = inject(NhModalService);

  onModalBackNavigation(): void {
    dismissals.push('guarded');
    this.modalService.confirmDialog('Discard your changes?', () => this.close());
  }
}

@Component({
  selector: 'nh-test-back-route',
  template: '',
  standalone: false
})
class RoutedPageComponent {
}

/** The application root, whose view container hosts the modals, as `setViewContainerRef` sets it. */
@Component({
  selector: 'nh-test-back-root',
  template: '<button type="button" id="opener">Edit</button>',
  standalone: false
})
class ModalRootComponent {
  readonly viewContainerRef = inject(ViewContainerRef);
}

/** A routed page that hosts a modal in its own view container. */
@Component({
  selector: 'nh-test-back-page',
  template: '',
  standalone: false
})
class ModalPageComponent {
  readonly viewContainerRef = inject(ViewContainerRef);
}

function active(): string {
  const element = document.activeElement as HTMLElement | null;
  return element?.id || element?.className || element?.tagName || '';
}

describe('NhModalService back navigation', () => {
  let service: NhModalService;
  let router: Router;
  let location: SpyLocation;
  let root: ComponentFixture<ModalRootComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TranslateDirective, TranslatePipe],
      declarations: [
        NhModalComponent,
        NhModalContentDirective,
        NhLoaderComponent,
        NhModalConfirmComponent,
        TextContentComponent,
        RecordingContentComponent,
        EscapeContentComponent,
        GuardedContentComponent,
        RoutedPageComponent,
        ModalRootComponent,
        ModalPageComponent
      ],
      providers: [
        provideTranslateService({fallbackLang: 'en'}),
        provideRouter([
          {path: 'projects', component: RoutedPageComponent},
          {path: 'projects/:id', component: RoutedPageComponent}
        ]),
        provideLocationMocks()
      ]
    });

    service = TestBed.inject(NhModalService);
    router = TestBed.inject(Router);
    location = TestBed.inject(Location) as unknown as SpyLocation;
    // What the router's bootstrap does in an application: browser history navigates the router.
    router.setUpLocationChangeListener();

    root = TestBed.createComponent(ModalRootComponent);
    root.detectChanges();
    service.setViewContainerRef(root.componentInstance.viewContainerRef);
    dismissals = [];
  });

  afterEach(() => {
    root.destroy();
    document.body.classList.remove('nh-modal-open');
  });

  /** Two pages in the browser history: the project list, then a project. */
  function browseToProject(): void {
    void router.navigateByUrl('/projects');
    tick();
    void router.navigateByUrl('/projects/1');
    tick();
  }

  /** The browser's back button: a `popstate` navigation that the router follows on a timer. */
  function browserBack(): void {
    location.back();
    tick();
  }

  function browserForward(): void {
    location.forward();
    tick();
  }

  function open<C extends INhModalComponent<C>>(content: Type<C>, options: Partial<NhModalOptions> = {}): NhModalComponentRef<C> {
    const modal = service.open(content, {title: 'Edit project', ...options});
    root.detectChanges();
    return modal;
  }

  function openRecording(name: string, options: Partial<NhModalOptions> = {}): NhModalComponentRef<RecordingContentComponent> {
    const modal = open(RecordingContentComponent, options);
    modal.contentComponent!.name = name;
    return modal;
  }

  function holderOf(modal: NhModalComponentRef<unknown>): HTMLElement {
    return (modal.componentRef.location.nativeElement as HTMLElement).querySelector('.nh-modal-holder') as HTMLElement;
  }

  it('closes an open modal on browser back as the close button does, and emits closed', fakeAsync(() => {
    browseToProject();
    (root.nativeElement as HTMLElement).querySelector<HTMLElement>('#opener')!.focus();
    const modal = open(TextContentComponent);
    const holder = holderOf(modal);
    const shellClosed = jasmine.createSpy('shell closed');
    const closed = jasmine.createSpy('closed');
    modal.modalComponent.closed.subscribe(shellClosed);
    modal.onClose(closed);

    browserBack();
    expect(router.url).toBe('/projects');
    expect(active()).toBe('opener');

    tick(600);
    expect(shellClosed).toHaveBeenCalledTimes(1);
    expect(closed).toHaveBeenCalledTimes(1);
    expect(holder.isConnected).toBeFalse();
    expect(service.modalRefs.length).toBe(0);
    expect(service.modalRefsOpen.value).toBeFalse();
    expect(document.body.classList.contains('nh-modal-open')).toBeFalse();
  }));

  it('closes an open modal on browser forward', fakeAsync(() => {
    browseToProject();
    browserBack();
    const modal = open(TextContentComponent);
    const closed = jasmine.createSpy('closed');
    modal.onClose(closed);

    browserForward();
    expect(router.url).toBe('/projects/1');

    tick(600);
    expect(closed).toHaveBeenCalledTimes(1);
    expect(service.modalRefs.length).toBe(0);
  }));

  it('keeps a modal open that opted out with closeOnBackNavigation: false', fakeAsync(() => {
    browseToProject();
    const modal = open(TextContentComponent, {closeOnBackNavigation: false});
    const closed = jasmine.createSpy('closed');
    modal.onClose(closed);
    expect(modal.modalComponent.closeOnBackNavigation).toBeFalse();

    browserBack();
    tick(600);

    expect(router.url).toBe('/projects');
    expect(closed).not.toHaveBeenCalled();
    expect(service.modalRefs).toEqual([modal]);
    expect(holderOf(modal).isConnected).toBeTrue();
  }));

  it('keeps a modal open that cannot close', fakeAsync(() => {
    browseToProject();
    const modal = open(TextContentComponent, {closeable: false});

    browserBack();
    tick(600);

    expect(service.modalRefs).toEqual([modal]);
    expect(holderOf(modal).isConnected).toBeTrue();
  }));

  it('keeps modals open on navigation started from code', fakeAsync(() => {
    browseToProject();
    const modal = open(TextContentComponent);

    void router.navigateByUrl('/projects');
    tick(600);

    expect(router.url).toBe('/projects');
    expect(service.modalRefs).toEqual([modal]);
  }));

  it('dismisses stacked modals from the top and leaves the ones that cannot close or opted out open', fakeAsync(() => {
    browseToProject();
    openRecording('lower');
    const pinned = openRecording('pinned', {closeOnBackNavigation: false});
    const busy = openRecording('busy', {closeable: false});
    openRecording('upper');

    browserBack();
    tick(600);

    expect(dismissals).toEqual(['upper', 'lower']);
    expect(service.modalRefs).toEqual([pinned, busy]);
  }));

  it('dismisses content without its own back navigation hook through onModalEscape', fakeAsync(() => {
    browseToProject();
    open(EscapeContentComponent);

    browserBack();
    tick(600);

    expect(dismissals).toEqual(['escape']);
    expect(service.modalRefs.length).toBe(0);
  }));

  it('leaves a modal that the content opens while it is dismissed open', fakeAsync(() => {
    browseToProject();
    const guarded = open(GuardedContentComponent);

    browserBack();
    root.detectChanges();
    tick(600);

    // The content asks before it discards its changes; the question belongs to this navigation.
    expect(dismissals).toEqual(['guarded']);
    expect(service.modalRefs.length).toBe(2);
    expect(service.modalRefs[0]).toBe(guarded);
    expect(service.modalRefs[1].contentComponent instanceof NhModalConfirmComponent).toBeTrue();
  }));

  it('keeps confirmation content open while its confirmation runs, then closes it through closeDialog', fakeAsync(() => {
    browseToProject();
    const modal = open(NhModalConfirmComponent, {title: 'Delete project'});
    const content = modal.contentComponent!;
    const closeDialog = spyOn(content, 'closeDialog').and.callThrough();
    let finishDelete: () => void = () => undefined;
    content.onConfirm = () => new Promise<void>(resolve => finishDelete = resolve);
    void content.confirm();

    browserBack();
    tick(600);
    expect(closeDialog).not.toHaveBeenCalled();
    expect(service.modalRefs).toEqual([modal]);

    finishDelete();
    tick();
    browserForward();

    expect(closeDialog).toHaveBeenCalledOnceWith({});
    expect(service.modalRefs.length).toBe(0);
  }));

  it('closes mutating content through onModalEscape, and keeps it open while it saves', () => {
    const onModalEscape = jasmine.createSpy('onModalEscape');

    NhModalMutateBaseTypeComponent.prototype.onModalBackNavigation.call({isSubmitting: true, onModalEscape} as never);
    expect(onModalEscape).not.toHaveBeenCalled();

    NhModalMutateBaseTypeComponent.prototype.onModalBackNavigation.call({isSubmitting: false, onModalEscape} as never);
    expect(onModalEscape).toHaveBeenCalledOnceWith();
  });

  it('finishes quietly when the page that hosts the modal is destroyed while it fades out', fakeAsync(() => {
    browseToProject();
    const page = TestBed.createComponent(ModalPageComponent);
    page.detectChanges();
    const modal = service.open(TextContentComponent, {title: 'Details', viewContainerRef: page.componentInstance.viewContainerRef});
    page.detectChanges();
    const closed = jasmine.createSpy('closed');
    modal.onClose(closed);
    const warn = spyOn(console, 'warn');

    browserBack();
    // The router replaces the page while the modal fades out.
    page.destroy();
    tick(600);

    expect(warn).not.toHaveBeenCalled();
    expect(closed).toHaveBeenCalledTimes(1);
    expect(service.modalRefs.length).toBe(0);
  }));

  it('skips a modal whose hosting page was already destroyed', fakeAsync(() => {
    browseToProject();
    const page = TestBed.createComponent(ModalPageComponent);
    page.detectChanges();
    service.open(TextContentComponent, {title: 'Details', viewContainerRef: page.componentInstance.viewContainerRef});
    page.detectChanges();
    page.destroy();
    const warn = spyOn(console, 'warn');

    expect(() => browserBack()).not.toThrow();
    tick(600);

    expect(warn).not.toHaveBeenCalled();
  }));
});
