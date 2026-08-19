import { bootstrapApplication } from '@angular/platform-browser';
import { AppHostComponent } from './app/app-host.component';
import { appConfig } from './app/app.config';

bootstrapApplication(AppHostComponent, appConfig).catch(error => console.error(error));
