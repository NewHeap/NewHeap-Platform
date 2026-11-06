import {Component} from '@angular/core';
import {ComponentFixture, TestBed, fakeAsync, tick} from '@angular/core/testing';
import {FormsModule} from '@angular/forms';
import {TranslatePipe, TranslateService, provideTranslateService} from '@ngx-translate/core';

import {NhDebounceDirective} from '../../directives/nh-debounce.directives';
import {NhSearchInputComponent} from './search-input.component';

@Component({
  selector: 'nh-test-search-host',
  template: `
    <label for="project-search">Search projects</label>
    <nh-search-input [value]="value" [label]="label" [inputId]="inputId" (onDebounce)="emitted.push($event)"></nh-search-input>`,
  standalone: false
})
class SearchHostComponent {
  value = 'initial';
  label: string | undefined;
  inputId: string | undefined;
  emitted: string[] = [];
}

describe('NhSearchInputComponent', () => {
  let fixture: ComponentFixture<SearchHostComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [FormsModule, TranslatePipe],
      declarations: [NhSearchInputComponent, NhDebounceDirective, SearchHostComponent],
      providers: [provideTranslateService({fallbackLang: 'en'})]
    });

    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('en', {general: {search: 'Search'}});
    translate.use('en');
    fixture = TestBed.createComponent(SearchHostComponent);
  });

  function input(): HTMLInputElement {
    return (fixture.nativeElement as HTMLElement).querySelector('nh-search-input input')!;
  }

  it('does not report its initial value through onDebounce', fakeAsync(() => {
    fixture.detectChanges();
    tick();
    fixture.detectChanges();
    tick(600);

    expect(fixture.componentInstance.emitted).toEqual([]);
  }));

  it('reports typed text once after the debounce time', fakeAsync(() => {
    fixture.detectChanges();
    tick(600);

    input().value = 'road';
    input().dispatchEvent(new Event('input'));
    tick(200);
    input().value = 'roadmap';
    input().dispatchEvent(new Event('input'));
    tick(500);

    expect(fixture.componentInstance.emitted).toEqual(['roadmap']);
  }));

  it('names the input with the translated search text by default and keeps its placeholder', () => {
    fixture.detectChanges();

    expect(input().getAttribute('aria-label')).toBe('Search');
    expect(input().getAttribute('placeholder')).toBe('Search...');
    expect(input().type).toBe('text');
    expect(input().classList).toContain('form-control');
    expect(input().hasAttribute('id')).toBeFalse();
  });

  it('uses the label input as the accessible name', () => {
    fixture.componentInstance.label = 'Search projects';
    fixture.detectChanges();

    expect(input().getAttribute('aria-label')).toBe('Search projects');
    expect(input().getAttribute('placeholder')).toBe('Search...');
  });

  it('puts inputId on the input so a page label can point at it', () => {
    fixture.componentInstance.inputId = 'project-search';
    fixture.detectChanges();

    expect(input().id).toBe('project-search');
    expect(Array.from(input().labels ?? []).map(label => label.textContent)).toEqual(['Search projects']);
  });
});
