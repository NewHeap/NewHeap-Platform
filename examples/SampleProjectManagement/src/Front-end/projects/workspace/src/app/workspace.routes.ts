import { Routes } from '@angular/router';
import {
  SampleLoginComponent,
  SampleProfileComponent,
  sampleIsAuthenticatedGuard
} from 'sample-project-management-common';
import { AppComponent } from './app.component';
import { WorkspaceLayoutComponent } from './workspace-layout/workspace-layout.component';

export const WORKSPACE_ROUTES: Routes = [
  {
    path: 'auth/login',
    title: 'Sign in',
    component: SampleLoginComponent
  },
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'workspace'
  },
  {
    path: 'workspace',
    component: WorkspaceLayoutComponent,
    canActivate: [sampleIsAuthenticatedGuard],
    canActivateChild: [sampleIsAuthenticatedGuard],
    children: [
      { path: '', pathMatch: 'full', title: 'Werkruimte', component: AppComponent },
      { path: 'profile', title: 'Edit profile', component: SampleProfileComponent }
    ]
  },
  {
    path: '**',
    redirectTo: 'workspace'
  }
];
