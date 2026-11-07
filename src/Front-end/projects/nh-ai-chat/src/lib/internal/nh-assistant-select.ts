import { DestroyRef, Provider, Signal, inject, signal } from '@angular/core';
import { NhSelectConfig, provideNhSelect } from '@newheap/platform-common/select';
import { TranslateService } from '@ngx-translate/core';
import { merge } from 'rxjs';
import { nhAssistantErrorKeys, toNhAssistantError } from './nh-assistant-errors';

/**
 * The texts of the comboboxes in the assistant's own language, and a failed search described by the
 * translated text of its code (the code itself when nothing translates it). Follows language and
 * translation changes. Runs in an injection context.
 */
export function nhAssistantSelectConfig(): Signal<NhSelectConfig> {
  const translate = inject(TranslateService);
  const text = (key: string, params?: Record<string, unknown>): string => translate.instant(key, params) as string;
  const firstTranslation = (keys: readonly string[]): string | null => {
    for (const key of keys) {
      const value = translate.instant(key);
      if (typeof value === 'string' && value.length > 0 && value !== key) {
        return value;
      }
    }

    return null;
  };

  const build = (): NhSelectConfig => ({
    texts: {
      toggle: text('nh-assistant.picker.toggle'),
      searching: text('nh-assistant.picker.searching'),
      noResults: text('nh-assistant.picker.no-results'),
      typeMore: count => text('nh-assistant.picker.type-more', { count }),
      searchFailed: text('nh-assistant.picker.search-failed'),
      retry: text('nh-assistant.picker.search-again')
    },
    errorText: error => {
      const failure = toNhAssistantError(error);
      return firstTranslation(nhAssistantErrorKeys(failure)) ?? failure.code;
    }
  });

  const config = signal(build());
  const subscription = merge(translate.onLangChange, translate.onTranslationChange, translate.onFallbackLangChange)
    .subscribe(() => config.set(build()));
  inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  return config.asReadonly();
}

/** Gives the comboboxes of a component the assistant's texts and error descriptions (internal). */
export function provideNhAssistantSelect(): Provider {
  return provideNhSelect(nhAssistantSelectConfig);
}
