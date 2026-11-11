import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  signal
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  NhAssistantAdminApiService,
  NhAssistantError,
  RuleScriptComparison,
  RuleScriptVersion,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTokensDirective as NhAssistantTokensDirective,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { RuleScriptScope } from './rule-scripts';
import { NhAssistantRuleScriptRunRecordComponent } from './nh-assistant-rule-script-run-record.component';

let nextId = 0;

/**
 * Two versions of a rule script compared (internal): the other version's script turned into this one's
 * line by line, the writes added and removed, and the latest dry run of both, so an administrator sees
 * what changes before a version goes live. By default the server compares with the live version, or the
 * live version with the one before; the administrator may choose another.
 */
@Component({
  selector: 'nh-assistant-rule-script-comparison',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantRuleScriptRunRecordComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-rule-script-comparison.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', '../page/agent-page.scss', './rule-scripts.scss']
})
export class NhAssistantRuleScriptComparisonComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;

  readonly agentId = input.required<string>();
  readonly skillId = input.required<RuleScriptScope>();
  readonly version = input.required<number>();
  /** Every version of the rule script, for choosing the other one. */
  readonly versions = input<readonly RuleScriptVersion[]>([]);
  readonly names = input<NhAssistantAccountNames | null>(null);

  readonly id = `nh-assistant-rule-script-comparison-${nextId++}`;
  readonly others = computed(() => this.versions().filter(item => item.version !== this.version()));
  readonly comparison = signal<RuleScriptComparison | null>(null);
  readonly changed = computed(() => (this.comparison()?.lines ?? []).filter(line => line.change !== 'same').length);
  readonly loading = signal(true);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  ngOnInit(): void {
    this.load(null);
  }

  /** Compares with `other`, or with the server's default when it is `null`. */
  load(other: number | null): void {
    this.loading.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.compareRuleScriptVersions(this.agentId(), this.skillId(), this.version(), other).subscribe({
      next: comparison => {
        this.comparison.set(comparison);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  chooseOther(event: Event): void {
    const value = Number((event.target as HTMLSelectElement).value);
    this.load(Number.isInteger(value) && value > 0 ? value : null);
  }
}
