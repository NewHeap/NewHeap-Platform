import { Routes } from '@angular/router';
import {
  Claim,
  ClaimTypes,
  IsOneActiveDivisionPermissionGrantedGuard,
  NhCanCancelNavigationGuard,
  NhIsAllPermissionsGrantedGuard,
  NhIsOneClaimGrantedGuard,
  NhIsOnePermissionGrantedGuard
} from '@newheap/platform-common';
import {
  SampleLoginComponent,
  SampleProfileComponent,
  sampleIsAuthenticatedGuard
} from 'sample-project-management-common';
import { AppComponent } from './app.component';
import { ManagementLayoutComponent } from './management-layout/management-layout.component';

export const SAMPLE_ROUTES: Routes = [
  {
    path: 'auth/login',
    title: 'Sign in',
    component: SampleLoginComponent
  },
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'management'
  },
  {
    path: 'management',
    component: ManagementLayoutComponent,
    canActivate: [sampleIsAuthenticatedGuard],
    canActivateChild: [sampleIsAuthenticatedGuard],
    children: [
      { path: '', pathMatch: 'full', title: 'Samplecatalogus', component: AppComponent },
      {
        path: 'collections',
        title: 'Collecties',
        loadComponent: () => import('./collection-playground/collection-playground.component')
          .then(module => module.CollectionPlaygroundComponent)
      },
      {
        path: 'interactions',
        title: 'Interacties',
        loadComponent: () => import('./interaction-playground/interaction-playground.component')
          .then(module => module.InteractionPlaygroundComponent)
      },
      {
        path: 'authorization',
        title: 'Authorization',
        loadComponent: () => import('./auth-playground/auth-playground.component')
          .then(module => module.AuthPlaygroundComponent)
      },
      {
        path: 'notifications',
        title: 'Notificaties',
        loadComponent: () => import('./notification-playground/notification-playground.component')
          .then(module => module.NotificationPlaygroundComponent)
      },
      {
        path: 'platform',
        title: 'Platform',
        loadComponent: () => import('./platform-playground/platform-playground.component')
          .then(module => module.PlatformPlaygroundComponent)
      },
      {
        path: 'media',
        title: 'Media',
        loadComponent: () => import('./media-playground/media-playground.component')
          .then(module => module.MediaPlaygroundComponent)
      },
      {
        path: 'transactions',
        title: 'Transacties',
        loadComponent: () => import('./transaction-playground/transaction-playground.component')
          .then(module => module.TransactionPlaygroundComponent)
      },
      {
        path: 'utilities',
        title: 'Utilities',
        loadComponent: () => import('./utility-playground/utility-playground.component')
          .then(module => module.UtilityPlaygroundComponent)
      },
      {
        path: 'cases',
        title: 'Samplecatalogus',
        canActivate: [NhIsOnePermissionGrantedGuard],
        data: {
          oneMatchPermissions: ['app.project.view'],
          breadcrumb: 'Examples',
          sitemap: true
        },
        loadComponent: () => import('./sample-case-catalog/sample-case-catalog.component')
          .then(module => module.SampleCaseCatalogComponent)
      },
      { path: 'profile', title: 'Edit profile', component: SampleProfileComponent },
      {
        path: 'samples/admin',
        canActivate: [NhIsOneClaimGrantedGuard],
        data: {
          oneMatchClaims: [new Claim({ type: ClaimTypes.Permission, value: 'app.project.manage' })],
          isAuthenticatedGuardRedirectPath: '/auth/login'
        },
        loadComponent: () => import('./auth-playground/auth-playground.component')
          .then(module => module.AuthPlaygroundComponent)
      },
      {
        path: 'samples/all-permissions',
        title: 'All permissions sample',
        canActivate: [NhIsAllPermissionsGrantedGuard],
        data: {
          allMatchPermissions: ['app.project.view', 'app.project.manage'],
          isAuthenticatedGuardRedirectPath: '/auth/login'
        },
        loadComponent: () => import('./auth-playground/auth-playground.component')
          .then(module => module.AuthPlaygroundComponent)
      },
      {
        path: 'samples/division',
        title: 'Active division sample',
        canActivate: [IsOneActiveDivisionPermissionGrantedGuard],
        data: {
          activeDivisionOneMatchPermissions: ['project.view'],
          isAuthenticatedGuardRedirectPath: '/auth/login'
        },
        loadComponent: () => import('./auth-playground/auth-playground.component')
          .then(module => module.AuthPlaygroundComponent)
      },
      {
        path: 'samples/dirty',
        canDeactivate: [NhCanCancelNavigationGuard],
        loadComponent: () => import('./dirty-route-sample/dirty-route-sample.component')
          .then(module => module.DirtyRouteSampleComponent)
      }
    ]
  },
  {
    path: '**',
    redirectTo: 'management'
  }
];
