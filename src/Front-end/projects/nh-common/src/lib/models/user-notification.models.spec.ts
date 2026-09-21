import { NhUserNotification, nhUserNotificationSeverityName } from './user-notification.models';

describe('nhUserNotificationSeverityName', () => {
  it('normalizes numeric and string severities from the API', () => {
    expect(nhUserNotificationSeverityName(0)).toBe('Information');
    expect(nhUserNotificationSeverityName(10)).toBe('Success');
    expect(nhUserNotificationSeverityName(20)).toBe('Warning');
    expect(nhUserNotificationSeverityName(30)).toBe('Error');
    expect(nhUserNotificationSeverityName('Warning')).toBe('Warning');
  });

  it('falls back to information for missing or unknown values', () => {
    expect(nhUserNotificationSeverityName(undefined)).toBe('Information');
    expect(nhUserNotificationSeverityName(null)).toBe('Information');
    expect(nhUserNotificationSeverityName(99)).toBe('Information');
    expect(new NhUserNotification().severity).toBe('Information');
  });
});
