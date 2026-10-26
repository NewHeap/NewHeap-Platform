import { DestroyRef, Injectable, inject } from '@angular/core';
import { TranslateService, TranslationObject } from '@ngx-translate/core';
import { NH_ASSISTANT_CONFIG, ɵnhAssistantMergeTranslations as nhAssistantMergeTranslations } from '@newheap/platform-ai-chat';
import en from './en.json';
import nl from './nl.json';

/**
 * The administration's own translation bundles, keyed by language. All keys live under
 * `nh-assistant.admin.`. They ship with this entry point, so they load with the administration's
 * lazy route instead of with the panel; `NH_ASSISTANT_TRANSLATIONS` holds the other texts.
 */
export const NH_ASSISTANT_ADMIN_TRANSLATIONS: Readonly<Record<'en' | 'nl', TranslationObject>> = { en, nl };

/**
 * Merges the administration's texts into `TranslateService` whenever the host loads or replaces a
 * language, like `NhAssistantTranslationMerger` does for the other assistant texts. Keys the host
 * defines under `nh-assistant.admin.` win. The administration's components start it themselves
 * unless the assistant leaves translations to the host (`translations: 'host'`).
 */
@Injectable({ providedIn: 'root' })
export class NhAssistantAdminTranslationMerger {
  private readonly translate = inject(TranslateService, { optional: true });
  private readonly destroyRef = inject(DestroyRef);
  private started = false;

  start(): void {
    const translate = this.translate;
    if (this.started || !translate) {
      return;
    }
    this.started = true;

    nhAssistantMergeTranslations(translate, NH_ASSISTANT_ADMIN_TRANSLATIONS, this.destroyRef);
  }
}

/**
 * Adds the administration's texts in the assistant's bundled translation mode. Every exported
 * component of this entry point calls it in its constructor, so a host that shows one of them on
 * its own page gets the texts too.
 */
export function startNhAssistantAdminTranslations(): void {
  if (inject(NH_ASSISTANT_CONFIG, { optional: true })?.translations === 'host') {
    return;
  }

  inject(NhAssistantAdminTranslationMerger).start();
}
