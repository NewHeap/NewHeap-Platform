import { CommonModule } from '@angular/common';
import { Component, OnDestroy, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NhApiService } from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { SampleAuthService } from './sample-auth.service';

@Component({
  selector: 'app-sample-profile',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, TranslateModule],
  templateUrl: './sample-profile.component.html',
  styleUrl: './sample-profile.component.scss'
})
export class SampleProfileComponent implements OnDestroy {
  private readonly api = inject(NhApiService);
  private readonly authService = inject(SampleAuthService);
  private readonly authSubscription: Subscription;

  readonly authorization = signal(this.authService.getAuthorization());
  readonly saving = signal(false);
  readonly result = signal<string | null>(null);
  activeDivisionId = this.authorization()?.activeDivision?.id
    ?? this.authorization()?.user?.activeDivisionId
    ?? '';
  currentPassword = '';
  password = '';
  confirmPassword = '';

  constructor() {
    this.authSubscription = this.authService.authSubject.subscribe(authorization => {
      this.authorization.set(authorization);
      this.activeDivisionId = authorization?.activeDivision?.id
        ?? authorization?.user?.activeDivisionId
        ?? '';
    });
  }

  ngOnDestroy(): void {
    this.authSubscription.unsubscribe();
  }

  async saveActiveDivision(): Promise<void> {
    this.saving.set(true);
    this.result.set(null);
    try {
      await this.api.put<void>('/account-samples/active-division', {
        divisionId: this.activeDivisionId || null
      }).lastValueFrom();
      await this.authService.reloadAuthorizationProfile();
      this.result.set('division-saved');
    } catch {
      this.result.set('save-failed');
    } finally {
      this.saving.set(false);
    }
  }

  async changePassword(): Promise<void> {
    if (this.password !== this.confirmPassword) {
      this.result.set('password-mismatch');
      return;
    }

    this.saving.set(true);
    this.result.set(null);
    try {
      await this.api.post<void>('/account-samples/password/change', {
        currentPassword: this.currentPassword,
        password: this.password,
        confirmPassword: this.confirmPassword
      }).lastValueFrom();
      this.currentPassword = '';
      this.password = '';
      this.confirmPassword = '';
      this.result.set('password-saved');
    } catch {
      this.result.set('save-failed');
    } finally {
      this.saving.set(false);
    }
  }
}
