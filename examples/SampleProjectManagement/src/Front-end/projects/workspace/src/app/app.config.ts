import { ApplicationConfig } from '@angular/core';
import { provideSampleProjectManagement } from 'sample-project-management-common';
import { WORKSPACE_ROUTES } from './workspace.routes';

export const appConfig: ApplicationConfig = {
  providers: [provideSampleProjectManagement(WORKSPACE_ROUTES)]
};
