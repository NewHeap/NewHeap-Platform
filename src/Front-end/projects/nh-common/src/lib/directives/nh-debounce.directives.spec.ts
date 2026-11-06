import {Component} from '@angular/core';
import {ComponentFixture, TestBed, fakeAsync, tick} from '@angular/core/testing';
import {FormsModule} from '@angular/forms';

import {NhButtonDebounceDirective, NhDebounceDirective} from './nh-debounce.directives';

@Component({
  selector: 'nh-test-debounce-host',
  template: `
    @if (shown) {
      <input [(ngModel)]="value" [nhDebounce]="300"
             (onDebounce)="debounced.push($event)" (preDebounce)="immediate.push($event)" />
    }`,
  standalone: false
})
class DebounceHostComponent {
  shown = true;
  value = 'initial';
  debounced: unknown[] = [];
  immediate: unknown[] = [];
}

@Component({
  selector: 'nh-test-button-debounce-host',
  template: `<button type="button" nhButtonDebounce [debounce]="300" (onDebounce)="clicks = clicks + 1">Save</button>`,
  standalone: false
})
class ButtonDebounceHostComponent {
  clicks = 0;
}

describe('NhDebounceDirective', () => {
  let fixture: ComponentFixture<DebounceHostComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [FormsModule],
      declarations: [NhDebounceDirective, NhButtonDebounceDirective, DebounceHostComponent, ButtonDebounceHostComponent]
    });
  });

  /** Creates the host and lets ngModel write its initial value, which it does after the first change detection. */
  function createWithInitialValue(): void {
    fixture = TestBed.createComponent(DebounceHostComponent);
    fixture.detectChanges();
    tick();
  }

  function type(value: string): void {
    const input = (fixture.nativeElement as HTMLElement).querySelector('input')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  it('reports the initial ngModel value through neither output', fakeAsync(() => {
    createWithInitialValue();
    fixture.detectChanges();
    tick(1000);

    expect(fixture.componentInstance.debounced).toEqual([]);
    expect(fixture.componentInstance.immediate).toEqual([]);
  }));

  it('reports typed values at once through preDebounce and the last one after the debounce time through onDebounce', fakeAsync(() => {
    createWithInitialValue();
    tick(1000);

    type('r');
    tick(100);
    type('ro');
    tick(100);
    type('road');

    expect(fixture.componentInstance.immediate).toEqual(['r', 'ro', 'road']);
    expect(fixture.componentInstance.debounced).toEqual([]);

    tick(300);

    expect(fixture.componentInstance.debounced).toEqual(['road']);
  }));

  it('reports a value typed while the initial value is still within the debounce time', fakeAsync(() => {
    createWithInitialValue();

    type('roadmap');
    tick(300);

    expect(fixture.componentInstance.debounced).toEqual(['roadmap']);
    expect(fixture.componentInstance.immediate).toEqual(['roadmap']);
  }));

  it('reports a value that the page writes through ngModel after the initial value', fakeAsync(() => {
    createWithInitialValue();
    tick(1000);

    fixture.componentInstance.value = 'from the route';
    fixture.detectChanges();
    tick(300);

    expect(fixture.componentInstance.immediate).toEqual(['from the route']);
    expect(fixture.componentInstance.debounced).toEqual(['from the route']);
  }));

  it('reports nothing once it is destroyed while a value waits for the debounce time', fakeAsync(() => {
    createWithInitialValue();
    tick(1000);
    fixture.componentInstance.debounced = [];

    type('roadmap');
    tick(100);
    fixture.componentInstance.shown = false;
    fixture.detectChanges();
    tick(1000);

    expect(fixture.componentInstance.debounced).toEqual([]);
  }));
});

describe('NhButtonDebounceDirective', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      declarations: [NhButtonDebounceDirective, ButtonDebounceHostComponent]
    });
  });

  it('reports rapid clicks once after the debounce time without writing to the console', fakeAsync(() => {
    const log = spyOn(console, 'log');
    const fixture = TestBed.createComponent(ButtonDebounceHostComponent);
    fixture.detectChanges();
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;

    button.click();
    tick(100);
    button.click();
    tick(100);
    button.click();

    expect(fixture.componentInstance.clicks).toBe(0);

    tick(300);

    expect(fixture.componentInstance.clicks).toBe(1);
    expect(log).not.toHaveBeenCalled();
  }));
});
