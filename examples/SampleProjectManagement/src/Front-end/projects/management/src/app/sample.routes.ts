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
import { sampleIsAuthenticatedGuard } from 'sample-project-management-common';
import { ManagementLayoutComponent } from './management-layout/management-layout.component';

export const SAMPLE_ROUTES: Routes = [
  {
    path: 'auth/login',
    title: 'Sign in',
    loadComponent: () => import('sample-project-management-common/pages')
      .then(module => module.SampleLoginComponent)
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
      {
        path: '',
        pathMatch: 'full',
        title: 'Samplecatalogus',
        loadComponent: () => import('./app.component').then(module => module.AppComponent)
      },
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
        path: 'background-operations',
        title: 'Background operations',
        loadComponent: () => import('sample-project-management-common/pages')
          .then(module => module.BackgroundOperationsPageComponent)
      },
      {
        path: 'background-operations/:id',
        title: 'Background operation progress',
        loadComponent: () => import('sample-project-management-common/pages')
          .then(module => module.BackgroundOperationsPageComponent)
      },
      {
        path: 'background-operation-administration',
        title: 'Background operation administration',
        canActivate: [NhIsOnePermissionGrantedGuard],
        data: { oneMatchPermissions: ['app.background-operation.administer'] },
        loadComponent: () => import('./background-operation-administration/background-operation-administration-page.component')
          .then(module => module.BackgroundOperationAdministrationPageComponent)
      },
      {
        path: 'background-operation-administration/:id',
        title: 'Background operation administration',
        canActivate: [NhIsOnePermissionGrantedGuard],
        data: { oneMatchPermissions: ['app.background-operation.administer'] },
        loadComponent: () => import('./background-operation-administration/background-operation-administration-page.component')
          .then(module => module.BackgroundOperationAdministrationPageComponent)
      },
      {
        path: 'notifications',
        title: 'Notificaties',
        loadComponent: () => import('./notification-playground/notification-playground.component')
          .then(module => module.NotificationPlaygroundComponent)
      },
      {
        path: 'assistant',
        loadChildren: () => import('./assistant-playground/assistant-playground.routes')
          .then(module => module.ASSISTANT_PLAYGROUND_ROUTES)
      },
      {
        // SPM-253: the assistant's chat on a page of its own, for long sessions.
        path: 'chat',
        title: 'Chat',
        canActivate: [NhIsOnePermissionGrantedGuard],
        data: { oneMatchPermissions: ['app.assistant.access'] },
        loadComponent: () => import('./assistant-chat/assistant-chat-page.component')
          .then(module => module.AssistantChatPageComponent)
      },
      {
        // The real administration; the server's admin policy decides what the page shows.
        path: 'assistant-administration',
        title: 'Assistant administration',
        canActivate: [NhIsOnePermissionGrantedGuard],
        data: { oneMatchPermissions: ['app.assistant.access'] },
        // SPM-313: its routes register the portal's editor of the cost center of agents, loaded with
        // the administration.
        loadChildren: () => import('./assistant-administration/assistant-administration.routes')
          .then(module => module.ASSISTANT_ADMINISTRATION_ROUTES)
      },
      {
        // SPM-304: the board of the project management group of agents; the server decides which
        // agents each person sees on it.
        path: 'project-management',
        title: 'Project management',
        canActivate: [NhIsAllPermissionsGrantedGuard],
        data: { allMatchPermissions: ['app.assistant.access', 'app.project.view'] },
        loadComponent: () => import('./project-management-board/project-management-board-page.component')
          .then(module => module.ProjectManagementBoardPageComponent)
      },
      {
        // SPM-371: the portal's own setting of compositions and scripts, next to what the assistant does.
        path: 'assistant-settings',
        title: 'Assistant settings',
        canActivate: [NhIsAllPermissionsGrantedGuard],
        data: { allMatchPermissions: ['app.assistant.access', 'app.project.manage'] },
        loadComponent: () => import('./assistant-settings/assistant-settings-page.component')
          .then(module => module.AssistantSettingsPageComponent)
      },
      {
        path: 'project-members',
        title: 'Project members',
        canActivate: [NhIsOnePermissionGrantedGuard],
        data: { oneMatchPermissions: ['app.project.view'] },
        loadComponent: () => import('./project-members/project-members-page.component')
          .then(module => module.ProjectMembersPageComponent)
      },
      {
        path: 'intake-reviews',
        title: 'Intake reviews',
        canActivate: [NhIsOnePermissionGrantedGuard],
        data: { oneMatchPermissions: ['app.project.view'] },
        loadComponent: () => import('./intake-reviews/intake-reviews-page.component')
          .then(module => module.IntakeReviewsPageComponent)
      },
      {
        // SPM-336: a bundle of Markdown documents read with nh-assistant-document.
        path: 'handbook',
        title: 'Project handbook',
        loadComponent: () => import('./project-handbook/project-handbook-page.component')
          .then(module => module.ProjectHandbookPageComponent)
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
      {
        path: 'profile',
        title: 'Edit profile',
        loadComponent: () => import('sample-project-management-common/pages')
          .then(module => module.SampleProfileComponent)
      },
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
    path: 'background-operations/:id',
    redirectTo: 'management/background-operations/:id'
  },
  {
    path: '**',
    redirectTo: 'management'
  }
];
