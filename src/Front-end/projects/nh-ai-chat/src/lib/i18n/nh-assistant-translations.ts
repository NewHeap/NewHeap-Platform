import { DestroyRef, Injectable, inject } from '@angular/core';
import { TranslateService, TranslationObject } from '@ngx-translate/core';
import { merge } from 'rxjs';
import en from './en.json';
import nl from './nl.json';

/** Translation bundles of one entry point, keyed by language. All keys live under `nh-assistant.`. */
type NhAssistantTranslationBundles = Readonly<Record<'en' | 'nl', TranslationObject>>;

/**
 * The library's own translation bundles, keyed by language. All keys live under `nh-assistant.`.
 * The administration's `nh-assistant.admin.` texts come with `@newheap/platform-ai-chat/admin` as
 * `NH_ASSISTANT_ADMIN_TRANSLATIONS`, so they load with the administration instead of with the panel.
 */
export const NH_ASSISTANT_TRANSLATIONS: NhAssistantTranslationBundles = { en, nl };

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

/**
 * The bundle of a language: its own, else that of its base language, so a regional tag such as
 * `nl-NL`, `nl_BE` or `en-GB` gets the `nl` or `en` texts, else English.
 */
function bundleOf(bundles: NhAssistantTranslationBundles, language: string): TranslationTree {
  for (const candidate of [language, language.split(/[-_]/)[0]].map(tag => tag.toLowerCase())) {
    if (Object.hasOwn(bundles, candidate)) {
      return bundles[candidate as keyof NhAssistantTranslationBundles] as TranslationTree;
    }
  }
  return bundles.en as TranslationTree;
}

function hasAllKeys(bundle: TranslationTree, candidate: unknown): boolean {
  if (!isTree(candidate)) {
    return false;
  }

  return Object.entries(bundle).every(([key, value]) =>
    isTree(value) ? hasAllKeys(value, candidate[key]) : key in candidate
  );
}

function ensure(translate: TranslateService, bundles: NhAssistantTranslationBundles, language: string, existing: unknown): void {
  const bundleRoot = bundleOf(bundles, language)[rootKey] as TranslationTree;
  if (hasAllKeys(bundleRoot, existing)) {
    return;
  }

  const merged = mergeTrees(bundleRoot, isTree(existing) ? existing : {});
  translate.setTranslation(language, { [rootKey]: merged } as TranslationObject, true);
}

/**
 * Merges `bundles` into `TranslateService` whenever the host loads or replaces a language, and
 * into the current language at once, until `destroyRef` is destroyed. Keys the host defines under
 * `nh-assistant.` win. Shared with the secondary entry points that bring texts of their own.
 */
export function nhAssistantMergeTranslations(
  translate: TranslateService,
  bundles: NhAssistantTranslationBundles,
  destroyRef: DestroyRef
): void {
  const subscription = merge(translate.onTranslationChange, translate.onLangChange).subscribe(event => {
    if (event.lang && isTree(event.translations)) {
      ensure(translate, bundles, event.lang, event.translations[rootKey]);
    }
  });
  destroyRef.onDestroy(() => subscription.unsubscribe());

  // The current language is loaded or already requested by the host, so merging into it
  // never pre-empts the host loader; a later load triggers a new merge.
  const current = translate.getCurrentLang();
  if (current) {
    ensure(translate, bundles, current, translate.instant(rootKey));
  }
}

/**
 * Merges the bundled texts into `TranslateService` whenever the host loads or replaces a
 * language. Host loaders never lose the library texts, the library never pre-empts the
 * host loader, and keys the host defines under `nh-assistant.` (agent names, overrides)
 * win over the bundle. A regional language such as `nl-NL` receives the texts of its base
 * language; languages without a bundle receive the English texts.
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

    nhAssistantMergeTranslations(translate, NH_ASSISTANT_TRANSLATIONS, this.destroyRef);
  }
}
