import {DestroyRef, inject, Injectable} from '@angular/core';
import {TranslateService, TranslationObject} from '@ngx-translate/core';
import {merge} from 'rxjs';
import en from './en.json';
import nl from './nl.json';

/** The entry point's own translation bundles, keyed by language. All keys live under `nh-two-factor.`. */
export const NH_TWO_FACTOR_TRANSLATIONS: Readonly<Record<'en' | 'nl', TranslationObject>> = {en, nl};

type TranslationTree = { [key: string]: unknown };

const rootKey = 'nh-two-factor';

function isTree(value: unknown): value is TranslationTree {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

/** Deep merge where values of `override` win. */
function mergeTrees(base: TranslationTree, override: TranslationTree): TranslationTree {
  const result: TranslationTree = {...base};
  for (const [key, value] of Object.entries(override)) {
    const current = result[key];
    result[key] = isTree(current) && isTree(value) ? mergeTrees(current, value) : value;
  }
  return result;
}

/**
 * The bundle of a language: its own, else that of its base language, so a regional tag such as
 * `nl-NL`, `nl_BE` or `en-GB` gets the `nl` or `en` texts, else English.
 */
function bundleOf(language: string): TranslationTree {
  for (const candidate of [language, language.split(/[-_]/)[0]].map(tag => tag.toLowerCase())) {
    if (Object.hasOwn(NH_TWO_FACTOR_TRANSLATIONS, candidate)) {
      return NH_TWO_FACTOR_TRANSLATIONS[candidate as keyof typeof NH_TWO_FACTOR_TRANSLATIONS] as TranslationTree;
    }
  }
  return NH_TWO_FACTOR_TRANSLATIONS.en as TranslationTree;
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
 * language. Host loaders never lose these texts, and keys the host defines under
 * `nh-two-factor.` win over the bundle. A regional language such as `nl-NL` receives the texts
 * of its base language; languages without a bundle receive the English texts.
 */
@Injectable({providedIn: 'root'})
export class NhTwoFactorTranslationMerger {
  private readonly translate = inject(TranslateService, {optional: true});
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
    const bundleRoot = bundleOf(language)[rootKey] as TranslationTree;
    if (hasAllKeys(bundleRoot, existing)) {
      return;
    }

    const merged = mergeTrees(bundleRoot, isTree(existing) ? existing : {});
    this.translate?.setTranslation(language, {[rootKey]: merged} as TranslationObject, true);
  }
}
