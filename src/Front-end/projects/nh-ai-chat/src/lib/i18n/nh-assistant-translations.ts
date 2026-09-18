import { DestroyRef, Injectable, inject } from '@angular/core';
import { TranslateService, TranslationObject } from '@ngx-translate/core';
import { merge } from 'rxjs';
import en from './en.json';
import nl from './nl.json';

/** The library's own translation bundles, keyed by language. All keys live under `nh-assistant.`. */
export const NH_ASSISTANT_TRANSLATIONS: Readonly<Record<'en' | 'nl', TranslationObject>> = { en, nl };

/**
 * Merges the bundled texts into `TranslateService` whenever the host loads or replaces a
 * language, so host loaders never overwrite them and the library never pre-empts the host
 * loader. Languages without a bundle receive the English texts.
 */
@Injectable({ providedIn: 'root' })
export class NhAssistantTranslationMerger {
  private readonly translate = inject(TranslateService, { optional: true });
  private readonly destroyRef = inject(DestroyRef);
  private started = false;

  start(): void {
    const translate = this.translate;
    if (this.started || !translate) {
      return;
    }
    this.started = true;

    const subscription = merge(translate.onTranslationChange, translate.onLangChange)
      .subscribe(event => {
        if (event.lang && event.translations && !('nh-assistant' in event.translations)) {
          this.mergeBundle(event.lang);
        }
      });
    this.destroyRef.onDestroy(() => subscription.unsubscribe());

    // The current and fallback languages are loaded or already requested by the host, so
    // merging into them never pre-empts the host loader; a later load triggers a new merge.
    const languages = new Set([translate.getCurrentLang(), translate.getFallbackLang()]);
    for (const language of languages) {
      if (language) {
        this.mergeBundle(language);
      }
    }
  }

  private mergeBundle(language: string): void {
    const bundle = NH_ASSISTANT_TRANSLATIONS[language as 'en' | 'nl'] ?? NH_ASSISTANT_TRANSLATIONS.en;
    this.translate?.setTranslation(language, bundle, true);
  }
}
