import {Component} from '@angular/core';
import {ComponentFixture, TestBed} from '@angular/core/testing';
import {TranslateService, provideTranslateService} from '@ngx-translate/core';
import {CollectionHttpResponse} from '../../models/http.models';
import {NhPagerComponent, NhPagerPageChange, NhPagerSource} from './component';

@Component({
  selector: 'nh-test-pager-host',
  standalone: true,
  imports: [NhPagerComponent],
  template: `
    <nh-pager
      [response]="response"
      [disabled]="disabled"
      [hideSinglePage]="hideSinglePage"
      [label]="label"
      (pageChange)="changes.push($event)" />`
})
class PagerHostComponent {
  response: NhPagerSource | undefined = new CollectionHttpResponse<unknown>({page: 1, itemsPerPage: 10, totalCount: 43, resultCount: 10});
  disabled = false;
  hideSinglePage = true;
  label: string | undefined;
  changes: NhPagerPageChange[] = [];
}

describe('NhPagerComponent', () => {
  let fixture: ComponentFixture<PagerHostComponent>;
  let host: PagerHostComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [PagerHostComponent],
      providers: [provideTranslateService({fallbackLang: 'en'})]
    });

    fixture = TestBed.createComponent(PagerHostComponent);
    host = fixture.componentInstance;
  });

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function nav(): HTMLElement | null {
    return element().querySelector('nav');
  }

  function buttons(): HTMLButtonElement[] {
    return Array.from(element().querySelectorAll<HTMLButtonElement>('nav button'));
  }

  function pageButtons(): HTMLButtonElement[] {
    return Array.from(element().querySelectorAll<HTMLButtonElement>('.nh-pager__page'));
  }

  function previous(): HTMLButtonElement {
    return buttons()[0];
  }

  function next(): HTMLButtonElement {
    return buttons()[buttons().length - 1];
  }

  function setResponse(response: NhPagerSource): void {
    host.response = response;
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
  }

  it('renders a labelled navigation with named previous, page and next buttons', () => {
    fixture.detectChanges();

    expect(nav()?.getAttribute('aria-label')).toBe('Pagination');
    expect(previous().getAttribute('aria-label')).toBe('Previous page');
    expect(previous().getAttribute('title')).toBe('Previous page');
    expect(next().getAttribute('aria-label')).toBe('Next page');
    expect(pageButtons().map(button => button.textContent?.trim())).toEqual(['1', '2', '3', '4', '5']);
    expect(pageButtons()[1].getAttribute('aria-label')).toBe('Page 2');
    expect(buttons().every(button => button.type === 'button')).toBeTrue();
  });

  it('marks only the current page with aria-current and shows its range', () => {
    setResponse({page: 2, itemsPerPage: 10, totalCount: 43, resultCount: 10});

    const current = pageButtons().filter(button => button.getAttribute('aria-current') === 'page');
    expect(current.map(button => button.textContent?.trim())).toEqual(['2']);
    expect(element().querySelector('.nh-pager__range')?.textContent?.trim()).toBe('11–20 of 43');
    expect(element().querySelector('.nh-pager__status')?.textContent?.trim()).toBe('Page 2 of 5');
  });

  it('uses resultCount for the range of a partial last page', () => {
    setResponse({page: 5, itemsPerPage: 10, totalCount: 43, resultCount: 3});

    expect(element().querySelector('.nh-pager__range')?.textContent?.trim()).toBe('41–43 of 43');
  });

  it('marks previous on the first page and next on the last page as disabled without removing focusability', () => {
    fixture.detectChanges();

    expect(previous().getAttribute('aria-disabled')).toBe('true');
    expect(previous().disabled).toBeFalse();
    expect(next().hasAttribute('aria-disabled')).toBeFalse();

    setResponse({page: 5, itemsPerPage: 10, totalCount: 43, resultCount: 3});

    expect(previous().hasAttribute('aria-disabled')).toBeFalse();
    expect(next().getAttribute('aria-disabled')).toBe('true');
  });

  it('emits the requested page with the current page size, as setPage expects', () => {
    setResponse({page: 2, itemsPerPage: 10, totalCount: 43, resultCount: 10});

    next().click();
    previous().click();
    pageButtons()[3].click();

    expect(host.changes).toEqual([
      {page: 3, itemsPerPage: 10},
      {page: 1, itemsPerPage: 10},
      {page: 4, itemsPerPage: 10}
    ]);
  });

  it('ignores the current page, the edges and every request while disabled', () => {
    fixture.detectChanges();

    previous().click();
    pageButtons()[0].click();

    host.disabled = true;
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
    next().click();
    pageButtons()[2].click();

    expect(host.changes).toEqual([]);
    expect(buttons().every(button => button.getAttribute('aria-disabled') === 'true')).toBeTrue();
  });

  it('collapses distant pages into gaps around the current page', () => {
    setResponse({page: 10, itemsPerPage: 10, totalCount: 200, resultCount: 10});

    const items = Array.from(element().querySelectorAll('.nh-pager__list > li'))
      .filter(item => item.classList.contains('nh-pager__page-item') || item.classList.contains('nh-pager__gap'))
      .map(item => item.textContent?.trim());
    expect(items).toEqual(['1', '…', '9', '10', '11', '…', '20']);
    expect(element().querySelector('.nh-pager__gap')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('keeps seven slots while the current page moves and never hides a single page in a gap', () => {
    const slots = () => Array.from(element().querySelectorAll('.nh-pager__page-item, .nh-pager__gap'))
      .map(item => item.textContent?.trim());

    setResponse({page: 4, itemsPerPage: 10, totalCount: 70, resultCount: 10});
    expect(slots()).toEqual(['1', '2', '3', '4', '5', '6', '7']);

    setResponse({page: 4, itemsPerPage: 10, totalCount: 200, resultCount: 10});
    expect(slots()).toEqual(['1', '2', '3', '4', '5', '…', '20']);

    setResponse({page: 5, itemsPerPage: 10, totalCount: 200, resultCount: 10});
    expect(slots()).toEqual(['1', '…', '4', '5', '6', '…', '20']);

    setResponse({page: 17, itemsPerPage: 10, totalCount: 200, resultCount: 10});
    expect(slots()).toEqual(['1', '…', '16', '17', '18', '19', '20']);
  });

  it('renders nothing for a single page unless hideSinglePage is off', () => {
    setResponse({page: 1, itemsPerPage: 10, totalCount: 4, resultCount: 4});
    expect(nav()).toBeNull();

    host.hideSinglePage = false;
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();

    expect(nav()).not.toBeNull();
    expect(previous().getAttribute('aria-disabled')).toBe('true');
    expect(next().getAttribute('aria-disabled')).toBe('true');
  });

  it('announces a changed page politely, but not the first page', () => {
    fixture.detectChanges();
    const liveRegion = element().querySelector('.nh-pager__announcement')!;

    expect(liveRegion.getAttribute('aria-live')).toBe('polite');
    expect(liveRegion.textContent?.trim()).toBe('');

    setResponse({page: 3, itemsPerPage: 10, totalCount: 43, resultCount: 10});

    expect(liveRegion.textContent?.trim()).toBe('Page 3 of 5');
  });

  it('moves focus between its buttons with the arrow keys, Home and End', () => {
    fixture.detectChanges();
    expect(document.body.contains(element())).toBeTrue();

    // Hidden page buttons of the compact layout are skipped, so compare with the visible ones.
    const visible = buttons().filter(button => button.getClientRects().length > 0);
    const press = (target: HTMLElement, key: string) => {
      const event = new KeyboardEvent('keydown', {key, bubbles: true, cancelable: true});
      target.dispatchEvent(event);
      return event;
    };

    visible[0].focus();
    expect(press(visible[0], 'ArrowRight').defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(visible[1]);

    press(visible[1], 'ArrowLeft');
    expect(document.activeElement).toBe(visible[0]);

    press(visible[0], 'End');
    expect(document.activeElement).toBe(next());

    press(next(), 'Home');
    expect(document.activeElement).toBe(previous());

    expect(press(previous(), 'Enter').defaultPrevented).toBeFalse();
  });

  it('prefers the label input and the application translation over the bundled text', () => {
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('en', {'nh-common': {pager: {next: 'Forward'}}});
    translate.use('en');
    host.label = 'Project pages';
    fixture.detectChanges();

    expect(nav()?.getAttribute('aria-label')).toBe('Project pages');
    expect(next().getAttribute('aria-label')).toBe('Forward');
    expect(previous().getAttribute('aria-label')).toBe('Previous page');
  });
});
