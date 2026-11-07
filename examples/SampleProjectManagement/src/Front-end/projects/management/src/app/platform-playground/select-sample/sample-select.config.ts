import { DestroyRef, Provider, inject, signal } from '@angular/core';
import { NhSelectConfig, provideNhSelect } from '@newheap/platform-common/select';
import { TranslateService } from '@ngx-translate/core';
import { merge } from 'rxjs';

/**
 * SPM-397: the texts of every nh-combobox and nh-multiselect of the portal, registered once at the
 * application root. The configuration is a signal that follows the runtime language, so the
 * controls switch between English and Dutch with the rest of the page.
 */
export function provideSampleSelect(): Provider {
  return provideNhSelect(() => {
    const translate = inject(TranslateService);
    const text = (key: string, params?: Record<string, unknown>): string => translate.instant(`nh-select.${key}`, params) as string;
    const build = (): NhSelectConfig => ({
      texts: {
        toggle: text('toggle'),
        searching: text('searching'),
        noResults: text('no-results'),
        typeMore: count => text('type-more', { count }),
        searchFailed: text('search-failed'),
        createFailed: text('create-failed'),
        retry: text('retry'),
        create: value => text('create', { text: value }),
        creating: text('creating'),
        remove: label => text('remove', { label }),
        added: label => text('added', { label }),
        removed: label => text('removed', { label })
      }
    });

    const config = signal(build());
    const subscription = merge(translate.onLangChange, translate.onTranslationChange)
      .subscribe(() => config.set(build()));
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
    return config.asReadonly();
  });
}
