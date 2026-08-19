import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { ICancelNavigationComponent, NhPageBaseComponent } from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import {
  ProjectApiService,
  ProjectCollectionRequestOptions
} from 'sample-project-management-common';

@Component({
  selector: 'app-dirty-route-sample',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, TranslateModule],
  template: `
    <section class="dirty-sample">
      <h2>{{ 'project.page-lifecycle-title' | translate }}</h2>
      <p>{{ 'project.page-lifecycle-description' | translate }}</p>
      <input [formControl]="name" [attr.aria-label]="'project.name' | translate">
      <button type="button" (click)="save()">{{ 'project.save' | translate }}</button>
      <ol>
        @for (event of lifecycleEvents(); track $index) {
          <li><code>{{ event }}</code></li>
        }
      </ol>
      <p class="lifecycle-note">{{ 'project.page-lifecycle-note' | translate }}</p>
    </section>
  `,
  styles: `.dirty-sample{margin:24px;padding:24px;border:1px solid #d6dfda;border-radius:18px}.dirty-sample input{padding:10px}.dirty-sample button{margin-left:8px;padding:10px}.dirty-sample ol{padding:12px 12px 12px 34px;border-radius:10px;color:#cfe0d6;background:#1d352b}.lifecycle-note{color:#68776f}`
})
export class DirtyRouteSampleComponent extends NhPageBaseComponent implements ICancelNavigationComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly projectApi = inject(ProjectApiService);
  readonly name = new FormControl('Sample Project Management', { nonNullable: true });
  readonly lifecycleEvents = signal<string[]>([]);

  constructor() {
    super();
    this.pageSettings.breadCrumbOverrideText = () =>
      this.translateService.instant('project.page-lifecycle-breadcrumb');
  }

  override async appOnInit(): Promise<void> {
    this.record('appOnInit');
  }

  override appOnInitAndLoad(): Promise<void> {
    this.record('appOnInitAndLoad');
    void this.loadProjectSummary().catch(error => this.handleProjectSummaryLoadError(error));
    this.pageSettings.title = this.translateService.instant('project.page-lifecycle-title');
    this.pageSettings.description = this.translateService.instant('project.page-lifecycle-description');
    return Promise.resolve();
  }

  override async appAfterViewInit(): Promise<void> {
    await Promise.resolve();
    this.record('appAfterViewInit');
  }

  override async appOnDestroy(): Promise<void> {
    this.record('appOnDestroy');
  }

  save(): void {
    this.name.markAsPristine();
  }

  canDeactivateComponent(): boolean {
    return !this.name.dirty || window.confirm(
      this.translateService.instant('project.unsaved-navigation-confirm')
    );
  }

  private record(event: string): void {
    this.lifecycleEvents.update(events => [...events, event]);
  }

  private async loadProjectSummary(): Promise<void> {
    this.record('project summary load started without blocking the page lifecycle');
    const response = await firstValueFrom(
      this.projectApi.list(
        new ProjectCollectionRequestOptions({ page: 1, itemsPerPage: 1 })
      ).pipe(takeUntilDestroyed(this.destroyRef))
    );
    this.record(`project summary load completed: ${response.totalCount} projects`);
  }

  private handleProjectSummaryLoadError(error: unknown): void {
    if (this.destroyRef.destroyed) return;
    this.record('project summary load failed; the page lifecycle still continued');
    console.error('The detached project summary load failed.', error);
  }
}
