import {Injectable} from '@angular/core';

/** What the stack needs of an open modal shell. */
export interface NhModalStackEntry {
  focusInitial(): void;
}

/**
 * The open modal shells of the application, the topmost last. Only the topmost modal reacts
 * to Escape and keeps Tab inside itself, so stacked modals close one at a time. Internal: not
 * exported from the package.
 */
@Injectable({providedIn: 'root'})
export class NhModalStack {
  private readonly entries: NhModalStackEntry[] = [];

  /** Puts a modal on top. Opening a modal that is already open changes nothing. */
  push(entry: NhModalStackEntry): void {
    if (!this.entries.includes(entry)) {
      this.entries.push(entry);
    }
  }

  /** Takes a modal from the stack, wherever it is. Removing it twice changes nothing. */
  remove(entry: NhModalStackEntry): void {
    const index = this.entries.indexOf(entry);
    if (index >= 0) {
      this.entries.splice(index, 1);
    }
  }

  isTop(entry: NhModalStackEntry): boolean {
    return this.entries.length > 0 && this.entries[this.entries.length - 1] === entry;
  }

  top(): NhModalStackEntry | undefined {
    return this.entries[this.entries.length - 1];
  }

  get isEmpty(): boolean {
    return this.entries.length === 0;
  }
}
