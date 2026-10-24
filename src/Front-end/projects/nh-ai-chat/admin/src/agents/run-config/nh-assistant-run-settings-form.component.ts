import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  AgentAccountability,
  AgentElevatedReads,
  AgentRunSettings,
  AgentRunSettingsRequest,
  NhAssistantAdminApiService,
  RunCatalog,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountPickerComponent } from './nh-assistant-account-picker.component';
import { NhAssistantFileLimitsFieldsComponent } from './nh-assistant-file-limits-fields.component';
import { NhAssistantPackageLockComponent } from '../agent-packages/nh-assistant-package-lock.component';
import { NhAssistantPackageLockedMarkComponent } from '../agent-packages/nh-assistant-package-locked-mark.component';
import { PackageLockView } from '../agent-packages/package-parts';
import { RunConfigForm } from './run-config-form';
import {
  RunConfigErrors,
  blank,
  checkBudget,
  checkWhole,
  describePolicy,
  FileLimitsDraft,
  numberText,
  policyOptions,
  profileOptions,
  ruleOptions,
  runConfigLimits,
  toNumber,
  withBudgetField
} from './run-config-shared';

let nextId = 0;

const defaults = { retries: '3', concurrent: '4', perSubject: '10' };

/**
 * The run settings of an agent created in the administration, as questions: who the agent answers to,
 * how it works, who approves its proposals, and its limits, with the rarely changed options under
 * "Advanced". Errors appear at their fields and next to the actions.
 */
@Component({
  selector: 'nh-assistant-run-settings-form',
  standalone: true,
  imports: [
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantPickerComponent,
    NhAssistantAccountPickerComponent,
    NhAssistantFileLimitsFieldsComponent,
    NhAssistantPackageLockComponent,
    NhAssistantPackageLockedMarkComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-run-settings-form.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantRunSettingsFormComponent extends RunConfigForm implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);

  readonly agentId = input.required<string>();
  readonly catalog = input<RunCatalog | null>(null);
  /** The current run settings; `null` adds them. */
  readonly settings = input<AgentRunSettings | null>(null);
  /**
   * What the run settings' agent package locks (part `runSettings`); `null` without a link. The
   * technical account never locks.
   */
  override readonly lock = input<PackageLockView | null>(null);
  readonly saved = output<AgentRunSettings>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-run-settings-${nextId++}`;
  readonly maxReason = runConfigLimits.reason;
  readonly accountabilities: AgentAccountability[] = ['service-account', 'subject-responsible', 'agent'];
  readonly elevatedReadModes: AgentElevatedReads[] = ['restricted', 'deny'];
  protected readonly fields = [
    'accountability', 'serviceAccountActorId', 'ruleHandler', 'approvalPolicy', 'triageProfile', 'retryCount',
    'maxConcurrentRuns', 'maxRunsPerSubjectPerDay', 'maxTokensPerRun', 'dailyToolCalls', 'dailyTokens',
    'elevatedReads', 'allowGatewayReads', 'reason', 'maxFileBytes', 'maxFilePages', 'maxFilesPerTurn', 'maxFileTokensPerTurn'
  ];

  readonly accountability = signal<AgentAccountability>('service-account');
  readonly serviceAccount = signal('');
  readonly approvalPolicy = signal('');
  readonly ruleHandler = signal('');
  readonly triageProfile = signal('');
  readonly retryCount = signal(defaults.retries);
  readonly maxConcurrentRuns = signal(defaults.concurrent);
  readonly maxRunsPerSubjectPerDay = signal(defaults.perSubject);
  readonly maxTokensPerRun = signal('');
  readonly dailyToolCalls = signal('');
  readonly dailyTokens = signal('');
  readonly allowGatewayReads = signal(false);
  readonly delegateFromInitiator = signal(false);
  /** Whether people's messages and the agent's tools may give it files; off for a new agent. */
  readonly acceptsFiles = signal(false);
  /** The agent's lower file limits, next to accepting files; empty keeps the application's. */
  readonly fileLimits = new FileLimitsDraft();
  readonly elevatedReads = signal<AgentElevatedReads>('restricted');
  readonly reason = signal('');
  readonly advancedOpen = signal(false);

  readonly accountSearch = computed(() => this.catalog()?.accountSearch === true);
  readonly rules = computed(() => ruleOptions(this.catalog()));
  readonly profiles = computed(() => profileOptions(this.catalog()));
  readonly policies = computed(() => {
    this.language();
    return policyOptions(this.catalog(), summary => describePolicy(summary, (key, params) => this.t(key, params)));
  });

  /** An error under "Advanced" keeps it open, so the field with the error can be seen. */
  readonly advancedHasErrors = computed(() => ['retryCount', 'maxConcurrentRuns', 'maxRunsPerSubjectPerDay', 'triageProfile',
    'elevatedReads', 'allowGatewayReads'].some(field => this.fieldError(field) !== null));

  /** Whether the advanced options differ from their defaults. */
  private readonly advancedInUse = computed(() =>
    this.retryCount() !== defaults.retries
    || this.maxConcurrentRuns() !== defaults.concurrent
    || this.maxRunsPerSubjectPerDay() !== defaults.perSubject
    || this.triageProfile() !== ''
    || this.elevatedReads() !== 'restricted'
    || this.allowGatewayReads()
    || this.delegateFromInitiator());

  ngOnInit(): void {
    const settings = this.settings();
    this.accountability.set(settings?.accountability ?? 'service-account');
    this.serviceAccount.set(settings?.serviceAccountActorId ?? '');
    this.approvalPolicy.set(settings?.approvalPolicy ?? '');
    this.ruleHandler.set(settings?.ruleHandler ?? '');
    this.triageProfile.set(settings?.triageProfile ?? '');
    this.retryCount.set(String(settings?.retryCount ?? defaults.retries));
    this.maxConcurrentRuns.set(String(settings?.maxConcurrentRuns ?? defaults.concurrent));
    this.maxRunsPerSubjectPerDay.set(String(settings?.maxRunsPerSubjectPerDay ?? defaults.perSubject));
    this.maxTokensPerRun.set(numberText(settings?.maxTokensPerRun));
    this.dailyToolCalls.set(numberText(settings?.dailyToolCalls));
    this.dailyTokens.set(numberText(settings?.dailyTokens));
    this.allowGatewayReads.set(settings?.allowGatewayReads ?? false);
    this.delegateFromInitiator.set(settings?.delegateFromInitiator ?? false);
    this.acceptsFiles.set(settings?.acceptsFiles ?? false);
    this.fileLimits.load(settings);
    this.elevatedReads.set(settings?.elevatedReads ?? 'restricted');
    this.advancedOpen.set(this.advancedInUse());
  }

  setAccountability(accountability: AgentAccountability): void {
    this.accountability.set(accountability);
    this.touch('accountability');
    this.touch('serviceAccountActorId');
  }

  save(): void {
    const errors: RunConfigErrors = {};
    if (this.accountability() === 'service-account' && this.serviceAccount().trim() === '') {
      errors['serviceAccountActorId'] = { code: 'required' };
    }
    checkWhole(errors, 'retryCount', this.retryCount(), ...runConfigLimits.retries, true);
    checkWhole(errors, 'maxConcurrentRuns', this.maxConcurrentRuns(), ...runConfigLimits.concurrent, true);
    checkWhole(errors, 'maxRunsPerSubjectPerDay', this.maxRunsPerSubjectPerDay(), ...runConfigLimits.perSubject, true);
    checkWhole(errors, 'maxTokensPerRun', this.maxTokensPerRun(), 1);
    checkBudget(errors, this.dailyToolCalls(), this.dailyTokens());
    if (this.acceptsFiles()) {
      this.fileLimits.check(errors, this.catalog()?.fileLimits);
    }

    const request: AgentRunSettingsRequest = {
      accountability: this.accountability(),
      serviceAccountActorId: this.accountability() === 'service-account' ? blank(this.serviceAccount()) : null,
      delegateFromInitiator: this.delegateFromInitiator(),
      allowGatewayReads: this.allowGatewayReads(),
      elevatedReads: this.elevatedReads(),
      approvalPolicy: blank(this.approvalPolicy()),
      retryCount: toNumber(this.retryCount()),
      maxConcurrentRuns: toNumber(this.maxConcurrentRuns()),
      maxRunsPerSubjectPerDay: toNumber(this.maxRunsPerSubjectPerDay()),
      maxTokensPerRun: toNumber(this.maxTokensPerRun()),
      dailyToolCalls: toNumber(this.dailyToolCalls()),
      dailyTokens: toNumber(this.dailyTokens()),
      triageProfile: blank(this.triageProfile()),
      ruleHandler: blank(this.ruleHandler()),
      expectedVersion: this.settings()?.version ?? null,
      reason: blank(this.reason()),
      acceptsFiles: this.acceptsFiles(),
      ...this.fileLimits.toRequest(this.acceptsFiles())
    };
    this.submit(errors, () => this.admin.saveRunSettings(this.agentId(), request), settings => this.saved.emit(settings));
  }

  protected override mapServerErrors(errors: RunConfigErrors): RunConfigErrors {
    return withBudgetField(errors, this.dailyToolCalls());
  }
}
