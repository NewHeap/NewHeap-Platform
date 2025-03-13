import {NgModule} from '@angular/core';
import {Routes, RouterModule} from '@angular/router';
import {AuthLoginPageComponent} from './pages/login/page';
import {AuthPasswordForgetPageComponent} from './pages/password-forget/page';
import {AuthResetPasswordPageComponent} from './pages/reset-password/page';
import {AuthRegistrationPageComponent} from './pages/registration/page';
import {AuthTokenLoginPageComponent} from './pages/tokenlogin/page';

const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'login'
  },
  {
    path: 'login',
    pathMatch: 'full',
    component: AuthLoginPageComponent,
    canActivate: [],
    data: {}
  },
  {
    path: 'tokenlogin',
    pathMatch: 'full',
    component: AuthTokenLoginPageComponent,
    canActivate: [],
    data: {}
  },
  {
    path: 'password-recover',
    pathMatch: 'full',
    component: AuthPasswordForgetPageComponent,
    canActivate: [],
    data: {}
  },
  {
    path: 'reset-password',
    pathMatch: 'full',
    component: AuthResetPasswordPageComponent,
    canActivate: [],
    data: {}
  },
  {
    path: 'registration',
    pathMatch: 'full',
    component: AuthRegistrationPageComponent,
    canActivate: [],
    data: {}
  }
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule],
  providers: []
})
export class AuthRoutingModule {
}
