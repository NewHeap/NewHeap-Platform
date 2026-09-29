import {NhTwoFactorFailureCodes, TaskResult} from '@newheap/platform-common';

const failureCodePrefix = 'two-factor-';
const translationPrefix = 'nh-two-factor.';

/**
 * Returns the translation keys for a failed two-factor result. Two-factor failure codes map
 * to `nh-two-factor.<suffix>`; other failures use a generic message.
 */
export function nhTwoFactorErrorKeys(result: TaskResult<unknown>): string[] {
  const keys = result.items.map(item => {
    if (item.name.startsWith(failureCodePrefix)) {
      return translationPrefix + item.name.substring(failureCodePrefix.length);
    }

    const message = item.errorMessages[0];
    return message?.startsWith(translationPrefix) ? message : translationPrefix + 'ui.error';
  });

  return keys.length > 0 ? [...new Set(keys)] : [translationPrefix + 'ui.error'];
}

/**
 * Whether the server ended the pending sign-in step, so the user has to sign in again.
 */
export function nhTwoFactorStepEnded(result: TaskResult<unknown>): boolean {
  return result.items.some(item =>
    item.name === NhTwoFactorFailureCodes.ChallengeExpired || item.name === NhTwoFactorFailureCodes.LockedOut);
}

/** Reads the value of the input that raised an event. */
export function nhTwoFactorInputValue(event: Event): string {
  return (event.target as HTMLInputElement).value;
}

/** Reads the checked state of the checkbox that raised an event. */
export function nhTwoFactorInputChecked(event: Event): boolean {
  return (event.target as HTMLInputElement).checked;
}
