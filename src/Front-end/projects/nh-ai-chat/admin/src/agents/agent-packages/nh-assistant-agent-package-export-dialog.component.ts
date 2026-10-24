import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Observable, Subscription, catchError, forkJoin, map, of, startWith } from 'rxjs';
import {
  AdminAgent,
  AgentHandOffs,
  AgentMetadata,
  AgentPackageExportOptions,
  AgentPackagePartKind,
  AgentPackageWarning,
  AgentRunConfiguration,
  NH_ASSISTANT_AGENT_PACKAGE_MAX_AGENTS,
  ɵNH_ASSISTANT_DASH_CASE as NH_ASSISTANT_DASH_CASE,
  NH_ASSISTANT_PACKAGE_LOCK_ALL,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { codeTextKeys } from '../agent-texts';
import { PackagePart, lockableFields, packageFieldLabelKey, packagePartLabelKey, packagePartOf } from './package-parts';

/** How a part is locked in the package. */
export type PackageLockMode = 'none' | 'whole' | 'fields';

/** The lock chosen for one part. */
interface LockChoice {
  mode: PackageLockMode;
  fields: readonly string[];
}

/** What the dialog read of one agent: the parts that travel as data and what they refer to. */
interface AgentParts {
  agentId: string;
  parts: PackagePart[];
  /** References to other agents: hand-off sources and consultation askers, by part. */
  references: AgentPackageWarning[];
}

/** One agent with its parts and their lock choices, as the dialog lists it. */
interface AgentLocks {
  agent: AdminAgent;
  name: string;
  loading: boolean;
  parts: { part: PackagePart; label: string; choice: LockChoice; fields: readonly { field: string; label: string }[] }[];
}

/** The longest package id. */
const maxPackageId = 128;
/** The longest package description. */
const maxDescription = 500;

let nextId = 0;

/**
 * Exports agents as an agent package (internal; an agent's card opens it with that agent chosen): the
 * agents, exactly as chosen; the package id, version and description, empty for the server's
 * defaults (the linked package's id and next version, otherwise the first agent's id and 1); per part
 * of each agent whether the package locks it, as a whole or by its lockable fields; and every reference
 * to an agent outside the selection, with "Add" to take that agent along. The parts and references come
 * from the administration's reads, so choosing changes nothing and is not audited; "Download" exports,
 * which the server audits, and saves `agent-package-{id}.json`. A modal dialog that opens itself and
 * emits `closed`; Escape closes it.
 */
@Component({
  selector: 'nh-assistant-agent-package-export-dialog',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-package-export-dialog.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './package-dialog.scss']
})
export class NhAssistantAgentPackageExportDialogComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly reads = new Map<string, Subscription>();
  private subscription?: Subscription;

  /** Every agent of the host, to choose from. */
  readonly agents = input<readonly AdminAgent[]>([]);
  /** The agent the export starts from; it is chosen when the dialog opens. */
  readonly start = input.required<AdminAgent>();

  /** The dialog closed; the host removes it. */
  readonly closed = output<void>();

  readonly id = `nh-assistant-package-export-${nextId++}`;
  readonly maxAgents = NH_ASSISTANT_AGENT_PACKAGE_MAX_AGENTS;
  readonly maxDescription = maxDescription;
  readonly lockModes: readonly PackageLockMode[] = ['none', 'whole', 'fields'];
  readonly chosen = signal<readonly string[] | null>(null);
  readonly packageId = signal('');
  readonly version = signal('');
  readonly description = signal('');
  readonly locks = signal<Readonly<Record<string, Readonly<Record<string, LockChoice>>>>>({});
  /** What was read of each chosen agent, by agent id. */
  readonly parts = signal<ReadonlyMap<string, AgentParts>>(new Map());
  /** Agents whose parts could not be read. */
  readonly failed = signal<ReadonlySet<string>>(new Set());
  readonly submitted = signal(false);
  readonly downloading = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** The file the last export saved, with the references the server reported. */
  readonly downloaded = signal<{ file: string; warnings: readonly AgentPackageWarning[] } | null>(null);

  /** The language, so the lists follow a switch. */
  private readonly language = toSignal(
    this.translate.onLangChange.pipe(map(event => event.lang), startWith(this.translate.currentLang)),
    { initialValue: this.translate.currentLang });

  /** The chosen agent ids, in the order of the host's list; the starting agent until a choice changes. */
  readonly selected = computed(() => {
    const chosen = this.chosen() ?? [this.start().id];
    return this.agents().map(agent => agent.id).filter(agentId => chosen.includes(agentId));
  });

  /** The first chosen agent, whose package and id the defaults come from. */
  private readonly first = computed(() => this.agents().find(agent => agent.id === this.selected()[0]) ?? null);
  /** The package id the server uses when none is given. */
  readonly defaultPackageId = computed(() => this.first()?.package?.packageId ?? this.first()?.id ?? '');
  /** The version the server uses when none is given: the next after the linked package's, otherwise 1. */
  readonly defaultVersion = computed(() => (this.first()?.package?.packageVersion ?? 0) + 1);

  readonly packageIdError = computed(() => {
    const value = this.packageId().trim();
    return value.length > 0 && (!NH_ASSISTANT_DASH_CASE.test(value) || value.length > maxPackageId) ? 'nh-assistant.admin.invalid-id' : this.serverFieldError('packageId');
  });
  readonly versionError = computed(() => {
    const value = this.version().trim();
    return value.length > 0 && !/^[1-9][0-9]{0,8}$/.test(value) ? 'nh-assistant.admin.run-config.field-errors.invalid' : this.serverFieldError('version');
  });
  readonly descriptionError = computed(() =>
    this.description().length > maxDescription ? 'nh-assistant.admin.too-long' : this.serverFieldError('description'));
  readonly locksError = computed(() => this.serverFieldError('locks'));
  readonly selectionError = computed(() => {
    const count = this.selected().length;
    if (count === 0) {
      return this.submitted() ? 'nh-assistant.admin.packages.export-dialog.agents-required' : null;
    }
    return count > NH_ASSISTANT_AGENT_PACKAGE_MAX_AGENTS ? 'nh-assistant.admin.packages.export-dialog.too-many' : this.serverFieldError('agentIds');
  });

  /** The chosen agents with their parts and lock choices. */
  readonly chosenAgents = computed<AgentLocks[]>(() => {
    this.language();
    const parts = this.parts();
    const locks = this.locks();
    return this.selected()
      .map(agentId => this.agents().find(agent => agent.id === agentId))
      .filter((agent): agent is AdminAgent => !!agent)
      .map(agent => {
        const read = parts.get(agent.id);
        return {
          agent,
          name: this.agentName(agent.id),
          loading: !read && !this.failed().has(agent.id),
          parts: (read?.parts ?? []).map(part => ({
            part,
            label: this.partLabel(part),
            choice: locks[agent.id]?.[part.key] ?? { mode: 'none', fields: [] },
            fields: lockableFields(part.kind, agent.source).map(field => ({ field, label: this.fieldLabel(field) }))
          }))
        };
      });
  });

  /** Every reference of the chosen agents to an agent outside the selection. */
  readonly references = computed<AgentPackageWarning[]>(() => {
    const selected = new Set(this.selected());
    const warnings: AgentPackageWarning[] = [];
    for (const agentId of selected) {
      const agent = this.agents().find(item => item.id === agentId);
      if (agent?.shadowOf && !selected.has(agent.shadowOf)) {
        warnings.push({ agentId, part: 'agent', code: 'shadow-of', referencedAgentId: agent.shadowOf });
      }
      for (const variant of this.agents().filter(item => item.shadowOf === agentId && !selected.has(item.id))) {
        warnings.push({ agentId, part: 'agent', code: 'shadow-variant', referencedAgentId: variant.id });
      }
      warnings.push(...(this.parts().get(agentId)?.references ?? []).filter(item => !selected.has(item.referencedAgentId)));
    }
    return warnings;
  });

  readonly canDownload = computed(() => this.selected().length > 0
    && this.selected().length <= NH_ASSISTANT_AGENT_PACKAGE_MAX_AGENTS
    && !this.downloading()
    && this.packageIdError() === null
    && this.versionError() === null
    && this.description().length <= maxDescription);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      for (const read of this.reads.values()) {
        read.unsubscribe();
      }
    });
    afterNextRender(() => {
      const dialog = this.dialog().nativeElement;
      if (!dialog.open) {
        dialog.showModal();
      }
      this.readParts();
      this.host.nativeElement.querySelector<HTMLElement>(`[data-export-agent="${this.start().id}"] input`)?.focus();
    }, { injector: this.injector });
  }

  isChosen(agentId: string): boolean {
    return this.selected().includes(agentId);
  }

  toggleAgent(agentId: string, checked: boolean): void {
    const selected = this.selected();
    this.chosen.set(checked ? [...selected.filter(id => id !== agentId), agentId] : selected.filter(id => id !== agentId));
    this.downloaded.set(null);
    this.error.set(null);
    this.readParts();
  }

  /** Takes the agent a reference names along in the package. */
  add(agentId: string): void {
    this.toggleAgent(agentId, true);
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`[data-export-agent="${agentId}"] input`)?.focus(), { injector: this.injector });
  }

  /** Whether the host has the agent a reference names, so it can be taken along. */
  canAdd(agentId: string): boolean {
    return this.agents().some(agent => agent.id === agentId) && this.selected().length < NH_ASSISTANT_AGENT_PACKAGE_MAX_AGENTS;
  }

  setLockMode(agentId: string, part: string, mode: PackageLockMode): void {
    this.updateLock(agentId, part, choice => ({ mode, fields: mode === 'fields' ? choice.fields : [] }));
  }

  toggleLockField(agentId: string, part: string, field: string, checked: boolean): void {
    this.updateLock(agentId, part, choice => ({
      mode: 'fields',
      fields: checked ? [...choice.fields.filter(item => item !== field), field] : choice.fields.filter(item => item !== field)
    }));
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value;
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  lockModeOf(event: Event): PackageLockMode {
    return this.value(event) as PackageLockMode;
  }

  /** The sentence of a reference to an agent outside the selection. */
  referenceText(warning: AgentPackageWarning): string {
    this.language();
    const part = packagePartOf(warning.part);
    return this.translate.instant(`nh-assistant.admin.packages.export-dialog.reference.${warning.code}`, {
      agent: this.agentName(warning.agentId),
      referenced: this.agentName(warning.referencedAgentId),
      part: part ? this.partLabel(part) : warning.part
    }) as string;
  }

  agentName(agentId: string): string {
    const agent = this.agents().find(item => item.id === agentId);
    if (!agent) {
      return agentId;
    }
    const keys = codeTextKeys(agent.source, agent.displayName);
    const translated = keys.length > 0 ? this.translate.instant(keys[0]) as string : null;
    return translated && translated !== keys[0] ? translated : agent.displayName;
  }

  /** Exports the chosen agents with the chosen locks and saves the package as a JSON file. */
  download(): void {
    this.submitted.set(true);
    if (!this.canDownload()) {
      return;
    }

    const version = this.version().trim();
    const description = this.description().trim();
    const options: AgentPackageExportOptions = {
      packageId: this.packageId().trim() || null,
      version: version.length > 0 ? Number(version) : null,
      description: description.length > 0 ? description : null,
      locks: this.chosenLocks()
    };
    this.downloading.set(true);
    this.error.set(null);
    this.downloaded.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.exportAgentPackage(this.selected(), options).subscribe({
      next: exported => {
        this.downloading.set(false);
        const file = `agent-package-${exported.package.package.id}.json`;
        this.save(exported.json, file);
        this.downloaded.set({ file, warnings: exported.warnings });
        afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-export-done]')?.focus(), { injector: this.injector });
      },
      error: (failure: unknown) => {
        this.downloading.set(false);
        this.error.set(toNhAssistantError(failure));
      }
    });
  }

  close(): void {
    const dialog = this.dialog().nativeElement;
    if (dialog.open) {
      dialog.close();
    } else {
      this.onClosed();
    }
  }

  /** The native dialog closed, by a button or by Escape. */
  onClosed(): void {
    this.subscription?.unsubscribe();
    this.closed.emit();
  }

  /** The locks as the export sends them: per agent and part, `*` or the chosen fields. */
  private chosenLocks(): Record<string, Record<string, string[]>> | null {
    const locks: Record<string, Record<string, string[]>> = {};
    for (const agent of this.chosenAgents()) {
      for (const { part, choice } of agent.parts) {
        const fields = choice.mode === 'whole' ? [NH_ASSISTANT_PACKAGE_LOCK_ALL] : choice.mode === 'fields' ? [...choice.fields] : [];
        if (fields.length > 0) {
          locks[agent.agent.id] = { ...(locks[agent.agent.id] ?? {}), [part.key]: fields };
        }
      }
    }
    return Object.keys(locks).length > 0 ? locks : null;
  }

  private updateLock(agentId: string, part: string, change: (choice: LockChoice) => LockChoice): void {
    this.locks.update(locks => {
      const current = locks[agentId]?.[part] ?? { mode: 'none' as const, fields: [] };
      return { ...locks, [agentId]: { ...(locks[agentId] ?? {}), [part]: change(current) } };
    });
    this.downloaded.set(null);
  }

  /** Reads the parts of the chosen agents that were not read yet. */
  private readParts(): void {
    for (const agentId of this.selected()) {
      if (this.parts().has(agentId) || this.reads.has(agentId)) {
        continue;
      }
      const agent = this.agents().find(item => item.id === agentId);
      if (!agent) {
        continue;
      }
      this.reads.set(agentId, this.readAgent(agent).subscribe({
        next: read => {
          this.reads.delete(agentId);
          this.parts.update(parts => new Map(parts).set(agentId, read));
        },
        error: () => {
          this.reads.delete(agentId);
          this.failed.update(failed => new Set(failed).add(agentId));
        }
      }));
    }
  }

  /**
   * The parts of an agent that travel as data: an agent created here whole, an agent in code with its
   * overrides, its data skills, triggers, consultations and hand-offs and its own metadata values.
   */
  private readAgent(agent: AdminAgent): Observable<AgentParts> {
    const optional = <T>(request: Observable<T>): Observable<T | null> => request.pipe(catchError(() => of(null)));
    return forkJoin([
      optional(this.admin.getAgentConfiguration(agent.id)),
      optional(this.admin.getAgentHandOffs(agent.id)),
      this.admin.getAgentMetadata(agent.id)
    ]).pipe(map(([configuration, handOffs, metadata]) => partsOf(agent, configuration, handOffs, metadata)));
  }

  private save(json: string, file: string): void {
    const url = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
    const link = this.document.createElement('a');
    link.href = url;
    link.download = file;
    link.click();
    URL.revokeObjectURL(url);
  }

  private serverFieldError(field: string): string | null {
    const code = this.error()?.fieldErrors?.[field]?.[0];
    return code ? `nh-assistant.admin.run-config.field-errors.${code}` : null;
  }

  private partLabel(part: PackagePart): string {
    return this.translate.instant(packagePartLabelKey(part), { name: part.name }) as string;
  }

  private fieldLabel(field: string): string {
    const key = packageFieldLabelKey(field);
    const text = this.translate.instant(key) as string;
    return text && text !== key ? text : field;
  }
}

/** The parts of an agent and its references to other agents, from the administration's reads. */
function partsOf(
  agent: AdminAgent,
  configuration: AgentRunConfiguration | null,
  handOffs: AgentHandOffs | null,
  metadata: readonly AgentMetadata[]
): AgentParts {
  const part = (kind: AgentPackagePartKind, name: string | null = null): PackagePart => ({ key: name === null ? kind : `${kind}:${name}`, kind, name });
  const parts: PackagePart[] = [];
  if (agent.source === 'admin' || agent.isOverridden) {
    parts.push(part('agent'));
  }
  if (agent.source === 'admin' && configuration?.runSettings) {
    parts.push(part('runSettings'));
  }
  parts.push(...(configuration?.dataSkills ?? []).map(skill => part('skill', skill.skillId)));
  parts.push(...(configuration?.dataTriggers ?? []).map(trigger => part('trigger', trigger.triggerId)));
  parts.push(...(configuration?.dataConsultations ?? []).map(consultation => part('consultation', consultation.subjectType)));
  parts.push(...(handOffs?.dataHandOffs ?? []).map(handOff => part('handOff', handOff.subjectType)));
  const own = agent.source === 'code' ? 'override' : 'data';
  parts.push(...metadata.filter(entry => entry.source === own).map(entry => part('metadata', entry.name)));

  const references: AgentPackageWarning[] = [
    ...(handOffs?.dataHandOffs ?? []).flatMap(handOff => handOff.sources.map(source => ({
      agentId: agent.id, part: `handOff:${handOff.subjectType}`, code: 'hand-off-source', referencedAgentId: source
    }))),
    ...(configuration?.dataConsultations ?? []).flatMap(consultation => consultation.askingAgents.map(asker => ({
      agentId: agent.id, part: `consultation:${consultation.subjectType}`, code: 'consultation-asker', referencedAgentId: asker
    })))
  ];
  return { agentId: agent.id, parts, references };
}
