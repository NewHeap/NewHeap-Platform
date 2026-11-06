import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  numberAttribute,
  booleanAttribute,
  output,
  signal,
  untracked
} from '@angular/core';
import {TranslateService} from '@ngx-translate/core';
import {ISimpleCollectionHttpResponse} from '../../models/http.models';
import {nhCommonText} from '../../i18n/nh-common-translations';
import {NhCommonTextPipe} from '../../i18n/nh-common-text.pipe';
import {NhCommonIconComponent} from '../nh-common-icon/component';

/**
 * The paging values that `nh-pager` reads: a `CollectionHttpResponse`, the `collectionResponse` of
 * an `NhCollectionBaseComponent`, or a plain object with the same fields.
 */
export type NhPagerSource = Pick<ISimpleCollectionHttpResponse<unknown>, 'page' | 'itemsPerPage' | 'totalCount' | 'resultCount'>;

/** The page that `nh-pager` asks for; `NhCollectionTypeBaseComponent.setPage` accepts it as is. */
export interface NhPagerPageChange {
  page: number;
  itemsPerPage: number;
}

type NhPagerItem =
  | {kind: 'page'; key: string; page: number}
  | {kind: 'gap'; key: string};

/**
 * Accessible paging for a collection. Reads `page`, `itemsPerPage`, `totalCount` and `resultCount`
 * from `response` and emits the requested page through `pageChange`, so a collection component
 * binds `(pageChange)="setPage($event)"`. It renders a `<nav>` with named previous, next and page
 * buttons, marks the current page with `aria-current="page"`, announces a changed page politely,
 * moves focus between its buttons with the arrow keys, Home and End, and collapses to
 * "Page 2 of 5" when its container is narrow.
 */
@Component({
  selector: 'nh-pager',
  standalone: true,
  imports: [NhCommonTextPipe, NhCommonIconComponent],
  templateUrl: './component.html',
  styleUrl: './component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NhPagerComponent {
  private readonly translate = inject(TranslateService, {optional: true});
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);

  /** The paging values of the current response. */
  readonly response = input<NhPagerSource | null | undefined>(undefined);
  /** Ignores page requests, for example while the next page loads. The buttons keep their focus. */
  readonly disabled = input(false, {transform: booleanAttribute});
  /** The accessible name of the navigation. Defaults to `nh-common.pager.label` ("Pagination"). */
  readonly label = input<string | undefined>(undefined);
  /** How many page buttons the pager shows on each side of the current page. */
  readonly siblingCount = input(1, {transform: numberAttribute});
  /** Shows the "11–20 of 43" range of the current page. */
  readonly showRange = input(true, {transform: booleanAttribute});
  /** Renders nothing while all results fit on one page. */
  readonly hideSinglePage = input(true, {transform: booleanAttribute});

  /** The requested page with the current page size. */
  readonly pageChange = output<NhPagerPageChange>();

  readonly itemsPerPage = computed(() => {
    const value = Math.floor(this.response()?.itemsPerPage ?? 0);
    return value > 0 ? value : 10;
  });
  readonly totalCount = computed(() => Math.max(0, Math.floor(this.response()?.totalCount ?? 0)));
  readonly pageCount = computed(() => Math.max(1, Math.ceil(this.totalCount() / this.itemsPerPage())));
  readonly currentPage = computed(() => {
    const page = Math.floor(this.response()?.page ?? 1);
    return Math.min(Math.max(1, page), this.pageCount());
  });
  readonly visible = computed(() => !this.hideSinglePage() || this.pageCount() > 1);
  readonly range = computed(() => {
    const total = this.totalCount();
    if (total === 0) {
      return {from: 0, to: 0, total};
    }

    const from = (this.currentPage() - 1) * this.itemsPerPage() + 1;
    const resultCount = this.response()?.resultCount;
    const to = resultCount !== undefined && resultCount !== null && resultCount > 0
      ? from + resultCount - 1
      : this.currentPage() * this.itemsPerPage();

    return {from: Math.min(from, total), to: Math.min(to, total), total};
  });
  readonly items = computed<NhPagerItem[]>(() => this.buildItems(this.currentPage(), this.pageCount(), this.siblingCount()));

  /** The text of the polite live region; empty until the page changes after the first render. */
  readonly announcement = signal('');

  constructor() {
    let previous: string | undefined;

    effect(() => {
      const current = `${this.currentPage()}/${this.pageCount()}`;
      if (previous !== undefined && previous !== current) {
        untracked(() => this.announcement.set(this.statusText()));
      }
      previous = current;
    });
  }

  statusText(): string {
    return nhCommonText(this.translate, 'nh-common.pager.status', {
      page: this.currentPage(),
      pageCount: this.pageCount()
    });
  }

  goTo(page: number): void {
    if (this.disabled()) {
      return;
    }

    const target = Math.min(Math.max(1, page), this.pageCount());
    if (target === this.currentPage()) {
      return;
    }

    this.pageChange.emit({page: target, itemsPerPage: this.itemsPerPage()});
  }

  /** Arrow keys, Home and End move focus between the visible buttons; Tab keeps working as usual. */
  onKeydown(event: KeyboardEvent): void {
    const buttons = Array.from(this.host.nativeElement.querySelectorAll<HTMLButtonElement>('button'))
      .filter(button => button.getClientRects().length > 0);
    const index = buttons.indexOf(event.target as HTMLButtonElement);
    if (index < 0 || buttons.length === 0) {
      return;
    }

    let next: number;
    switch (event.key) {
      case 'ArrowLeft':
        next = Math.max(0, index - 1);
        break;
      case 'ArrowRight':
        next = Math.min(buttons.length - 1, index + 1);
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = buttons.length - 1;
        break;
      default:
        return;
    }

    event.preventDefault();
    buttons[next].focus();
  }

  /**
   * The first and last page, the current page with its siblings and a gap on either side. The
   * number of slots stays the same while the current page moves, so the buttons do not jump, and a
   * gap never hides a single page.
   */
  private buildItems(current: number, pageCount: number, siblingCount: number): NhPagerItem[] {
    const siblings = Number.isFinite(siblingCount) ? Math.max(0, Math.floor(siblingCount)) : 1;
    const slots = siblings * 2 + 5;
    const page = (value: number): NhPagerItem => ({kind: 'page', key: `page-${value}`, page: value});
    const pages = (from: number, to: number): NhPagerItem[] => {
      return Array.from({length: to - from + 1}, (_, index) => page(from + index));
    };

    if (pageCount <= slots) {
      return pages(1, pageCount);
    }

    const leftSibling = Math.max(current - siblings, 1);
    const rightSibling = Math.min(current + siblings, pageCount);
    const showStartGap = leftSibling > 3;
    const showEndGap = rightSibling < pageCount - 2;
    const edgeLength = slots - 2;

    if (!showStartGap) {
      return [...pages(1, edgeLength), {kind: 'gap', key: 'gap-end'}, page(pageCount)];
    }

    if (!showEndGap) {
      return [page(1), {kind: 'gap', key: 'gap-start'}, ...pages(pageCount - edgeLength + 1, pageCount)];
    }

    return [
      page(1),
      {kind: 'gap', key: 'gap-start'},
      ...pages(leftSibling, rightSibling),
      {kind: 'gap', key: 'gap-end'},
      page(pageCount)
    ];
  }
}
