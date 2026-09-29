import {EnvironmentProviders, inject, makeEnvironmentProviders, provideEnvironmentInitializer} from '@angular/core';
import {NhTwoFactorTranslationMerger} from './i18n/nh-two-factor-translations';

/**
 * Registers the two-factor texts at startup. The components also register them when they are
 * first created, so this provider is only needed to translate `nh-two-factor.` failure keys
 * outside the components, for example on a custom login page.
 */
export function provideNhTwoFactor(): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideEnvironmentInitializer(() => inject(NhTwoFactorTranslationMerger).start())
  ]);
}
