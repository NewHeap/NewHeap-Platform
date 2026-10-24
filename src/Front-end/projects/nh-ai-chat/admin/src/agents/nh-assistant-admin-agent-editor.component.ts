import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked
} from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Observable, Subscription, map, of, switchMap, tap } from 'rxjs';
import {
  AdminAgent,
  AdminAgentGroups,
  AdminAgentInput,
  AssistantAutonomy,
  AssistantDataClassification,
  InstructionAssetReference,
  McpServer,
  ɵNH_ASSISTANT_DASH_CASE as NH_ASSISTANT_DASH_CASE,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError,
  RunCatalog,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { NH_ASSISTANT_MAX_INSTRUCTIONS } from '../context/nh-assistant-admin-context.component';
import { policyKeys, warningKeys, worksOnSummary } from './agent-texts';
import { NhAssistantAgentMetadataComponent } from './metadata/nh-assistant-agent-metadata.component';
import { InstructionsSource, NhAssistantInstructionsAssetComponent, sameInstructionAsset } from './run-config/nh-assistant-instructions-asset.component';
import { RunConfigFieldError } from './run-config/run-config-shared';
import { NhAssistantPackageLockComponent } from './agent-packages/nh-assistant-package-lock.component';
import { NhAssistantPackageLockedMarkComponent } from './agent-packages/nh-assistant-package-locked-mark.component';
import { PackageLockView, agentDefinitionLockOf, isPackageFieldLocked } from './agent-packages/package-parts';

/** True when a tool id matches a selector glob (`*` matches any characters). */
export function nhAssistantSelectorMatches(toolId: string, selector: string): boolean {
  const pattern = selector.split('*').map(part => part.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*');
  return new RegExp(`^${pattern}$`).test(toolId);
}

const selectorPattern = /^[a-z0-9*][a-z0-9.*_-]*$/i;

/** A text of an agent in code: its translation key and the translation the editor shows instead. */
interface KeyedText {
  key: string;
  shown: string;
}

/** How an agent created in the administration is used: chosen in the chat, working on its own, or both. */
export type AgentUse = 'chat' | 'background' | 'both';

/** Whether an agent created in the administration works on every object type or on chosen ones. */
export type WorksOnScope = 'all' | 'types';

/** The most object types one agent works on, as the server allows. */
const maxWorksOnTypes = 20;

let nextId = 0;

/**
 * Creates or edits one agent: how it is used (offered in the chat, working on its own, or both),
 * what it works on (all objects or chosen object types, the whole application, per division when
 * the application lists divisions, and who may run it and see its results for the whole
 * application), texts, autonomy, policy, tool selectors chosen from the tool catalog or typed as
 * patterns, and assigned MCP servers. A new agent meant to work on its own continues with
 * `workRequested`. An agent in code names and describes itself with translation keys: the editor
 * shows their translation and sends the key back unless the administrator changed the text, so the
 * agent stays translated; what it works on comes from its code and is not edited here. Below the form,
 * an existing agent shows the application's fields (`nh-assistant-agent-metadata`): one slot per entry
 * of agent metadata, where the application's own editor saves the value; a change of a field of an
 * agent created here is its next version, which the next save of the definition continues on. Fields
 * of the definition that the agent's package locks show read-only with a lock and the package's id and
 * version, with "Unlink" for the definition; a save keeps their values.
 */
@Component({
  selector: 'nh-assistant-admin-agent-editor',
  standalone: true,
  imports: [
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAgentMetadataComponent,
    NhAssistantInstructionsAssetComponent,
    NhAssistantPackageLockComponent,
    NhAssistantPackageLockedMarkComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-agent-editor.component.html',
  styleUrl: '../nh-assistant-admin-shared.scss'
})
export class NhAssistantAdminAgentEditorComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  private subscription?: Subscription;
  private keyedName: KeyedText | null = null;
  private keyedDescription: KeyedText | null = null;

  /** The agent to edit, or null for a new agent. */
  readonly agent = input<AdminAgent | null>(null);
  readonly tools = input<readonly ToolCatalogEntry[]>([]);
  readonly servers = input<readonly McpServer[]>([]);
  /** The run catalog: the object types by name, the policies to choose and whether divisions are listed. */
  readonly catalog = input<RunCatalog | null>(null);
  /**
   * The run catalog could not be read: the editor then leaves out what the agent works on and its
   * groups, which depend on it, and a save keeps them as they are.
   */
  readonly catalogUnavailable = input(false);
  readonly saved = output<AdminAgent>();
  /**
   * A new agent meant to work on its own was created; the host continues with its run
   * configuration's guided setup. `saved` is not emitted for it.
   */
  readonly workRequested = output<AdminAgent>();
  readonly cancelled = output<void>();
  readonly uses: readonly AgentUse[] = ['chat', 'background', 'both'];

  readonly id = `nh-assistant-agent-editor-${nextId++}`;
  readonly max = NH_ASSISTANT_MAX_INSTRUCTIONS;
  readonly autonomyLevels: readonly AssistantAutonomy[] = ['observe', 'explain', 'propose', 'simulate', 'execute'];
  readonly classifications: readonly AssistantDataClassification[] = ['public', 'internal', 'confidential', 'restricted'];

  readonly agentId = signal('');
  readonly displayName = signal('');
  readonly description = signal('');
  readonly instructions = signal('');
  /** Whether the instructions of an agent created here are written here or come from an asset of the application. */
  readonly instructionsSource = signal<InstructionsSource>('inline');
  /** The application's asset the agent names, once chosen and read. */
  readonly instructionsAsset = signal<InstructionAssetReference | null>(null);
  readonly autonomy = signal<AssistantAutonomy>('explain');
  /** How the agent is used: in the chat, on its own, or both. */
  readonly use = signal<AgentUse>('chat');
  /** A new agent takes its triggers at once; without it, it starts off until someone switches it on. */
  readonly switchOnNow = signal(true);
  /**
   * A new agent starts in shadow: once it has run settings its runs work in its shadow threads, where
   * nobody is notified and nothing executes, until someone puts it live.
   */
  readonly startsInShadow = signal(false);
  /** The model profile of an agent created here; empty uses the application's chat profile. */
  readonly profile = signal('');
  /** The classification of the model calls of an agent created here; `internal` when it has none. */
  readonly dataClassification = signal<AssistantDataClassification>('internal');
  readonly requiredPolicy = signal('');
  readonly toolSelectors = signal<string[]>([]);
  readonly mcpServerIds = signal<string[]>([]);
  /** What the agent works on: all objects or the chosen object types. */
  readonly worksOnScope = signal<WorksOnScope>('all');
  readonly worksOnTypes = signal<string[]>([]);
  readonly worksOnApplication = signal(false);
  readonly perDivision = signal(false);
  readonly runPolicy = signal('');
  readonly viewPolicy = signal('');
  /** The groups of agents the agent is in, by id. */
  readonly groups = signal<string[]>([]);
  readonly newSelector = signal('');
  readonly catalogFilter = signal('');
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly error = signal<NhAssistantError | null>(null);

  /**
   * A new agent that was created while its groups could not be saved yet: the next save completes it
   * as an existing agent.
   */
  private readonly created = signal<AdminAgent | null>(null);
  /** The agent the next save updates; `null` while a new agent is not created yet. */
  private readonly editing = computed(() => this.created() ?? this.agent());
  /**
   * The agent's version after one of its application fields changed, which made its next version;
   * `null` while no field changed.
   */
  private readonly fieldsVersion = signal<number | null>(null);
  /** The definition was unlinked from its package here, so it no longer locks. */
  private readonly unlinked = signal(false);
  /** What the agent's package locks of its definition (part `agent`); `null` without a link. */
  readonly lock = computed(() => this.unlinked() ? null : agentDefinitionLockOf(this.agent()));
  /** The agent whose application fields show below the form; `null` for a new agent. */
  readonly metadataAgent = computed(() => this.editing());
  /** The entries of agent metadata the application registers, for a new agent. */
  readonly metadataEntries = computed(() => this.catalog()?.agentMetadata ?? []);

  readonly isNew = computed(() => this.editing() === null);
  /** Only agents created here choose whether they are offered in the chat; code decides for the others. */
  readonly choosesUse = computed(() => this.agent()?.source !== 'code');
  readonly idInvalid = computed(() => this.isNew() && !NH_ASSISTANT_DASH_CASE.test(this.agentId()));
  readonly nameMissing = computed(() => this.displayName().trim().length === 0);
  readonly tooLong = computed(() => this.instructionsSource() === 'inline' && this.instructions().length > this.max);
  /**
   * Only an agent created here takes its instructions from an asset, when the application offers its
   * assets; one that names an asset keeps showing it.
   */
  readonly assetsAvailable = computed(() => this.choosesUse() && this.catalog()?.instructionAssets === true);
  /** An asset is the source, but none is chosen and read yet. */
  readonly assetMissing = computed(() => this.instructionsSource() === 'asset' && this.instructionsAsset() === null);
  /** The problem of the asset the saved agent names, while it is still the choice. */
  readonly assetProblem = computed(() => {
    const agent = this.agent();
    return agent?.instructionsProblem && sameInstructionAsset(agent.instructionsAsset, this.instructionsAsset()) ? agent.instructionsProblem : null;
  });
  /** The error of the asset choice: chosen none, or the save's refusal of the reference. */
  readonly assetError = computed<RunConfigFieldError | null>(() => {
    const errors = this.error()?.fieldErrors;
    if (errors?.['instructionsAsset.hash']?.length) {
      return { code: 'changed' };
    }
    const code = errors?.['instructionsAsset']?.[0];
    if (code) {
      return { code };
    }
    return this.submitted() && this.assetMissing() ? { code: 'required' } : null;
  });
  readonly selectorInvalid = computed(() => this.newSelector().trim().length > 0 && !selectorPattern.test(this.newSelector().trim()));
  /**
   * The application's own object types, by name; the platform's types, such as the whole
   * application, are chosen otherwise. A stored type the application no longer registers stays
   * listed by its id, so it can be unticked.
   */
  readonly subjectTypes = computed(() => {
    const catalog = this.catalog()?.subjectTypes ?? [];
    const registered = catalog.filter(type => !type.isSystem);
    const stored = (this.agent()?.worksOnSubjectTypes ?? [])
      .filter(id => !id.startsWith('nh-') && !catalog.some(type => type.id === id))
      .map(id => ({ id, name: id, pluralName: null as string | null }));
    return [...registered, ...stored];
  });
  readonly agentPolicies = computed(() => this.catalog()?.agentPolicies ?? []);
  /** The groups of agents the application registers, which boards show. */
  readonly agentGroups = computed(() => this.catalog()?.agentGroups ?? []);
  /** The groups the agent joins in code. */
  readonly codeGroups = computed(() => this.agent()?.codeGroups ?? []);
  /** A shadow variant works for administrators only and joins no group. */
  readonly isVariant = computed(() => !!this.agent()?.shadowOf);
  /** The server's refusal of the groups, as a translation key. */
  readonly groupsError = computed(() => this.fieldError('groups'));
  /** Per division is offered only when the application lists its divisions. */
  readonly divisionsListed = computed(() => this.catalog()?.divisions === true);
  /** The object types the agent is known to work on, by name, as a hint next to "All objects". */
  readonly knownTypes = computed(() => {
    const agent = this.agent();
    return agent ? worksOnSummary(agent, this.catalog()?.subjectTypes).known : [];
  });
  /** What the agent works on is edited here: an agent created here, while the run catalog is known. */
  readonly editsWorksOn = computed(() => this.choosesUse() && !this.catalogUnavailable());
  /** An agent created here chooses its profile from those the run catalog offers. */
  readonly choosesProfile = computed(() => this.choosesUse() && !this.catalogUnavailable());
  /** The profiles the run catalog offers, and the agent's own when the catalog no longer lists it. */
  readonly profileChoices = computed(() => {
    const listed = this.catalog()?.profiles ?? [];
    const current = this.agent()?.profile;
    return current && !listed.includes(current) ? [...listed, current] : listed;
  });
  /** Chosen object types without a type, and not the application either: the agent would work on nothing. */
  readonly worksOnMissing = computed(() =>
    this.editsWorksOn() && this.worksOnScope() === 'types' && this.worksOnTypes().length === 0 && !this.worksOnApplication());
  readonly tooManyTypes = computed(() =>
    this.editsWorksOn() && this.worksOnScope() === 'types' && this.worksOnTypes().length > maxWorksOnTypes);
  readonly policyKeys = policyKeys;
  readonly warningKeys = warningKeys;
  /** The warnings of the agent as it was saved, until this editor saves it again. */
  readonly warnings = computed(() => this.agent()?.warnings ?? []);
  readonly valid = computed(() => !this.idInvalid() && !this.nameMissing() && !this.tooLong() && !this.assetMissing() && !this.worksOnMissing() && !this.tooManyTypes());
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly filteredTools = computed(() => {
    const filter = this.catalogFilter().trim().toLowerCase();
    return this.tools().filter(tool => !filter ||
      tool.id.toLowerCase().includes(filter) || tool.description.toLowerCase().includes(filter));
  });
  readonly matchCount = computed(() => this.tools()
    .filter(tool => this.toolSelectors().some(selector => nhAssistantSelectorMatches(tool.id, selector))).length);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());

    effect(() => {
      const agent = this.agent();
      untracked(() => this.reset(agent));
    });
  }

  isSelected(toolId: string): boolean {
    return this.toolSelectors().includes(toolId);
  }

  isCovered(toolId: string): boolean {
    return this.toolSelectors().some(selector => selector !== toolId && nhAssistantSelectorMatches(toolId, selector));
  }

  toggleTool(toolId: string, checked: boolean): void {
    this.toolSelectors.update(selectors => checked
      ? [...selectors.filter(selector => selector !== toolId), toolId]
      : selectors.filter(selector => selector !== toolId));
  }

  addSelector(): void {
    const selector = this.newSelector().trim();
    if (selector.length === 0 || this.selectorInvalid()) {
      return;
    }

    this.toolSelectors.update(selectors => selectors.includes(selector) ? selectors : [...selectors, selector]);
    this.newSelector.set('');
  }

  onSelectorKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      this.addSelector();
    }
  }

  removeSelector(selector: string): void {
    this.toolSelectors.update(selectors => selectors.filter(item => item !== selector));
  }

  toggleWorksOnType(typeId: string, checked: boolean): void {
    this.worksOnTypes.update(types => checked ? [...types.filter(type => type !== typeId), typeId] : types.filter(type => type !== typeId));
  }

  toggleGroup(groupId: string, checked: boolean): void {
    this.groups.update(groups => checked ? [...groups.filter(group => group !== groupId), groupId] : groups.filter(group => group !== groupId));
  }

  toggleServer(serverId: string, checked: boolean): void {
    this.mcpServerIds.update(ids => checked ? [...ids.filter(id => id !== serverId), serverId] : ids.filter(id => id !== serverId));
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value;
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  /** Whether the agent's package locks a field of its definition, which then shows read-only. */
  locked(field: string): boolean {
    return isPackageFieldLocked(this.lock(), field);
  }

  /** The lock the mark next to a field's label shows; `null` for a field that changes here. */
  lockOf(field: string): PackageLockView | null {
    return this.locked(field) ? this.lock() : null;
  }

  /** The definition was unlinked from its package: its fields change here again. */
  onUnlinked(): void {
    this.unlinked.set(true);
  }

  /** The first error the server reported for a field, as a translation key. */
  fieldError(field: string): string | null {
    const code = this.error()?.fieldErrors?.[field]?.[0];
    return code ? `nh-assistant.admin.run-config.field-errors.${code}` : null;
  }

  /** An application field of an agent created here made the agent's next version. */
  fieldsChanged(agentVersion: number): void {
    this.fieldsVersion.set(agentVersion);
  }

  save(): void {
    this.submitted.set(true);
    if (!this.valid() || this.saving()) {
      return;
    }

    const policy = this.requiredPolicy().trim();
    const fromAsset = this.instructionsSource() === 'asset';
    const input: AdminAgentInput = {
      id: this.isNew() ? this.agentId() : this.editing()!.id,
      displayName: keyOrText(this.displayName(), this.keyedName),
      description: keyOrText(this.description(), this.keyedDescription),
      // An agent that names an asset stores no text; omitted, the instructions are text again.
      instructions: fromAsset ? '' : this.instructions(),
      ...(fromAsset ? { instructionsAsset: this.instructionsAsset() } : {}),
      toolSelectors: this.toolSelectors(),
      mcpServerIds: this.mcpServerIds(),
      requiredPolicy: policy.length > 0 ? policy : null,
      autonomy: this.autonomy(),
      ...(this.choosesUse() ? { offeredInChat: this.use() !== 'background' } : {}),
      // Omitted, what the agent works on stays as it is; a new agent then works on every object type.
      ...(this.editsWorksOn() ? this.worksOnInput() : {}),
      ...(this.isNew() && !this.switchOnNow() ? { startsOff: true } : {}),
      ...(this.isNew() && this.use() !== 'chat' && this.startsInShadow() ? { startsInShadow: true } : {}),
      // Omitted, the profile stays as it is; a new agent then uses the application's chat profile.
      ...(this.choosesProfile() && this.profile() !== '' ? { profile: this.profile() } : {}),
      // Sent only when it changes, so an agent without a classification keeps none.
      ...(this.choosesUse() && this.dataClassification() !== (this.editing()?.dataClassification ?? 'internal')
        ? { dataClassification: this.dataClassification() }
        : {})
    };
    const startsWork = this.agent() === null && this.use() !== 'chat';

    // An existing agent's groups are saved before its definition, a new agent's right after it is
    // created; a new agent whose groups failed is completed as an existing one by the next save.
    // Without the run catalog the groups are not shown, so they stay as they are.
    const existing = this.editing();
    const groups = this.catalogUnavailable() ? [...(existing?.groups ?? [])].sort() : [...this.groups()].sort();
    const groupsChanged = !sameGroups(groups, existing?.groups ?? []);
    const groupsSaved: Observable<AdminAgentGroups | null> = existing && groupsChanged && !this.isVariant()
      ? this.admin.setAgentGroups(existing.id, groups)
      : of(null);
    const request: Observable<AdminAgent> = existing
      ? groupsSaved.pipe(
        switchMap(membership => this.admin.updateAgent(existing.id, { ...input, expectedVersion: this.fieldsVersion() ?? existing.version }).pipe(
          map(agent => withGroups(agent, membership)))))
      : this.admin.createAgent(input).pipe(
        tap(agent => this.created.set(agent)),
        switchMap(agent => groups.length > 0
          ? this.admin.setAgentGroups(agent.id, groups).pipe(map(membership => withGroups(agent, membership)))
          : of(agent)));

    this.saving.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = request.subscribe({
      next: agent => {
        this.saving.set(false);
        if (startsWork) {
          this.workRequested.emit(agent);
        } else {
          this.saved.emit(agent);
        }
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  /** What the agent works on, as the six fields the server reads together. */
  private worksOnInput(): Pick<AdminAgentInput,
    'worksOnSubjectTypes' | 'worksOnAllSubjects' | 'worksOnApplication' | 'perDivision' | 'runPolicy' | 'viewPolicy'> {
    const all = this.worksOnScope() === 'all';
    return {
      worksOnSubjectTypes: all ? [] : [...this.worksOnTypes()].sort(),
      worksOnAllSubjects: all,
      worksOnApplication: this.worksOnApplication(),
      perDivision: this.divisionsListed() && this.perDivision(),
      runPolicy: this.runPolicy() || null,
      viewPolicy: this.viewPolicy() || null
    };
  }

  private reset(agent: AdminAgent | null): void {
    this.agentId.set(agent?.id ?? '');
    this.keyedName = agent?.source === 'code' ? this.keyed(agent.displayName) : null;
    this.keyedDescription = agent?.source === 'code' ? this.keyed(agent.description) : null;
    this.displayName.set(this.keyedName?.shown ?? agent?.displayName ?? '');
    this.description.set(this.keyedDescription?.shown ?? agent?.description ?? '');
    // The asset's text, read-only while the agent names it, is where text written here starts from.
    this.instructions.set(agent?.instructions ?? '');
    this.instructionsSource.set(agent?.source === 'admin' && agent.instructionsAsset ? 'asset' : 'inline');
    this.instructionsAsset.set(agent?.source === 'admin' ? agent.instructionsAsset ?? null : null);
    this.autonomy.set(agent?.autonomy ?? 'explain');
    this.use.set(agent?.offeredInChat === false ? 'background' : 'chat');
    this.switchOnNow.set(true);
    this.startsInShadow.set(false);
    this.profile.set(agent?.source === 'admin' ? agent.profile ?? '' : '');
    this.dataClassification.set(agent?.source === 'admin' ? agent.dataClassification ?? 'internal' : 'internal');
    this.requiredPolicy.set(agent?.requiredPolicy ?? '');
    this.toolSelectors.set([...(agent?.toolSelectors ?? [])]);
    this.mcpServerIds.set([...(agent?.mcpServerIds ?? [])]);
    const worksOn = worksOnSummary(agent ?? {}, null);
    this.worksOnScope.set(worksOn.all ? 'all' : 'types');
    this.worksOnTypes.set([...(worksOn.all ? [] : agent?.worksOnSubjectTypes ?? [])]);
    this.worksOnApplication.set(worksOn.application);
    this.perDivision.set(worksOn.perDivision);
    this.runPolicy.set(worksOn.runPolicy ?? '');
    this.viewPolicy.set(worksOn.viewPolicy ?? '');
    this.groups.set([...(agent?.groups ?? [])]);
    this.created.set(null);
    this.unlinked.set(false);
    this.fieldsVersion.set(null);
    this.submitted.set(false);
    this.error.set(null);
  }

  /** The translation of a key of an agent in code, or `null` when the text is not a key with a translation. */
  private keyed(text: string | null | undefined): KeyedText | null {
    if (!text) {
      return null;
    }
    const shown = this.translate.instant(text) as unknown;
    return typeof shown === 'string' && shown.length > 0 && shown !== text ? { key: text, shown } : null;
  }
}

/** Whether two sorted lists of group ids are the same. */
function sameGroups(left: readonly string[], right: readonly string[]): boolean {
  const sorted = [...right].sort();
  return left.length === sorted.length && left.every((group, index) => group === sorted[index]);
}

/** The agent with the groups the server answered, or as it is without an answer. */
function withGroups(agent: AdminAgent, membership: AdminAgentGroups | null): AdminAgent {
  return membership ? { ...agent, groups: membership.groups, codeGroups: membership.codeGroups } : agent;
}

/** The key of a text of an agent in code while the administrator kept its translation, otherwise the typed text. */
function keyOrText(value: string, keyed: KeyedText | null): string {
  const text = value.trim();
  return keyed !== null && text === keyed.shown.trim() ? keyed.key : text;
}
