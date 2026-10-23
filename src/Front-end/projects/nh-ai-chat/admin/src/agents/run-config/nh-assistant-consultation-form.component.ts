import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  AgentAccountability,
  AssistantDataClassification,
  ConsultationCeiling,
  ConsultationSubjectRole,
  DataConsultation,
  DataConsultationRequest,
  NhAssistantAdminApiService,
  NhAssistantConsultationRoute,
  RunCatalog,
  ToolCatalogEntry,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  ɵNhAssistantPickerOption as NhAssistantPickerOption,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { policyKeys } from '../agent-texts';
import {
  ConsultationLimit,
  ConsultationRouteBlock,
  consultationLimits,
  consultationRoles,
  consultationRouteBlock,
  consultationRoutes,
  consultationToolChoices,
  consultationTypeOptions,
  initiatorOnly,
  maxConsultationDescription,
  subjectRoutes
} from './consultations';
import { RunConfigForm } from './run-config-form';
import {
  RunConfigErrors,
  WorksOnStatement,
  blank,
  checkWhole,
  dataClassifications,
  describePolicy,
  numberText,
  policyOptions,
  requireText,
  runConfigLimits,
  toNumber
} from './run-config-shared';

let nextId = 0;

/** The fields under Advanced. */
const advancedFields: readonly string[] = [
  'toolSelectors', 'limits', 'maxToolCalls', 'timeoutSeconds', 'maxAnswerChars', 'maxHistoryMessages', 'allowRequests', 'exposeToMcp', 'dataClassification'
];

/** What a saved consultation form tells its host. */
export interface ConsultationFormSaved {
  consultation: DataConsultation;
  added: boolean;
}

/** A route as the form offers it, with why it cannot be chosen. */
interface RouteChoice {
  route: NhAssistantConsultationRoute;
  checked: boolean;
  block: ConsultationRouteBlock | null;
}

/**
 * Adds or changes a consultation defined as data (AC20): what other assistants may consult the agent
 * about and when (the subject type and a description for the asking model), what the agent keeps in
 * mind (instructions), who may ask (a role on the subject and a policy the application lists), what
 * it may do (only answer, or also propose with routes within the ceiling, with the role and approval
 * policy its proposals need) and, under Advanced, the agent's tools it may use (none checked: every
 * tool of the agent), its limits within the ceiling, the request tool, MCP and the classification of
 * its answers. Routes and options the ceiling or the agent rule out are shown disabled with the
 * reason, and a saved tool the agent no longer has can only be cleared. A consultation is in effect
 * once saved, unless it starts off: by the administrator's choice, or for every new one when the ceiling
 * says so; it is then offered once it is switched on in the list.
 */
@Component({
  selector: 'nh-assistant-consultation-form',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-consultation-form.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantConsultationFormComponent extends RunConfigForm implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);

  readonly agentId = input.required<string>();
  readonly agentSource = input<'code' | 'admin'>('admin');
  readonly catalog = input<RunCatalog | null>(null);
  /** The agent's ceiling in code: the routes, requests, MCP and limits a consultation may use. */
  readonly ceiling = input.required<ConsultationCeiling>();
  /** What the agent works on: a consultation can only be about such subjects. */
  readonly worksOn = input<WorksOnStatement | null>(null);
  /** Whom the agent answers to, which decides whether it may lend its own rights; `null` while unknown. */
  readonly accountability = input<AgentAccountability | null>(null);
  /** The agent's own approval policy, which its proposals use without one of the consultation. */
  readonly agentApprovalPolicy = input<string | null>(null);
  /** The agent's own tool selectors; a consultation's tools stay within them. */
  readonly toolSelectors = input<readonly string[]>([]);
  /** The tool catalog, to show what each of the agent's tool selectors offers. */
  readonly toolCatalog = input<readonly ToolCatalogEntry[]>([]);
  /** The consultation to change; `null` adds one. */
  readonly consultation = input<DataConsultation | null>(null);
  /** The subject types the agent already has a consultation about, in code or as data. */
  readonly takenTypes = input<readonly string[]>([]);
  readonly saved = output<ConsultationFormSaved>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-consultation-form-${nextId++}`;
  readonly maxReason = runConfigLimits.reason;
  readonly maxDescription = maxConsultationDescription;
  readonly roles = consultationRoles;
  protected readonly fields = [
    'subjectType', 'description', 'instructions', 'minimumRole', 'askerPolicy', 'routes', 'proposalRole', 'proposalPolicy',
    'toolSelectors', 'limits', 'maxToolCalls', 'timeoutSeconds', 'maxAnswerChars', 'maxHistoryMessages', 'allowRequests', 'exposeToMcp',
    'dataClassification', 'reason'
  ];

  readonly subjectType = signal('');
  readonly description = signal('');
  readonly instructions = signal('');
  readonly minimumRole = signal<ConsultationSubjectRole>('reader');
  readonly askerPolicy = signal('');
  /** Only answer, or answer and propose with the routes chosen. */
  readonly mode = signal<'answer' | 'propose'>('answer');
  readonly routes = signal<readonly NhAssistantConsultationRoute[]>([]);
  readonly proposalRole = signal<ConsultationSubjectRole>('participant');
  readonly proposalPolicy = signal('');
  /** The tool selectors chosen; none offers every tool of the agent. */
  readonly tools = signal<readonly string[]>([]);
  /** The limits as typed, by field; empty starts at the code's default within the ceiling. */
  readonly limits = signal<Record<ConsultationLimit['field'], string>>({ maxToolCalls: '', timeoutSeconds: '', maxAnswerChars: '', maxHistoryMessages: '' });
  readonly allowRequests = signal(false);
  readonly exposeToMcp = signal(false);
  readonly dataClassification = signal('');
  readonly reason = signal('');
  /** A new consultation starts off, so it is offered only once it is switched on in the list. */
  readonly startsOff = signal(false);
  /** The application lets every new consultation start off; the form then offers no choice. */
  readonly ceilingStartsOff = computed(() => this.ceiling().startsOff === true);
  /** Advanced is open: the administrator opened it, or one of its fields has an error to show. */
  readonly advancedOpen = signal(false);

  readonly editing = computed(() => this.consultation() !== null);
  readonly maxInstructions = computed(() => this.ceiling().maxInstructionsLength);
  /** The subject being chosen or changed. */
  readonly chosenType = computed(() => this.consultation()?.subjectType ?? this.subjectType());
  /** The name of the subject type, as the run catalog names it. */
  readonly typeName = computed(() => {
    const type = this.chosenType();
    return this.catalog()?.subjectTypes?.find(item => item.id === type)?.name ?? type;
  });
  /** The types the agent works on and has no consultation about yet, the whole application included. */
  readonly typeOptions = computed<NhAssistantPickerOption[]>(() => consultationTypeOptions(this.catalog(), this.worksOn(), this.takenTypes())
    .map(type => ({ value: type.id, label: type.name, description: type.isSystem ? null : type.pluralName ?? null })));
  /** The ceiling allows routes, so the consultation may propose. */
  readonly mayPropose = computed(() => this.ceiling().routes.length > 0);
  readonly routeChoices = computed<RouteChoice[]>(() => {
    const chosen = this.routes();
    const agent = { source: this.agentSource(), accountability: this.accountability() };
    return consultationRoutes.map(route => ({
      route,
      checked: chosen.includes(route),
      block: consultationRouteBlock(route, this.ceiling(), this.chosenType(), agent)
    }));
  });
  /** The policies the application lists for askers, by the application's name for each when it has one. */
  readonly askerPolicies = computed<NhAssistantPickerOption[]>(() => {
    this.language();
    return (this.catalog()?.agentPolicies ?? []).map(policy => ({
      value: policy.name,
      label: this.policyName(policy.name),
      description: policy.description
    }));
  });
  readonly policies = computed(() => {
    this.language();
    return policyOptions(this.catalog(), summary => describePolicy(summary, (key, params) => this.t(key, params)));
  });
  /** The approval policy that decides the consultation's proposals: its own, or the agent's. */
  private readonly policySummary = computed(() => {
    const name = blank(this.proposalPolicy()) ?? this.agentApprovalPolicy();
    return name ? this.catalog()?.approvalPolicySummaries?.find(summary => summary.name === name) ?? null : null;
  });
  /**
   * What the approval policy means for the routes chosen: four eyes keeps the person who asked from
   * deciding their own proposal, and a policy of the initiator only leaves no approver for the route
   * with the agent's own rights.
   */
  readonly policyWarnings = computed(() => {
    if (this.mode() !== 'propose') {
      return [];
    }
    const summary = this.policySummary();
    const routes = this.routes();
    const warnings: string[] = [];
    if (summary?.fourEyes && routes.includes('asker-decides')) {
      warnings.push('four-eyes');
    }
    if (initiatorOnly(summary) && routes.includes('subject-agent-rights')) {
      warnings.push('initiator-only');
    }
    return warnings;
  });
  /** The agent's own tool selectors, and the saved selectors that are none of them. */
  readonly toolChoices = computed(() => consultationToolChoices(this.toolSelectors(), this.toolCatalog(), this.consultation()?.toolSelectors ?? []));
  readonly limitFields = computed(() => consultationLimits.map(limit => ({
    ...limit,
    highest: Math.min(limit.max, limit.ceiling(this.ceiling())),
    placeholder: String(Math.min(limit.initial, limit.ceiling(this.ceiling())))
  })));
  readonly classifications = computed(() => {
    this.language();
    return dataClassifications.map(value => ({
      value,
      label: this.t(`nh-assistant.admin.run-config.skills.classification.${value}`),
      description: this.t(`nh-assistant.admin.run-config.skills.classification.${value}-hint`)
    }));
  });

  ngOnInit(): void {
    const consultation = this.consultation();
    this.subjectType.set(consultation?.subjectType ?? '');
    this.description.set(consultation?.description ?? '');
    this.instructions.set(consultation?.instructions ?? '');
    this.minimumRole.set(consultation?.minimumRole ?? 'reader');
    this.askerPolicy.set(consultation?.askerPolicy ?? '');
    this.routes.set([...(consultation?.routes ?? [])]);
    this.mode.set((consultation?.routes.length ?? 0) > 0 ? 'propose' : 'answer');
    this.proposalRole.set(consultation?.proposalRole ?? 'participant');
    this.proposalPolicy.set(consultation?.proposalPolicy ?? '');
    this.tools.set([...(consultation?.toolSelectors ?? [])]);
    this.limits.set({
      maxToolCalls: numberText(consultation?.maxToolCalls),
      timeoutSeconds: numberText(consultation?.timeoutSeconds),
      maxAnswerChars: numberText(consultation?.maxAnswerChars),
      maxHistoryMessages: numberText(consultation?.maxHistoryMessages)
    });
    this.allowRequests.set(consultation?.allowRequests ?? false);
    this.exposeToMcp.set(consultation?.exposeToMcp ?? false);
    this.dataClassification.set(consultation?.dataClassification ?? '');
  }

  setSubjectType(subjectType: string): void {
    this.subjectType.set(subjectType);
    this.touch('subjectType');
    this.touch('routes');
  }

  /** Proposing starts with the first route the agent may use; answering only keeps the routes for later. */
  setMode(mode: 'answer' | 'propose'): void {
    this.mode.set(mode);
    this.touch('routes');
    if (mode === 'propose' && this.routes().length === 0) {
      const first = this.routeChoices().find(choice => choice.block === null);
      if (first) {
        this.routes.set([first.route]);
      }
    }
  }

  /** Chooses a route; of the two routes at the subject one at most, so choosing one drops the other. */
  toggleRoute(route: NhAssistantConsultationRoute, selected: boolean): void {
    this.routes.update(routes => {
      const others = routes.filter(item => item !== route && (!selected || !subjectRoutes.includes(route) || !subjectRoutes.includes(item)));
      return selected ? [...others, route] : others;
    });
    this.touch('routes');
  }

  toggleTool(selector: string, selected: boolean): void {
    this.tools.update(tools => selected
      ? [...tools.filter(tool => tool !== selector), selector]
      : tools.filter(tool => tool !== selector));
    this.touch('toolSelectors');
  }

  /** The text of the tools' error: a selector outside the agent's own tools has its own. */
  toolsErrorKey(): string | null {
    return this.fieldError('toolSelectors')?.code === 'not-allowed'
      ? 'nh-assistant.admin.run-config.field-errors.tools-not-allowed'
      : this.errorKey('toolSelectors');
  }

  /** The id of the text that says why a saved tool can no longer be chosen. */
  toolBlockId(selector: string): string {
    return `${this.id}-tool-${selector.replace(/[^a-z0-9-]/g, '_')}-block`;
  }

  setLimit(field: ConsultationLimit['field'], value: string): void {
    this.limits.update(limits => ({ ...limits, [field]: value }));
    this.touch(field);
    this.touch('limits');
  }

  /** The id of the text that says why a route cannot be chosen. */
  blockId(route: NhAssistantConsultationRoute): string {
    return `${this.id}-route-${route}-block`;
  }

  save(): void {
    const editing = this.consultation();
    const subjectType = editing?.subjectType ?? this.subjectType();
    const propose = this.mode() === 'propose';
    const routes = propose ? this.orderedRoutes() : [];
    const errors: RunConfigErrors = {};
    if (!editing && subjectType === '') {
      errors['subjectType'] = { code: 'required' };
    }
    requireText(errors, 'description', this.description(), this.maxDescription);
    if (this.instructions().length > this.maxInstructions()) {
      errors['instructions'] = { code: 'too-long', params: { max: this.maxInstructions() } };
    }
    if (propose && routes.length === 0) {
      errors['routes'] = { code: 'routes-required' };
    } else if (this.routeChoices().some(choice => choice.checked && choice.block !== null && propose)) {
      errors['routes'] = { code: 'not-allowed' };
    }
    const tools = this.orderedTools();
    if (this.toolChoices().some(choice => !choice.covered && tools.includes(choice.selector))) {
      errors['toolSelectors'] = { code: 'tools-not-allowed' };
    }
    for (const limit of this.limitFields()) {
      checkWhole(errors, limit.field, this.limits()[limit.field], limit.min, limit.highest);
    }

    const ceiling = this.ceiling();
    const request: DataConsultationRequest = {
      description: this.description().trim(),
      instructions: blank(this.instructions()),
      minimumRole: this.minimumRole(),
      askerPolicy: blank(this.askerPolicy()),
      routes,
      proposalRole: this.proposalRole(),
      proposalPolicy: propose ? blank(this.proposalPolicy()) : null,
      // None chosen offers every tool of the agent.
      toolSelectors: tools.length > 0 ? tools : null,
      maxToolCalls: toNumber(this.limits().maxToolCalls),
      timeoutSeconds: toNumber(this.limits().timeoutSeconds),
      maxAnswerChars: toNumber(this.limits().maxAnswerChars),
      maxHistoryMessages: toNumber(this.limits().maxHistoryMessages),
      // A choice the ceiling no longer allows is dropped, as the server would drop it.
      allowRequests: ceiling.allowRequests && this.allowRequests(),
      exposeToMcp: ceiling.allowMcp && this.exposeToMcp(),
      dataClassification: blank(this.dataClassification()) as AssistantDataClassification | null,
      expectedVersion: editing?.version ?? null,
      reason: blank(this.reason()),
      // Only a new consultation starts off; one that exists keeps whether it is on.
      ...(!editing && this.startsOff() && !this.ceilingStartsOff() ? { startsOff: true } : {})
    };
    this.openAdvancedFor(Object.keys(errors));
    this.submit(
      errors,
      () => this.admin.saveDataConsultation(this.agentId(), subjectType, request),
      consultation => this.saved.emit({ consultation, added: editing === null }),
      () => this.openAdvancedFor(Object.keys(this.fieldErrors())));
  }

  isOpen(event: Event): boolean {
    return (event.target as HTMLDetailsElement).open;
  }

  /** Opens Advanced when one of its fields has an error, so the error and the focus can reach it. */
  private openAdvancedFor(fields: readonly string[]): void {
    if (fields.some(field => advancedFields.includes(field))) {
      this.advancedOpen.set(true);
    }
  }

  /** A policy's name as the application translates it, or the policy itself. */
  private policyName(policy: string): string {
    const [key] = policyKeys(policy);
    const translated = this.t(key);
    return translated && translated !== key ? translated : policy;
  }

  /** The chosen tool selectors in the order the form offers them. */
  private orderedTools(): string[] {
    const chosen = this.tools();
    return this.toolChoices().map(choice => choice.selector).filter(selector => chosen.includes(selector));
  }

  /** The chosen routes in the order the form offers them. */
  private orderedRoutes(): NhAssistantConsultationRoute[] {
    const chosen = this.routes();
    return consultationRoutes.filter(route => chosen.includes(route));
  }
}
