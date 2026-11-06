import {ChangeDetectionStrategy, Component, ViewChild} from '@angular/core';
import {ComponentFixture, TestBed, fakeAsync, flush, tick} from '@angular/core/testing';
import {FormControl, FormsModule, ReactiveFormsModule} from '@angular/forms';
import {provideTranslateService} from '@ngx-translate/core';
import {NgxBootstrapMultiselectModule} from 'ngx-bootstrap-multiselect';
import {Subject, of} from 'rxjs';

import {CollectionHttpResponse} from '../../models/http.models';
import {NhPageSizeComponent} from '../nh-page-size/page-size.component';
import {DefaultMultiSelectSettings, NhFormDropDownComponent, NhFormDropDownSettings} from './form-dropdown.component';

type Item = {id: any; name: string; image?: string; inactive?: boolean};

const catalogue: Item[] = [{id: 'a', name: 'Alpha'}, {id: 'b', name: 'Beta'}];

function response(items: Item[]): CollectionHttpResponse<Item> {
  return new CollectionHttpResponse<Item>({items, resultCount: items.length, itemsPerPage: 25});
}

function listSettings(items: Item[], extra: Partial<NhFormDropDownSettings> = {}): NhFormDropDownSettings {
  return new NhFormDropDownSettings({lazyLoad: false, loadLambda: () => of(items), ...extra});
}

function deferredSettingsWithSynchronousLookup(): NhFormDropDownSettings {
  return new NhFormDropDownSettings({
    lazyLoad: true,
    deferLazyLoadUntilOpened: true,
    lazyLoadLambda: () => of(response(catalogue)),
    // A cached lookup, as an application store returns it, answers synchronously.
    selectedLazyLoadLambda: (_options, value) => of(response(catalogue.filter(x => x.id === value)))
  });
}

@Component({
  selector: 'nh-dropdown-reactive-host',
  template: `<nh-form-dropdown #dropdown [formControl]="control" [settings]="settings"></nh-form-dropdown>`,
  standalone: false
})
class ReactiveHostComponent {
  @ViewChild('dropdown', {static: true}) dropdown!: NhFormDropDownComponent;
  control = new FormControl<any>(null);
  settings!: NhFormDropDownSettings;
}

@Component({
  selector: 'nh-dropdown-onpush-parent',
  template: `<nh-form-dropdown #dropdown [(ngModel)]="value" [settings]="settings"></nh-form-dropdown>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false
})
class OnPushParentComponent {
  @ViewChild('dropdown', {static: true}) dropdown!: NhFormDropDownComponent;
  value: any = null;
  lazyResponse = new Subject<CollectionHttpResponse<Item>>();
  settings = new NhFormDropDownSettings({
    lazyLoad: true,
    deferLazyLoadUntilOpened: true,
    lazyLoadLambda: () => this.lazyResponse
  });
}

@Component({
  selector: 'nh-dropdown-onpush-host',
  template: `<nh-dropdown-onpush-parent></nh-dropdown-onpush-parent>`,
  standalone: false
})
class OnPushHostComponent {
  @ViewChild(OnPushParentComponent, {static: true}) parent!: OnPushParentComponent;
}

@Component({
  selector: 'nh-dropdown-template-host',
  template: `
    <button type="button" id="outside" (click)="dropdown.toggleDropdown()">Edit</button>
    <nh-form-dropdown #dropdown [(ngModel)]="value" [settings]="settings"
      (opened)="events.push('opened')" (closed)="events.push('closed')"
      (ngModelChange)="events.push('value')"></nh-form-dropdown>`,
  standalone: false
})
class TemplateHostComponent {
  @ViewChild('dropdown', {static: true}) dropdown!: NhFormDropDownComponent;
  value: any = null;
  events: string[] = [];
  settings!: NhFormDropDownSettings;
}

@Component({
  selector: 'nh-page-size-host',
  template: `<nh-page-size [(ngModel)]="pageSize" (ngModelChange)="changes.push($event)"></nh-page-size>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false
})
class PageSizeHostComponent {
  pageSize = 25;
  changes: number[] = [];
}

describe('NhFormDropDownComponent in a form', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [FormsModule, ReactiveFormsModule, NgxBootstrapMultiselectModule],
      declarations: [
        NhFormDropDownComponent, NhPageSizeComponent, ReactiveHostComponent, OnPushParentComponent,
        OnPushHostComponent, TemplateHostComponent, PageSizeHostComponent
      ],
      providers: [provideTranslateService()]
    });
  });

  function element(fixture: ComponentFixture<unknown>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function menu(fixture: ComponentFixture<unknown>): HTMLElement | null {
    return element(fixture).querySelector('.dropdown-menu');
  }

  function items(fixture: ComponentFixture<unknown>): HTMLElement[] {
    return Array.from(element(fixture).querySelectorAll<HTMLElement>('.dropdown-menu a.dropdown-item:not(.empty):not(.check-control)'));
  }

  function toggleButton(fixture: ComponentFixture<unknown>): HTMLButtonElement {
    return element(fixture).querySelector<HTMLButtonElement>('button.dropdown-toggle')!;
  }

  function open(fixture: ComponentFixture<unknown>) {
    toggleButton(fixture).click();
    fixture.detectChanges();
  }

  function choose(fixture: ComponentFixture<unknown>, index: number) {
    items(fixture)[index].click();
    // The inner list applies a selection in a timeout.
    tick();
    fixture.detectChanges();
  }

  // Forms write values in microtasks, and every write can bind a value one component deeper.
  function settle(fixture: ComponentFixture<unknown>) {
    for (let round = 0; round < 4; round++) {
      fixture.detectChanges();
      flush();
    }

    fixture.detectChanges();
  }

  describe('value', () => {
    it('keeps an external setValue when the selected option resolves synchronously', fakeAsync(() => {
      const fixture = TestBed.createComponent(ReactiveHostComponent);
      fixture.componentInstance.control.setValue('a');
      fixture.componentInstance.settings = deferredSettingsWithSynchronousLookup();
      fixture.detectChanges();
      flush();
      fixture.detectChanges();

      const history: any[] = [];
      fixture.componentInstance.control.valueChanges.subscribe(x => history.push(x));
      fixture.componentInstance.control.setValue('b');
      expect(() => fixture.detectChanges()).not.toThrow();
      flush();
      const perCycle: any[] = [fixture.componentInstance.control.value];
      for (let cycle = 0; cycle < 6; cycle++) {
        fixture.detectChanges();
        flush();
        perCycle.push(fixture.componentInstance.control.value);
      }

      expect(history).toEqual(['b']);
      expect(perCycle).toEqual(['b', 'b', 'b', 'b', 'b', 'b', 'b']);
      expect(toggleButton(fixture).textContent).toContain('Beta');
    }));

    it('keeps an external setValue on an empty control', fakeAsync(() => {
      const fixture = TestBed.createComponent(ReactiveHostComponent);
      fixture.componentInstance.settings = deferredSettingsWithSynchronousLookup();
      fixture.detectChanges();
      flush();
      fixture.detectChanges();

      fixture.componentInstance.control.setValue('b');
      expect(() => fixture.detectChanges()).not.toThrow();
      flush();
      const perCycle: any[] = [fixture.componentInstance.control.value];
      for (let cycle = 0; cycle < 6; cycle++) {
        fixture.detectChanges();
        flush();
        perCycle.push(fixture.componentInstance.control.value);
      }

      expect(perCycle).toEqual(['b', 'b', 'b', 'b', 'b', 'b', 'b']);
    }));

    it('keeps an external setValue when the selected option resolves later', fakeAsync(() => {
      const fixture = TestBed.createComponent(ReactiveHostComponent);
      const selected = new Subject<CollectionHttpResponse<Item>>();
      fixture.componentInstance.settings = new NhFormDropDownSettings({
        lazyLoad: true,
        deferLazyLoadUntilOpened: true,
        lazyLoadLambda: () => of(response(catalogue)),
        selectedLazyLoadLambda: () => selected
      });
      fixture.detectChanges();
      flush();

      fixture.componentInstance.control.setValue('b');
      fixture.detectChanges();
      flush();
      selected.next(response([catalogue[1]]));
      selected.complete();
      fixture.detectChanges();
      flush();
      fixture.detectChanges();

      expect(fixture.componentInstance.control.value).toBe('b');
      expect(toggleButton(fixture).textContent).toContain('Beta');
    }));

    it('takes a user choice and marks the control dirty once', fakeAsync(() => {
      const fixture = TestBed.createComponent(ReactiveHostComponent);
      fixture.componentInstance.control.setValue('a');
      fixture.componentInstance.settings = deferredSettingsWithSynchronousLookup();
      fixture.detectChanges();
      flush();
      fixture.detectChanges();
      const control = fixture.componentInstance.control;
      const history: any[] = [];
      control.valueChanges.subscribe(x => history.push(x));

      open(fixture);
      flush();
      fixture.detectChanges();
      choose(fixture, 1);
      flush();
      fixture.detectChanges();

      expect(control.value).toBe('b');
      expect(history).toEqual(['b']);
      expect(control.dirty).toBeTrue();
    }));

    it('takes check all and uncheck all in multi-select mode', fakeAsync(() => {
      const fixture = TestBed.createComponent(ReactiveHostComponent);
      fixture.componentInstance.settings = listSettings(catalogue, {
        multiSelectSettings: new DefaultMultiSelectSettings({selectionLimit: 0, showCheckAll: true, showUncheckAll: true})
      });
      fixture.detectChanges();
      flush();
      fixture.detectChanges();

      open(fixture);
      element(fixture).querySelector<HTMLElement>('a.check-control-check')!.click();
      fixture.detectChanges();
      expect(fixture.componentInstance.control.value).toEqual(['a', 'b']);

      element(fixture).querySelector<HTMLElement>('a.check-control-uncheck')!.click();
      fixture.detectChanges();
      expect(fixture.componentInstance.control.value).toEqual([]);
    }));

    it('does not echo setValue or reset back as a change', fakeAsync(() => {
      const fixture = TestBed.createComponent(ReactiveHostComponent);
      fixture.componentInstance.settings = listSettings([{id: 1, name: 'One'}]);
      fixture.detectChanges();
      flush();
      const control = fixture.componentInstance.control;
      let valueChanges = 0;
      control.valueChanges.subscribe(() => valueChanges++);

      control.setValue(1);
      expect(control.dirty).toBeFalse();
      expect(valueChanges).toBe(1);

      control.markAsDirty();
      control.reset();
      expect(control.dirty).toBeFalse();
      expect(control.value).toBeNull();
    }));

    it('does not emit ngModelChange when a template-driven form initializes', fakeAsync(() => {
      const fixture = TestBed.createComponent(TemplateHostComponent);
      fixture.componentInstance.settings = listSettings([{id: 1, name: 'One'}]);
      fixture.componentInstance.value = 1;
      settle(fixture);

      expect(fixture.componentInstance.events).toEqual([]);
      expect(toggleButton(fixture).textContent).toContain('One');
    }));

    it('keeps 0 and false as single-select choices', fakeAsync(() => {
      const fixture = TestBed.createComponent(ReactiveHostComponent);
      fixture.componentInstance.settings = listSettings([{id: 0, name: 'Zero'}, {id: false, name: 'No'}, {id: 1, name: 'One'}]);
      fixture.detectChanges();
      flush();
      fixture.detectChanges();
      const control = fixture.componentInstance.control;

      open(fixture);
      choose(fixture, 0);
      expect(control.value).toBe(0);

      choose(fixture, 1);
      expect(control.value).toBeFalse();

      control.setValue(1);
      fixture.componentInstance.dropdown.onModelChange([0]);
      expect(control.value).toBe(0);

      fixture.componentInstance.dropdown.onModelChange([]);
      expect(control.value).toBeUndefined();
    }));
  });

  describe('options', () => {
    it('carries image and classes in every loading mode', fakeAsync(() => {
      const source: Item[] = [{id: 1, name: 'One', image: '/one.png', inactive: true}, {id: 2, name: 'Two'}];
      const lambdas: Partial<NhFormDropDownSettings> = {
        imageGetLambda: (x: Item) => x.image as never,
        classesGetLambda: (x: Item) => x.inactive ? 'text-muted' : undefined
      };

      const lazy = TestBed.createComponent(ReactiveHostComponent);
      lazy.componentInstance.settings = new NhFormDropDownSettings({lazyLoad: true, lazyLoadLambda: () => of(response(source)), ...lambdas});
      lazy.detectChanges();
      expect(lazy.componentInstance.dropdown.options[0]).toEqual(jasmine.objectContaining({image: '/one.png', classes: 'text-muted'}));

      const selected = TestBed.createComponent(ReactiveHostComponent);
      selected.componentInstance.control.setValue(1);
      selected.componentInstance.settings = new NhFormDropDownSettings({
        lazyLoad: true,
        deferLazyLoadUntilOpened: true,
        selectedLazyLoadLambda: () => of(response([source[0]])),
        ...lambdas
      });
      selected.detectChanges();
      expect(selected.componentInstance.dropdown.options[0]).toEqual(jasmine.objectContaining({image: '/one.png', classes: 'text-muted'}));

      const list = TestBed.createComponent(ReactiveHostComponent);
      list.componentInstance.settings = listSettings(source, lambdas);
      list.detectChanges();
      flush();
      list.detectChanges();
      expect(list.componentInstance.dropdown.options[0]).toEqual(jasmine.objectContaining({image: '/one.png', classes: 'text-muted'}));

      open(list);
      expect(items(list)[0].classList).toContain('text-muted');
      expect(items(list)[1].classList).not.toContain('text-muted');
    }));

    it('leaves classes out when no classesGetLambda is configured', () => {
      const fixture = TestBed.createComponent(ReactiveHostComponent);
      fixture.componentInstance.settings = new NhFormDropDownSettings({lazyLoad: true, lazyLoadLambda: () => of(response([{id: 1, name: 'One'}]))});
      fixture.detectChanges();

      expect(fixture.componentInstance.dropdown.options).toEqual([{id: 1, name: 'One', image: undefined}]);
    });

    it('renders the lazy response inside an OnPush parent', fakeAsync(() => {
      const fixture = TestBed.createComponent(OnPushHostComponent);
      fixture.detectChanges();
      flush();
      fixture.detectChanges();
      const parent = fixture.componentInstance.parent;

      open(fixture);
      expect(menu(fixture)).not.toBeNull();

      parent.lazyResponse.next(response([{id: 1, name: 'One'}, {id: 2, name: 'Two'}]));
      parent.lazyResponse.complete();
      fixture.detectChanges();
      tick(1000);
      fixture.detectChanges();

      expect(parent.dropdown.options.length).toBe(2);
      expect(items(fixture).length).toBe(2);
      expect(element(fixture).querySelector('.dropdown-menu a.empty')).toBeNull();
    }));
  });

  describe('opening and closing', () => {
    it('stays open when toggleDropdown() is called from a click outside the dropdown', fakeAsync(() => {
      const fixture = TestBed.createComponent(TemplateHostComponent);
      fixture.componentInstance.settings = listSettings([{id: 1, name: 'One'}, {id: 2, name: 'Two'}]);
      fixture.detectChanges();
      flush();
      fixture.detectChanges();

      element(fixture).querySelector<HTMLButtonElement>('#outside')!.click();
      tick();
      fixture.detectChanges();

      expect(fixture.componentInstance.events).toEqual(['opened']);
      expect(fixture.componentInstance.dropdown.getDropdownComponent()!.isVisible).toBeTrue();
      expect(menu(fixture)).not.toBeNull();

      element(fixture).querySelector<HTMLButtonElement>('#outside')!.click();
      fixture.detectChanges();
      expect(fixture.componentInstance.events).toEqual(['opened', 'closed']);
      expect(menu(fixture)).toBeNull();
    }));

    it('opens and closes at once when toggleDropdown() is called from code', fakeAsync(() => {
      const fixture = TestBed.createComponent(TemplateHostComponent);
      fixture.componentInstance.settings = listSettings([{id: 1, name: 'One'}, {id: 2, name: 'Two'}]);
      fixture.detectChanges();
      flush();
      fixture.detectChanges();

      fixture.componentInstance.dropdown.toggleDropdown();
      fixture.detectChanges();
      expect(menu(fixture)).not.toBeNull();

      fixture.componentInstance.dropdown.closeDropdown();
      fixture.detectChanges();
      expect(menu(fixture)).toBeNull();
    }));

    it('emits closed after the chosen value with closeOnSelect', fakeAsync(() => {
      const fixture = TestBed.createComponent(TemplateHostComponent);
      fixture.componentInstance.settings = listSettings([{id: 1, name: 'One'}, {id: 2, name: 'Two'}]);
      fixture.componentInstance.settings.multiSelectSettings.closeOnSelect = true;
      fixture.detectChanges();
      flush();
      fixture.detectChanges();
      fixture.componentInstance.events = [];

      open(fixture);
      choose(fixture, 1);
      flush();

      expect(fixture.componentInstance.events).toEqual(['opened', 'value', 'closed']);
      expect(fixture.componentInstance.value).toBe(2);
    }));

    it('closes the open list on Escape and returns focus to the toggle button', fakeAsync(() => {
      const fixture = TestBed.createComponent(TemplateHostComponent);
      fixture.componentInstance.settings = listSettings([{id: 1, name: 'One'}, {id: 2, name: 'Two'}]);
      fixture.detectChanges();
      flush();
      fixture.detectChanges();
      open(fixture);

      const escape = new KeyboardEvent('keydown', {key: 'Escape', bubbles: true, cancelable: true});
      menu(fixture)!.dispatchEvent(escape);
      fixture.detectChanges();

      expect(menu(fixture)).toBeNull();
      expect(escape.defaultPrevented).toBeTrue();
      expect(document.activeElement).toBe(toggleButton(fixture));
      expect(fixture.componentInstance.events).toEqual(['opened', 'closed']);

      const ignored = new KeyboardEvent('keydown', {key: 'Escape', bubbles: true, cancelable: true});
      toggleButton(fixture).dispatchEvent(ignored);
      expect(ignored.defaultPrevented).toBeFalse();
    }));
  });

  describe('NhPageSizeComponent', () => {
    it('shows the bound page size and reports only the user choice', fakeAsync(() => {
      const fixture = TestBed.createComponent(PageSizeHostComponent);
      settle(fixture);

      expect(fixture.componentInstance.changes).toEqual([]);
      expect(toggleButton(fixture).textContent).toContain('25');

      open(fixture);
      choose(fixture, 2);
      flush();
      fixture.detectChanges();

      expect(fixture.componentInstance.changes).toEqual([50]);
      expect(fixture.componentInstance.pageSize).toBe(50);
      expect(toggleButton(fixture).textContent).toContain('50');
    }));
  });
});
