import { ApplicationConfig } from '@angular/core';
import { provideSampleProjectManagement } from 'sample-project-management-common';
import { SAMPLE_ROUTES } from './sample.routes';

export const appConfig: ApplicationConfig = {
  providers: [provideSampleProjectManagement(SAMPLE_ROUTES)]
};
