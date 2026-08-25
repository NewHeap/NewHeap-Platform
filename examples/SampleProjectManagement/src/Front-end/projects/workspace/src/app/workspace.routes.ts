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
      {
        path: 'background-operations',
        title: 'Background operations',
        loadComponent: () => import('sample-project-management-common')
          .then(module => module.BackgroundOperationsPageComponent)
      },
      {
        path: 'background-operations/:id',
        title: 'Background operation progress',
        loadComponent: () => import('sample-project-management-common')
          .then(module => module.BackgroundOperationsPageComponent)
      },
      { path: 'profile', title: 'Edit profile', component: SampleProfileComponent }
    ]
  },
  {
    path: 'background-operations/:id',
    redirectTo: 'workspace/background-operations/:id'
  },
  {
    path: '**',
    redirectTo: 'workspace'
  }
];
