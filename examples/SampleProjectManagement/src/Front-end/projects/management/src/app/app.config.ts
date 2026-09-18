import { ApplicationConfig } from '@angular/core';
import { provideSampleProjectManagement } from 'sample-project-management-common';
import { provideSampleAssistant } from './assistant-playground/sample-assistant.config';
import { SAMPLE_ROUTES } from './sample.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideSampleProjectManagement(SAMPLE_ROUTES),
    // One assistant scope for the portal: the launcher and panel live in the management layout.
    provideSampleAssistant()
  ]
};
