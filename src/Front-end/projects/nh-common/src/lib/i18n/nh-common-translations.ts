import {TranslateService, TranslationObject} from '@ngx-translate/core';
import en from './en.json';
import nl from './nl.json';

/**
 * The library's own texts of the main entry point, keyed by language. All keys live under
 * `nh-common.`; an application that defines the same key in its own translations wins.
 */
export const NH_COMMON_TRANSLATIONS: Readonly<Record<'en' | 'nl', TranslationObject>> = {en, nl};

type TranslationTree = { [key: string]: unknown };

function isTree(value: unknown): value is TranslationTree {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

/**
 * The bundle of a language: its own, else that of its base language, so a regional tag such as
 * `nl-NL` or `en-GB` gets the `nl` or `en` texts, else English.
 */
function bundleOf(language: string): TranslationTree {
  for (const candidate of [language, language.split(/[-_]/)[0]].map(tag => tag.toLowerCase())) {
    if (Object.hasOwn(NH_COMMON_TRANSLATIONS, candidate)) {
      return NH_COMMON_TRANSLATIONS[candidate as keyof typeof NH_COMMON_TRANSLATIONS] as TranslationTree;
    }
  }

  return NH_COMMON_TRANSLATIONS.en as TranslationTree;
}

function lookup(bundle: TranslationTree, key: string): string | undefined {
  let current: unknown = bundle;
  for (const part of key.split('.')) {
    if (!isTree(current)) {
      return undefined;
    }
    current = current[part];
  }

  return typeof current === 'string' ? current : undefined;
}

/**
 * The text of a library key: the application's translation when it has one, otherwise the
 * bundled text of the current language, otherwise English. Works without `TranslateService`.
 */
export function nhCommonText(translate: TranslateService | null, key: string): string {
  if (translate) {
    const translated = translate.instant(key);
    if (typeof translated === 'string' && translated.length > 0 && translated !== key) {
      return translated;
    }
  }

  const language = translate?.getCurrentLang() || translate?.getFallbackLang() || 'en';
  return lookup(bundleOf(language), key) ?? lookup(NH_COMMON_TRANSLATIONS.en as TranslationTree, key) ?? key;
}
