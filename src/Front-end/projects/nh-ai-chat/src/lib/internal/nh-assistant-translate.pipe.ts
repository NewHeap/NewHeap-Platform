import { ChangeDetectorRef, DestroyRef, Pipe, PipeTransform, inject } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { merge } from 'rxjs';

/**
 * Translates the first key that has a translation and otherwise returns the fallback text.
 * Used for server-provided keys (agent names, error message keys, result codes) that the
 * host may or may not translate.
 */
@Pipe({ name: 'nhAssistantTranslate', standalone: true, pure: false })
export class NhAssistantTranslatePipe implements PipeTransform {
  private readonly translate = inject(TranslateService);
  private lastKeys = '';
  private lastParams = '';
  private lastValue = '';
  private dirty = true;

  constructor() {
    const changeDetector = inject(ChangeDetectorRef);
    const subscription = merge(this.translate.onTranslationChange, this.translate.onLangChange).subscribe(() => {
      this.dirty = true;
      changeDetector.markForCheck();
    });
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  transform(keys: string | readonly (string | null | undefined)[], fallback = '', params?: Record<string, unknown>): string {
    const candidates = (Array.isArray(keys) ? keys : [keys]).filter((key): key is string => !!key);
    const keySignature = candidates.join('|');
    const paramSignature = params ? JSON.stringify(params) : '';
    if (!this.dirty && keySignature === this.lastKeys && paramSignature === this.lastParams) {
      return this.lastValue;
    }

    this.lastKeys = keySignature;
    this.lastParams = paramSignature;
    this.dirty = false;
    this.lastValue = this.resolve(candidates, fallback, params);
    return this.lastValue;
  }

  private resolve(candidates: string[], fallback: string, params?: Record<string, unknown>): string {
    for (const key of candidates) {
      const value = this.translate.instant(key, params);
      if (typeof value === 'string' && value.length > 0 && value !== key) {
        return value;
      }
    }

    return fallback;
  }
}
