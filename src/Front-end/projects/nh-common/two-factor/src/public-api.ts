/*
 * Public API surface of @newheap/platform-common/two-factor.
 *
 * The two-factor components are a separate entry point, so applications that do not import
 * it ship none of their code. The service and models live in @newheap/platform-common.
 */
export * from './lib/challenge/nh-two-factor-challenge.component';
export * from './lib/enrollment/nh-two-factor-enrollment.component';
export * from './lib/recovery-codes/nh-two-factor-recovery-codes.component';
export * from './lib/settings/nh-two-factor-settings.component';
export * from './lib/passkey-login/nh-passkey-login-button.component';
export {NH_TWO_FACTOR_TRANSLATIONS, NhTwoFactorTranslationMerger} from './lib/i18n/nh-two-factor-translations';
export {nhTwoFactorErrorKeys, nhTwoFactorStepEnded} from './lib/nh-two-factor-errors';
export * from './lib/provide-nh-two-factor';
