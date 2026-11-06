import { Component, EventEmitter, Input, Output, ViewChild, ElementRef } from '@angular/core';

/**
 * A search field that reports what the user typed once typing pauses for 500 ms. It does not report
 * its initial `value`, so a page loads its initial data itself.
 */
@Component({
  selector: 'nh-search-input',
  templateUrl: './search-input.component.html',
  styleUrls: ['./search-input.component.scss'],
  standalone: false
})
export class NhSearchInputComponent {
  @Input() value : string | undefined = '';
  /**
   * The accessible name of the field. Defaults to the translated `general.search` text that the
   * placeholder shows. When the page shows its own label next to the field, pass that text, so
   * assistive technology announces what users see.
   */
  @Input() label: string | undefined;
  /** The id of the input element, for example for a page label's `for` attribute. */
  @Input() inputId: string | undefined;
  @Output() onDebounce = new EventEmitter<string>();
  open = false;
  @ViewChild('searchInput') searchInput?: ElementRef<HTMLInputElement>;

  focusInput(): void {
    this.searchInput?.nativeElement.focus();
  }

  onDebounceEvent(val: Event): void {
    this.onDebounce.emit(val?.toString());
  }
}
