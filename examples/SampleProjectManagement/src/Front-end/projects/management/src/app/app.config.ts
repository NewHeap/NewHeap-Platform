import { ApplicationConfig } from '@angular/core';
import { provideSampleProjectManagement, SAMPLE_MANAGEMENT_ACCOUNT_HINT_COOKIE } from 'sample-project-management-common';
import { provideSampleAssistant } from './assistant-playground/sample-assistant.config';
import { provideSampleSelect } from './platform-playground/select-sample/sample-select.config';
import { SAMPLE_ROUTES } from './sample.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideSampleProjectManagement(SAMPLE_ROUTES, { accountHintCookieName: SAMPLE_MANAGEMENT_ACCOUNT_HINT_COOKIE }),
    // One assistant scope for the portal: the launcher and panel live in the management layout.
    provideSampleAssistant({ adminRoute: '/management/assistant-administration' }),
    // Translated texts for every combobox and multiselect of the portal.
    provideSampleSelect()
  ]
};
