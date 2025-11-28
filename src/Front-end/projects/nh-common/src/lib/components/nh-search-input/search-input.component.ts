import { Component, EventEmitter, Input, Output, ViewChild, ElementRef } from '@angular/core';

@Component({
  selector: 'nh-search-input',
  templateUrl: './search-input.component.html',
  styleUrls: ['./search-input.component.scss'],
  standalone: false
})
export class NhSearchInputComponent {
  @Input() value : string | undefined = '';
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
