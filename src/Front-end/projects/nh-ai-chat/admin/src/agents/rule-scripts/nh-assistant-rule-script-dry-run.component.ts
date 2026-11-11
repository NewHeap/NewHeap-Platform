import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AdminAgent,
  AgentSubjectType,
  NH_ASSISTANT_RULE_SCRIPT_MAX_DRY_RUN_SUBJECTS,
  NhAssistantAdminApiService,
  NhAssistantError,
  RuleScriptRunRecord,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTokensDirective as NhAssistantTokensDirective,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import {
  ɵNhAssistantSubjectPickerComponent as NhAssistantSubjectPickerComponent,
  type ɵNhAssistantChosenSubject as NhAssistantChosenSubject
} from '@newheap/platform-ai-chat/agent-runs';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { RuleScriptScope, ruleScriptSubjectTargets } from './rule-scripts';
import { NhAssistantRuleScriptRunRecordComponent } from './nh-assistant-rule-script-run-record.component';

let nextId = 0;

/**
 * A dry run of one version of a rule script (RB-5, internal): the administrator chooses up to twenty
 * subjects of the types the agent works on with the subject picker that running the agent now uses, runs
 * the version on them without effects and reads per subject what it would do. The version's latest dry
 * run shows first; a new one replaces it, as the server keeps only the latest.
 */
@Component({
  selector: 'nh-assistant-rule-script-dry-run',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantSubjectPickerComponent, NhAssistantRuleScriptRunRecordComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-rule-script-dry-run.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', '../page/agent-page.scss', './rule-scripts.scss']
})
export class NhAssistantRuleScriptDryRunComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  private loadSubscription?: Subscription;
  private runSubscription?: Subscription;

  readonly agent = input.required<AdminAgent>();
  readonly skillId = input.required<RuleScriptScope>();
  readonly version = input.required<number>();
  /** The application's subject types from the run catalog. */
  readonly subjectTypes = input<readonly AgentSubjectType[]>([]);
  readonly names = input<NhAssistantAccountNames | null>(null);

  /** A dry run finished; it is the version's latest now. */
  readonly ran = output<RuleScriptRunRecord>();

  readonly id = `nh-assistant-rule-script-dry-run-${nextId++}`;
  readonly maxSubjects = NH_ASSISTANT_RULE_SCRIPT_MAX_DRY_RUN_SUBJECTS;
  readonly targets = computed(() => ruleScriptSubjectTargets(
    this.agent(),
    this.subjectTypes(),
    this.translate.instant('nh-assistant.run-now.whole-application') as string));
  readonly targetKey = signal<string | null>(null);
  readonly target = computed(() => {
    const targets = this.targets();
    return targets.find(item => item.key === this.targetKey()) ?? targets[0] ?? null;
  });
  /** The subject the picker holds, which Add puts in the list. */
  readonly picked = signal<NhAssistantChosenSubject | null>(null);
  /** A new picker after every Add, so it starts empty again. */
  readonly pickerRound = signal(0);
  readonly chosen = signal<NhAssistantChosenSubject[]>([]);
  readonly full = computed(() => this.chosen().length >= this.maxSubjects);
  readonly loading = signal(true);
  readonly running = signal(false);
  readonly latest = signal<RuleScriptRunRecord | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.runSubscription?.unsubscribe();
    });
  }

  ngOnInit(): void {
    this.loadSubscription = this.admin.getRuleScriptDryRun(this.agent().id, this.skillId(), this.version()).subscribe({
      next: record => {
        this.latest.set(record);
        this.loading.set(false);
      },
      // Without the latest dry run a new one can still run.
      error: () => this.loading.set(false)
    });
  }

  chooseTarget(key: string): void {
    this.targetKey.set(key);
    this.picked.set(null);
    this.pickerRound.update(round => round + 1);
  }

  add(): void {
    const subject = this.picked();
    if (!subject || this.full()) {
      return;
    }

    if (!this.chosen().some(item => item.type === subject.type && item.id === subject.id)) {
      this.chosen.update(items => [...items, subject]);
    }
    this.picked.set(null);
    this.pickerRound.update(round => round + 1);
  }

  remove(subject: NhAssistantChosenSubject): void {
    this.chosen.update(items => items.filter(item => item !== subject));
  }

  run(): void {
    const subjects = this.chosen();
    if (subjects.length === 0 || this.running()) {
      return;
    }

    this.running.set(true);
    this.error.set(null);
    this.runSubscription?.unsubscribe();
    this.runSubscription = this.admin
      .dryRunRuleScriptVersion(this.agent().id, this.skillId(), this.version(), {
        subjects: subjects.map(subject => ({ type: subject.type, id: subject.id }))
      })
      .subscribe({
        next: record => {
          this.running.set(false);
          this.latest.set(record);
          this.ran.emit(record);
        },
        error: (error: unknown) => {
          this.running.set(false);
          this.error.set(toNhAssistantError(error));
        }
      });
  }

  value(event: Event): string {
    return (event.target as HTMLSelectElement).value;
  }
}
