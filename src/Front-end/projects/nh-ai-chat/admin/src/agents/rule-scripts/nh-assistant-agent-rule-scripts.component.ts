import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  inject,
  input,
  signal
} from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import {
  AdminAgent,
  AgentSubjectType,
  NH_ASSISTANT_RULE_SCRIPT_MAX_REASON,
  NhAssistantAdminApiService,
  NhAssistantApiError,
  NhAssistantError,
  RuleScript,
  RuleScriptRunRecord,
  RuleScriptVersion,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTokensDirective as NhAssistantTokensDirective,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';
import { agentDisplayName, readableId, skillNameKeys } from '../agent-texts';
import { RuleScriptScope, ruleScriptStatusTone, ruleScriptVersionOf } from './rule-scripts';
import { NhAssistantRuleScriptComparisonComponent } from './nh-assistant-rule-script-comparison.component';
import { NhAssistantRuleScriptDryRunComponent } from './nh-assistant-rule-script-dry-run.component';
import { NhAssistantRuleScriptEditorComponent } from './nh-assistant-rule-script-editor.component';
import { NhAssistantRuleScriptRunRecordComponent } from './nh-assistant-rule-script-run-record.component';

/** What one version shows below it; one at a time in the section. */
export type RuleScriptPanel = 'script' | 'dry-run' | 'compare' | 'shadow-runs' | 'live' | 'discard';

/** The latest dry run that going live shows, while it loads, when it failed and once it is known. */
interface LiveReview {
  version: number;
  loading: boolean;
  failed: boolean;
  record: RuleScriptRunRecord | null;
}

/** The shadow runs of a candidate, while they load, when they failed and once they are known. */
interface ShadowRuns {
  version: number;
  failed: boolean;
  records: RuleScriptRunRecord[] | null;
}

let nextId = 0;

/**
 * The rule script of an agent's own rules and of each of its skills (RB-1 to RB-6), a section of the
 * agent page's Build tab (internal). It shows only when the code's ceiling (`UseRuleScriptsAsData`) lets
 * the agent have rule scripts, so an application that registers nothing new sees no change.
 *
 * Per scope it shows the version in effect, the candidate and whether it runs in shadow, and the code
 * rule a live version takes the place of. A new version is written in the editor, from scratch or from
 * an earlier version, and saved as the candidate, as the candidate in shadow or straight live. Every
 * version lists with its author, the agent that wrote it for the person, its reason and its times, and
 * opens its script, a dry run on chosen subjects, a comparison with another version and, for the
 * candidate, its shadow runs, shadow on or off, going live after its latest dry run is shown (RB-5), and
 * discarding. Every change expects the rule script's revision; the section reads it again after each one.
 */
@Component({
  selector: 'nh-assistant-agent-rule-scripts',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantRuleScriptEditorComponent,
    NhAssistantRuleScriptDryRunComponent,
    NhAssistantRuleScriptComparisonComponent,
    NhAssistantRuleScriptRunRecordComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-rule-scripts.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', '../page/agent-page.scss', './rule-scripts.scss']
})
export class NhAssistantAgentRuleScriptsComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private loadSubscription?: Subscription;
  private panelSubscription?: Subscription;
  private changeSubscription?: Subscription;

  readonly agent = input.required<AdminAgent>();
  /** Every agent, to name the agent that wrote a version for a person, such as the agent builder. */
  readonly agents = input<readonly AdminAgent[]>([]);
  /** The application's subject types from the run catalog, for choosing the subjects of a dry run. */
  readonly subjectTypes = input<readonly AgentSubjectType[]>([]);
  /** The names of the people who wrote and changed versions, once known. */
  readonly names = input.required<NhAssistantAccountNames>();
  /** Another action of the page is busy. */
  readonly disabled = input(false);

  readonly id = `nh-assistant-agent-rule-scripts-${nextId++}`;
  readonly maxReason = NH_ASSISTANT_RULE_SCRIPT_MAX_REASON;
  readonly statusTone = ruleScriptStatusTone;
  readonly skillNameKeys = skillNameKeys;
  readonly readableId = readableId;

  /** Whether the ceiling lets the agent have rule scripts, from the agent's own rule script; `null` until known. */
  readonly allowed = signal<boolean | null>(null);
  readonly scope = signal<RuleScriptScope>(null);
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly ruleScript = signal<RuleScript | null>(null);
  /** The editor is open, starting from this version or, with `null`, empty. */
  readonly editing = signal<{ base: RuleScriptVersion | null } | null>(null);
  readonly panel = signal<{ version: number; kind: RuleScriptPanel } | null>(null);
  readonly liveReview = signal<LiveReview | null>(null);
  readonly shadowRuns = signal<ShadowRuns | null>(null);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<{ version: number | null; error: NhAssistantError } | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()?.error ?? null));
  /** A saved or changed version, announced once. */
  readonly notice = signal<{ key: string; version: number } | null>(null);

  /** The section shows once the ceiling allows rule scripts, or when they could not be read. */
  readonly visible = computed(() => this.allowed() === true || (this.allowed() === null && this.loadFailed()));
  readonly scopes = computed<RuleScriptScope[]>(() => [null, ...(this.agent().skills ?? []).map(skill => skill.skillId)]);
  readonly live = computed(() => {
    const current = this.ruleScript();
    return current ? ruleScriptVersionOf(current.versions, current.liveVersion) : null;
  });
  readonly candidate = computed(() => {
    const current = this.ruleScript();
    return current ? ruleScriptVersionOf(current.versions, current.candidateVersion) : null;
  });

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.panelSubscription?.unsubscribe();
      this.changeSubscription?.unsubscribe();
    });
  }

  ngOnInit(): void {
    this.load();
  }

  /** Reads the rule script of the chosen scope; the agent's own decides whether the section shows. */
  load(): void {
    const scope = this.scope();
    this.loading.set(true);
    this.loadFailed.set(false);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = this.admin.getRuleScript(this.agent().id, scope).subscribe({
      next: ruleScript => {
        this.ruleScript.set(ruleScript);
        if (scope === null) {
          this.allowed.set(ruleScript.allowed);
        }
        this.names().load(ruleScript.versions.flatMap(version => [version.authorActorId, version.liveBy, version.endedBy]));
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        // A server without rule scripts has no such endpoint: the section stays away, as without the ceiling.
        if (scope === null && error instanceof NhAssistantApiError && error.status === 404) {
          this.allowed.set(false);
          return;
        }
        this.loadFailed.set(true);
      }
    });
  }

  selectScope(scope: RuleScriptScope): void {
    if (this.scope() === scope) {
      return;
    }

    this.scope.set(scope);
    this.ruleScript.set(null);
    this.close();
    this.editing.set(null);
    this.notice.set(null);
    this.load();
  }

  /** Arrow keys move between the scopes, as a group of toggle buttons. */
  onScopeKeydown(event: KeyboardEvent): void {
    const scopes = this.scopes();
    const index = scopes.indexOf(this.scope());
    const last = scopes.length - 1;
    const targets: Record<string, number> = {
      ArrowRight: index === last ? 0 : index + 1,
      ArrowLeft: index === 0 ? last : index - 1
    };
    const next = targets[event.key];
    if (next === undefined) {
      return;
    }

    event.preventDefault();
    this.selectScope(scopes[next]);
    this.focusIn(`[data-rule-script-scope="${scopes[next] ?? ''}"]`);
  }

  scopeKey(scope: RuleScriptScope): string {
    return scope ?? '';
  }

  /** Opens the editor, empty or from a version; the live version is the usual start. */
  write(base: RuleScriptVersion | null): void {
    this.close();
    this.notice.set(null);
    this.editing.set({ base });
  }

  onSaved(version: RuleScriptVersion): void {
    this.editing.set(null);
    this.notice.set({ key: `nh-assistant.admin.rule-scripts.notice.saved-${version.status === 'live' ? 'live' : version.shadow ? 'shadow' : 'candidate'}`, version: version.version });
    // The editor and its button are gone: the focus goes to what happened.
    this.focusIn('[data-rule-script-notice]');
    this.load();
  }

  /** Opens one panel of a version, or closes it when it is open. */
  toggle(version: RuleScriptVersion, kind: RuleScriptPanel): void {
    const open = this.panel();
    if (open?.version === version.version && open.kind === kind) {
      this.close();
      return;
    }

    this.close();
    this.panel.set({ version: version.version, kind });
    if (kind === 'live') {
      this.reviewLive(version);
    } else if (kind === 'shadow-runs') {
      this.readShadowRuns(version);
    }
    if (kind === 'live' || kind === 'discard') {
      this.focusIn(`[data-rule-script-confirm-form="${kind}"] input`);
    }
  }

  isOpen(version: RuleScriptVersion, kind: RuleScriptPanel): boolean {
    const open = this.panel();
    return open?.version === version.version && open.kind === kind;
  }

  close(): void {
    this.panelSubscription?.unsubscribe();
    this.panel.set(null);
    this.liveReview.set(null);
    this.shadowRuns.set(null);
    this.reason.set('');
    this.error.set(null);
  }

  /** Sets the candidate live once its latest dry run, or that it has none, was shown (RB-5). */
  setLive(version: RuleScriptVersion): void {
    this.change(version, 'live', this.admin.setRuleScriptVersionLive(this.agent().id, this.scope(), version.version, {
      expectedRevision: this.ruleScript()?.revision ?? 0,
      reason: this.reasonOrNull()
    }));
  }

  setShadow(version: RuleScriptVersion, shadow: boolean): void {
    this.close();
    this.change(version, shadow ? 'shadow-on' : 'shadow-off', this.admin.setRuleScriptVersionShadow(this.agent().id, this.scope(), version.version, {
      expectedRevision: this.ruleScript()?.revision ?? 0,
      shadow
    }));
  }

  discard(version: RuleScriptVersion): void {
    this.change(version, 'discarded', this.admin.discardRuleScriptVersion(this.agent().id, this.scope(), version.version, {
      expectedRevision: this.ruleScript()?.revision ?? 0,
      reason: this.reasonOrNull()
    }));
  }

  /** A dry run of the candidate that is about to go live replaces what the confirmation shows. */
  onDryRun(record: RuleScriptRunRecord): void {
    const review = this.liveReview();
    if (review?.version === record.version) {
      this.liveReview.set({ ...review, record });
    }
  }

  /** Who wrote a version: the person, or an agent such as the agent builder for the person. */
  authorKey(version: RuleScriptVersion): string {
    return version.authorAgentId
      ? 'nh-assistant.admin.rule-scripts.versions.written-for'
      : 'nh-assistant.admin.rule-scripts.versions.written-by';
  }

  agentName(agentId: string | null): string {
    const agent = agentId ? this.agents().find(item => item.id === agentId) : undefined;
    return agent ? agentDisplayName(this.translate, agent) : agentId ?? '';
  }

  accountName(actorId: string | null | undefined): string {
    return this.names().nameOf(actorId);
  }

  accountTitle(actorId: string | null | undefined): string | null {
    return this.names().idTitleOf(actorId);
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  private reviewLive(version: RuleScriptVersion): void {
    this.liveReview.set({ version: version.version, loading: true, failed: false, record: null });
    this.panelSubscription = this.admin.getRuleScriptDryRun(this.agent().id, this.scope(), version.version).subscribe({
      next: record => this.liveReview.set({ version: version.version, loading: false, failed: false, record }),
      error: () => this.liveReview.set({ version: version.version, loading: false, failed: true, record: null })
    });
  }

  private readShadowRuns(version: RuleScriptVersion): void {
    this.shadowRuns.set({ version: version.version, failed: false, records: null });
    this.panelSubscription = this.admin.getRuleScriptShadowRuns(this.agent().id, this.scope(), version.version).subscribe({
      next: records => this.shadowRuns.set({ version: version.version, failed: false, records }),
      error: () => this.shadowRuns.set({ version: version.version, failed: true, records: null })
    });
  }

  private change(version: RuleScriptVersion, notice: string, request: Observable<unknown>): void {
    this.busy.set(true);
    this.error.set(null);
    this.changeSubscription?.unsubscribe();
    this.changeSubscription = request.subscribe({
      next: () => {
        this.busy.set(false);
        this.close();
        this.notice.set({ key: `nh-assistant.admin.rule-scripts.notice.${notice}`, version: version.version });
        // The confirmation and its button are gone: the focus goes to what happened.
        this.focusIn('[data-rule-script-notice]');
        this.load();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set({ version: version.version, error: toNhAssistantError(error) });
      }
    });
  }

  private reasonOrNull(): string | null {
    const reason = this.reason().trim();
    return reason.length > 0 ? reason : null;
  }

  private focusIn(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }
}
