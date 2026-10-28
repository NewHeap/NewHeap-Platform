import { DatePipe } from '@angular/common';
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
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AdminAgent,
  AgentRunConfiguration,
  DataSkill,
  DataTrigger,
  NhAssistantAdminApiService,
  NhAssistantError,
  ShadowVariantPromotion,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { readableId, skillNameKeys } from '../agent-texts';
import { agentStatus, runsOnItsOwn } from '../agent-operations';
import { NhAssistantAgentEvaluationComponent } from '../nh-assistant-agent-evaluation.component';
import { NhAssistantAgentShadowVariantsComponent } from '../run-config/nh-assistant-agent-shadow-variants.component';
import { NhAssistantAgentStateControlComponent } from './nh-assistant-agent-state-control.component';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';

/** A part of the agent that is not live yet, and what decides it. */
export interface RolloutReview {
  section: 'skills' | 'triggers';
  id: string;
}

let nextId = 0;

/**
 * Everything of an agent that is not live yet, in one place: the agent's own shadow mode, its skills
 * and triggers that work in shadow, the candidate versions of its skills and triggers that run in
 * shadow next to the live version, and its shadow variants with the evaluation that compares them.
 * The shadow modes change here; a candidate is compared and promoted or discarded in Build, which
 * `review` opens at it.
 */
@Component({
  selector: 'nh-assistant-agent-rollout',
  standalone: true,
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAgentStateControlComponent,
    NhAssistantAgentShadowVariantsComponent,
    NhAssistantAgentEvaluationComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-rollout.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantAgentRolloutComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private loadSubscription?: Subscription;
  private actionSubscription?: Subscription;

  readonly agent = input.required<AdminAgent>();
  /** Every agent of the administration, to find the agent's shadow variants. */
  readonly agents = input<readonly AdminAgent[]>([]);
  /** The names of the people who changed the agent's mode, once known. */
  readonly names = input.required<NhAssistantAccountNames>();
  readonly disabled = input(false);
  readonly agentChanged = output<AdminAgent>();
  /** Opens Build at a candidate, where it is compared, promoted or discarded. */
  readonly review = output<RolloutReview>();
  readonly variantCreated = output<AdminAgent>();
  readonly variantPromoted = output<ShadowVariantPromotion>();
  readonly openVariant = output<string>();

  readonly id = `nh-assistant-rollout-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly configuration = signal<AgentRunConfiguration | null>(null);
  readonly busyTrigger = signal<string | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly skillNameKeys = skillNameKeys;
  readonly readableId = readableId;

  readonly status = computed(() => agentStatus(this.agent()));
  /** Whether the agent itself works in shadow: then everything it does stays in shadow. */
  readonly agentInShadow = computed(() => this.agent().shadow?.shadow === true);
  /** Whether the agent runs on its own and can have shadow variants. */
  readonly runs = computed(() => runsOnItsOwn(this.agent()));
  readonly skillsInShadow = computed(() => (this.agent().skills ?? []).filter(skill => skill.shadow?.shadow === true && !skill.shadow.locked));
  readonly triggersInShadow = computed(() => (this.configuration()?.dataTriggers ?? []).filter(trigger => trigger.shadow));
  readonly skillCandidates = computed(() => (this.configuration()?.dataSkills ?? []).filter(skill => skill.candidate));
  readonly triggerCandidates = computed(() => (this.configuration()?.dataTriggers ?? []).filter(trigger => trigger.candidate));
  readonly variants = computed(() => this.agents().filter(item => item.shadowOf === this.agent().id).map(item => item.id));
  /** How many parts are not live yet, besides the agent's own mode. */
  readonly notLive = computed(() =>
    this.skillsInShadow().length + this.triggersInShadow().length + this.skillCandidates().length + this.triggerCandidates().length);

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
    });
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = this.admin.getAgentConfiguration(this.agent().id).subscribe({
      next: configuration => {
        this.configuration.set(configuration);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  /** Puts a trigger in shadow live; the next events it fires on work live. */
  putTriggerLive(trigger: DataTrigger): void {
    this.busyTrigger.set(trigger.triggerId);
    this.error.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = this.admin.setDataTriggerShadow(this.agent().id, trigger.triggerId, { mode: 'live', reason: null }).subscribe({
      next: () => {
        this.busyTrigger.set(null);
        this.load();
      },
      error: (error: unknown) => {
        this.busyTrigger.set(null);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  skillName(skill: DataSkill): { keys: string[]; fallback: string } {
    return { keys: skillNameKeys(this.agent().id, skill.skillId), fallback: readableId(skill.skillId) };
  }

  accountName(actorId: string | null | undefined): string {
    return this.names().nameOf(actorId);
  }

  accountTitle(actorId: string | null | undefined): string | null {
    return this.names().idTitleOf(actorId);
  }

  /** A change of the agent's or a skill's mode also changes what is not live, so the list reads again. */
  onChanged(agent: AdminAgent): void {
    this.agentChanged.emit(agent);
  }
}
