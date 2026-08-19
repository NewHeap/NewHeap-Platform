import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import {
  ProjectApiService,
  ProjectCreatedEventViewModel,
  ProjectMutateModel,
  ProjectRollbackSampleViewModel,
  ProjectStatus
} from 'sample-project-management-common';

type TransactionStepState = 'pending' | 'active' | 'done' | 'verified' | 'failed';

interface TransactionStep {
  title: string;
  detail: string;
  state: TransactionStepState;
}

@Component({
  selector: 'app-transaction-playground',
  standalone: true,
  imports: [CommonModule, TranslateModule],
  templateUrl: './transaction-playground.component.html',
  styleUrl: './transaction-playground.component.scss'
})
export class TransactionPlaygroundComponent {
  private readonly projectApi = inject(ProjectApiService);

  readonly divisionId = signal('b14a1178-8bd7-4e87-845f-e0d89b63f099');
  readonly key = signal('ROLLBACK-DEMO');
  readonly name = signal('Project that never commits');
  readonly running = signal(false);
  readonly result = signal('Start Aspire, sign in with app.project.manage, and run the rollback sample.');
  readonly rollback = signal<ProjectRollbackSampleViewModel | null>(null);
  readonly verification = signal<{ project: string; event: string } | null>(null);
  readonly steps = signal<TransactionStep[]>(this.initialSteps());

  runRollback(): void {
    const key = this.key().trim().toUpperCase();
    const name = this.name().trim();
    const divisionId = this.divisionId().trim();
    if (!key || !name || !divisionId) {
      this.result.set('Enter the division ID, project key, and name.');
      return;
    }

    const model: ProjectMutateModel = {
      divisionId,
      key,
      name,
      description: 'SPM-200: save and publish followed by a deliberate rollback.',
      status: ProjectStatus.Draft,
      deadline: null
    };

    this.running.set(true);
    this.rollback.set(null);
    this.verification.set(null);
    this.steps.set(this.initialSteps().map((step, index) => ({
      ...step,
      state: index === 0 ? 'active' : 'pending'
    })));

    this.projectApi.createRolledBackSample(model).subscribe({
      next: rollback => {
        this.rollback.set(rollback);
        this.updateStep(0, 'done');
        this.updateStep(1, 'done');
        this.updateStep(2, 'done');
        this.updateStep(3, 'active');
        this.verifyRollback(rollback);
      },
      error: error => {
        this.updateStep(0, 'failed');
        this.running.set(false);
        this.result.set(this.errorMessage(error));
      }
    });
  }

  updateDivisionId(event: Event): void {
    this.divisionId.set((event.target as HTMLInputElement).value);
  }

  updateKey(event: Event): void {
    this.key.set((event.target as HTMLInputElement).value.toUpperCase());
  }

  updateName(event: Event): void {
    this.name.set((event.target as HTMLInputElement).value);
  }

  private verifyRollback(rollback: ProjectRollbackSampleViewModel): void {
    const projectCheck = this.projectApi.getById(rollback.projectId).pipe(
      map(() => 'Unexpectedly found: the rollback did not succeed.'),
      catchError(error => of(error.status === 404
        ? '404 confirmed: the project was not committed.'
        : `Projectcontrole gaf HTTP ${error.status ?? 'onbekend'}.`))
    );
    const eventCheck = this.projectApi.getConsumedEvents().pipe(
      map(events => this.eventMessage(events, rollback.eventId)),
      catchError(error => of(`The event log could not be read (HTTP ${error.status ?? 'unknown'}).`))
    );

    forkJoin({ project: projectCheck, event: eventCheck }).subscribe(({ project, event }) => {
      this.verification.set({ project, event });
      const confirmed = project.startsWith('404 confirmed') && event.startsWith('Absence confirmed');
      this.updateStep(3, confirmed ? 'verified' : 'failed');
      this.running.set(false);
      this.result.set(confirmed
        ? 'Rollback proven: neither the project row nor the outbox event escaped the transaction.'
        : 'The API returned an unexpected verification result; inspect the two checks below.');
    });
  }

  private eventMessage(events: ProjectCreatedEventViewModel[], eventId: string): string {
    return events.some(event => event.eventId === eventId)
      ? 'Unexpectedly found: the event reached the consumer log.'
      : 'Absence confirmed: there is no committed outbox event in the consumer log.';
  }

  private updateStep(index: number, state: TransactionStepState): void {
    this.steps.update(steps => steps.map((step, currentIndex) =>
      currentIndex === index ? { ...step, state } : step));
  }

  private errorMessage(error: any): string {
    if (error?.status === 401 || error?.status === 403) {
      return 'This live sample requires a signed-in user with app.project.manage.';
    }
    return `Rollback sample failed (HTTP ${error?.status ?? 'offline'}). Start the API through Aspire.`;
  }

  private initialSteps(): TransactionStep[] {
    return [
      { title: '1. Start buitenste scope', detail: 'ProjectService opent StartOrGetTransactionScopeAsync.', state: 'pending' },
      { title: '2. Save and typed event', detail: 'The project write and outbox publication share the same SQL transaction.', state: 'pending' },
      { title: '3. Deliberate rollback', detail: 'No CommitAsync follows; the scope explicitly rolls back both effects.', state: 'pending' },
      { title: '4. Verify outside the scope', detail: 'Project-by-ID must return 404 and the consumer log must not contain the event.', state: 'pending' }
    ];
  }
}
