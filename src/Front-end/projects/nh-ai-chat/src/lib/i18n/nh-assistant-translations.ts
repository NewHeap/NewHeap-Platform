import { DestroyRef, Injectable, inject } from '@angular/core';
import { TranslateService, TranslationObject } from '@ngx-translate/core';
import { merge } from 'rxjs';
import en from './en.json';
import nl from './nl.json';

/** The library's own translation bundles, keyed by language. All keys live under `nh-assistant.`. */
export const NH_ASSISTANT_TRANSLATIONS: Readonly<Record<'en' | 'nl', TranslationObject>> = { en, nl };

type TranslationTree = { [key: string]: unknown };

const rootKey = 'nh-assistant';

function isTree(value: unknown): value is TranslationTree {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

/** Deep merge where values of `override` win. */
function mergeTrees(base: TranslationTree, override: TranslationTree): TranslationTree {
  const result: TranslationTree = { ...base };
  for (const [key, value] of Object.entries(override)) {
    const current = result[key];
    result[key] = isTree(current) && isTree(value) ? mergeTrees(current, value) : value;
  }
  return result;
}

function hasAllKeys(bundle: TranslationTree, candidate: unknown): boolean {
  if (!isTree(candidate)) {
    return false;
  }

  return Object.entries(bundle).every(([key, value]) =>
    isTree(value) ? hasAllKeys(value, candidate[key]) : key in candidate
  );
}

/**
 * Merges the bundled texts into `TranslateService` whenever the host loads or replaces a
 * language. Host loaders never lose the library texts, the library never pre-empts the
 * host loader, and keys the host defines under `nh-assistant.` (agent names, overrides)
 * win over the bundle. Languages without a bundle receive the English texts.
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

    const subscription = merge(translate.onTranslationChange, translate.onLangChange).subscribe(event => {
      if (event.lang && isTree(event.translations)) {
        this.ensure(event.lang, event.translations[rootKey]);
      }
    });
    this.destroyRef.onDestroy(() => subscription.unsubscribe());

    // The current language is loaded or already requested by the host, so merging into it
    // never pre-empts the host loader; a later load triggers a new merge.
    const current = translate.getCurrentLang();
    if (current) {
      this.ensure(current, translate.instant(rootKey));
    }
  }

  private ensure(language: string, existing: unknown): void {
    const bundle = (NH_ASSISTANT_TRANSLATIONS[language as 'en' | 'nl'] ?? NH_ASSISTANT_TRANSLATIONS.en) as TranslationTree;
    const bundleRoot = bundle[rootKey] as TranslationTree;
    if (hasAllKeys(bundleRoot, existing)) {
      return;
    }

    const merged = mergeTrees(bundleRoot, isTree(existing) ? existing : {});
    this.translate?.setTranslation(language, { [rootKey]: merged } as TranslationObject, true);
  }
}
