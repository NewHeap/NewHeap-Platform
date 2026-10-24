import { NgTemplateOutlet } from '@angular/common';
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
import { Subscription, map, startWith } from 'rxjs';
import {
  AdminAgent,
  AgentPackage,
  AgentPackageImport,
  AgentPackageImportOptions,
  AgentPackagePart,
  AgentPackagePreview,
  NH_ASSISTANT_AGENT_PACKAGE_MAX_BYTES,
  NH_ASSISTANT_PACKAGE_LOCK_ALL,
  NhAssistantAdminApiService,
  NhAssistantError,
  PackagePartReference,
  SkillApplyMode,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { codeTextKeys } from '../agent-texts';
import { NhAssistantAccountPickerComponent } from '../run-config/nh-assistant-account-picker.component';
import { packageFieldLabelKey, packagePartLabelKey, packagePartOf } from './package-parts';

/** One part in the table of a check or an import. */
interface ImportRow {
  key: string;
  label: string;
  outcome: AgentPackagePart['outcome'];
  changedLocally: boolean;
  code: string | null;
  /** The translation keys of the refusal's code, the import's own text first, with its parameters. */
  codeKeys: string[];
  codeParams: Record<string, unknown> | null;
  fields: { field: string; codes: string[] }[];
  /** What the package locks on the part, in the reader's language; `null` without locks. */
  locked: string | null;
  /**
   * The import may take over the host's agent with the package's id (`relink` naming the whole
   * agent): it is created here and belongs to no package, or the import takes it over already.
   */
  takeOver: boolean;
}

/** The parts of one agent, with what the import still needs of it. */
interface ImportAgentView {
  agentId: string;
  /** Its run settings answer to a technical account, which the import names on this host. */
  needsAccount: boolean;
  /** The check refused the account, or found none. */
  accountRefused: boolean;
  /** A part stayed local, which the import can take the package's version of again. */
  relinkable: boolean;
  parts: ImportRow[];
}

/** A check or an import as the dialog shows it. */
interface ImportView {
  packageId: string;
  version: number;
  agents: ImportAgentView[];
  refused: number;
}

/** The refusal of an agent the host has with the package's id. */
const agentExists = 'assistant-agent-exists';

/** The codes of a refused technical account. */
const accountRefusals = new Set(['assistant-service-account-not-found']);

let nextId = 0;

/**
 * Imports an agent package (internal; the agents page opens it): a package file or pasted JSON, the
 * check of what the import would do per agent and part (`created`, `changed` with "changed locally",
 * `unchanged`, `kept-local`, `left-behind` or `refused` with its code and field errors), a technical
 * account per agent whose run settings answer to one, whether changed skills and triggers take effect
 * at once or run in shadow first, and per kept-local part whether to take the package's version again.
 * The import applies everything or nothing; when its own check refuses a part after all, the dialog
 * checks again to show which. A modal dialog that opens itself and emits `closed`; Escape closes it.
 */
@Component({
  selector: 'nh-assistant-agent-package-import-dialog',
  standalone: true,
  imports: [NgTemplateOutlet, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantAccountPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-package-import-dialog.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './package-dialog.scss']
})
export class NhAssistantAgentPackageImportDialogComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private subscription?: Subscription;
  /** The dialog closed and said so. */
  private ended = false;

  /** The host's agents, to name them. */
  readonly agents = input<readonly AdminAgent[]>([]);
  /** Whether the application offers an account search for the technical accounts. */
  readonly accountSearch = input(false);

  /** The package was imported; the host reads its agents again. */
  readonly imported = output<AgentPackageImport>();
  /** The dialog closed; the host removes it. */
  readonly closed = output<void>();

  readonly id = `nh-assistant-package-import-${nextId++}`;
  readonly applyModes: readonly SkillApplyMode[] = ['live', 'shadow'];
  readonly text = signal('');
  readonly fileName = signal<string | null>(null);
  /** The chosen file is larger than a package may be, so it was not read. */
  readonly fileTooLarge = signal(false);
  /** The chosen file could not be read as text. */
  readonly fileUnreadable = signal(false);
  readonly apply = signal<SkillApplyMode>('live');
  readonly accounts = signal<Readonly<Record<string, string>>>({});
  readonly relink = signal<readonly PackagePartReference[]>([]);
  readonly previewing = signal(false);
  readonly importing = signal(false);
  readonly busy = computed(() => this.previewing() || this.importing());
  /** The source was submitted without a package. */
  readonly submitted = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  private readonly previewed = signal<{ preview: AgentPackagePreview; key: string } | null>(null);
  readonly result = signal<ImportView | null>(null);

  /** The language, so the tables follow a switch. */
  private readonly language = toSignal(
    this.translate.onLangChange.pipe(map(event => event.lang), startWith(this.translate.currentLang)),
    { initialValue: this.translate.currentLang });

  /** The package the source holds, or why it holds none. */
  readonly source = computed<{ package: AgentPackage | null; error: string | null }>(() => {
    if (this.fileTooLarge()) {
      return { package: null, error: 'too-large' };
    }
    if (this.fileUnreadable()) {
      return { package: null, error: 'invalid-json' };
    }
    const text = this.text().trim();
    if (text.length === 0) {
      return { package: null, error: null };
    }
    if (new TextEncoder().encode(text).length > NH_ASSISTANT_AGENT_PACKAGE_MAX_BYTES) {
      return { package: null, error: 'too-large' };
    }
    try {
      const value = JSON.parse(text) as unknown;
      return value && typeof value === 'object' && !Array.isArray(value)
        ? { package: value as AgentPackage, error: null }
        : { package: null, error: 'invalid-json' };
    } catch {
      return { package: null, error: 'invalid-json' };
    }
  });

  /** The translation key of the source's problem, once it matters. */
  readonly sourceError = computed(() => {
    const source = this.source();
    if (source.error) {
      return `nh-assistant.admin.packages.import-dialog.${source.error}`;
    }
    return this.submitted() && !source.package ? 'nh-assistant.admin.packages.import-dialog.no-package' : null;
  });

  /** The options as the import sends them. */
  /**
   * The options as the import sends them, for the agents of the package as it is now: an account or a
   * relink of an agent a changed package no longer holds is left out, as the server refuses it.
   */
  readonly options = computed<AgentPackageImportOptions>(() => {
    const agentIds = new Set((this.source().package?.agents ?? []).map(agent => agent?.id));
    const accounts = Object.fromEntries(Object.entries(this.accounts())
      .map(([agentId, actorId]) => [agentId, actorId.trim()] as const)
      .filter(([agentId, actorId]) => actorId.length > 0 && agentIds.has(agentId)));
    const relink = this.relink().filter(item => agentIds.has(item.agentId));
    return {
      serviceAccounts: Object.keys(accounts).length > 0 ? accounts : null,
      apply: this.apply(),
      relink: relink.length > 0 ? relink : null
    };
  });

  /** What the check and the import depend on: the package text and the options. */
  private readonly checkKey = computed(() => JSON.stringify([this.text().trim(), this.options()]));

  /** The latest check, as the dialog shows it. */
  readonly checked = computed<ImportView | null>(() => {
    const previewed = this.previewed();
    this.language();
    return previewed ? this.viewOf(previewed.preview.packageId, previewed.preview.packageVersion, previewed.preview.parts, true) : null;
  });

  /** The package text or the options changed since the check. */
  readonly stale = computed(() => {
    const previewed = this.previewed();
    return previewed !== null && previewed.key !== this.checkKey();
  });

  readonly canImport = computed(() => {
    const previewed = this.previewed();
    return previewed !== null && previewed.preview.importable && !this.stale() && !this.busy();
  });

  readonly previewLabelKey = computed(() => {
    if (this.previewing()) {
      return 'nh-assistant.admin.packages.import-dialog.previewing';
    }
    return this.previewed() ? 'nh-assistant.admin.packages.import-dialog.preview-again' : 'nh-assistant.admin.packages.import-dialog.preview';
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    afterNextRender(() => {
      const dialog = this.dialog().nativeElement;
      if (!dialog.open) {
        dialog.showModal();
      }
      this.host.nativeElement.querySelector<HTMLElement>('[data-package-file]')?.focus();
    }, { injector: this.injector });
  }

  onText(event: Event): void {
    this.text.set((event.target as HTMLTextAreaElement).value);
    this.fileName.set(null);
    this.fileTooLarge.set(false);
    this.fileUnreadable.set(false);
    this.error.set(null);
  }

  /** Reads the chosen file into the package text. */
  async onFile(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) {
      return;
    }

    this.error.set(null);
    this.fileName.set(file.name);
    this.fileUnreadable.set(false);
    this.fileTooLarge.set(file.size > NH_ASSISTANT_AGENT_PACKAGE_MAX_BYTES);
    if (this.fileTooLarge()) {
      this.text.set('');
      return;
    }
    try {
      this.text.set(await file.text());
    } catch {
      this.text.set('');
      this.fileUnreadable.set(true);
    }
  }

  accountOf(agentId: string): string {
    return this.accounts()[agentId] ?? '';
  }

  setAccount(agentId: string, actorId: string): void {
    this.accounts.update(accounts => ({ ...accounts, [agentId]: actorId }));
  }

  /** Whether the import takes the package's version of a part again; `part` `null` takes over the whole agent. */
  isRelinked(agentId: string, part: string | null): boolean {
    return this.relink().some(item => item.agentId === agentId && item.part === part);
  }

  toggleRelink(agentId: string, part: string | null, checked: boolean): void {
    this.relink.update(items => checked
      ? [...items.filter(item => item.agentId !== agentId || item.part !== part), { agentId, part }]
      : items.filter(item => item.agentId !== agentId || item.part !== part));
  }

  checkedOf(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  /** The name of an agent: the host's, the package's, or its id. */
  agentName(agentId: string): string {
    const agent = this.agents().find(item => item.id === agentId);
    if (agent) {
      const keys = codeTextKeys(agent.source, agent.displayName);
      const translated = keys.length > 0 ? this.translate.instant(keys[0]) as string : null;
      return translated && translated !== keys[0] ? translated : agent.displayName;
    }
    return this.source().package?.agents?.find(item => item?.id === agentId)?.definition?.displayName ?? agentId;
  }

  /** The translation keys of the codes of one field, the first one known. */
  fieldErrorKeys(codes: readonly string[]): string[] {
    return codes.map(code => `nh-assistant.admin.run-config.field-errors.${code}`);
  }

  /** Checks what the import would do, with the current options. */
  preview(): void {
    this.submitted.set(true);
    const agentPackage = this.source().package;
    if (!agentPackage || this.busy()) {
      return;
    }

    const key = this.checkKey();
    this.previewing.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.previewAgentPackageImport(agentPackage, this.options()).subscribe({
      next: preview => {
        this.previewing.set(false);
        this.previewed.set({ preview, key });
        this.focus('[data-import-preview] h3');
      },
      error: (failure: unknown) => {
        this.previewing.set(false);
        this.previewed.set(null);
        this.error.set(toNhAssistantError(failure));
      }
    });
  }

  /** Imports the package as it was checked; a refusal of its own check shows which parts again. */
  import(): void {
    const agentPackage = this.source().package;
    if (!agentPackage || !this.canImport()) {
      return;
    }

    this.importing.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.importAgentPackage(agentPackage, this.options()).subscribe({
      next: done => {
        this.importing.set(false);
        this.result.set(this.viewOf(done.packageId, done.packageVersion, done.parts, false));
        this.imported.emit(done);
        this.focus('[data-import-done]');
      },
      error: (failure: unknown) => {
        this.importing.set(false);
        const error = toNhAssistantError(failure);
        this.previewed.set(null);
        if (error.code === 'assistant-package-refused') {
          // The host changed since the check: check again, so the table shows which parts.
          this.preview();
        }
        this.error.set(error);
      }
    });
  }

  close(): void {
    const dialog = this.dialog().nativeElement;
    if (dialog.open) {
      dialog.close();
    }
    this.onClosed();
  }

  /** The dialog closed, by a button or by Escape; the host hears it once. */
  onClosed(): void {
    if (this.ended) {
      return;
    }

    this.ended = true;
    this.subscription?.unsubscribe();
    this.closed.emit();
  }

  private viewOf(packageId: string, version: number, parts: readonly AgentPackagePart[], choose: boolean): ImportView {
    const agents: ImportAgentView[] = [];
    for (const part of parts) {
      let agent = agents.find(item => item.agentId === part.agentId);
      if (!agent) {
        agent = { agentId: part.agentId, needsAccount: choose && this.needsAccount(part.agentId), accountRefused: false, relinkable: false, parts: [] };
        agents.push(agent);
      }
      if (part.part === 'runSettings' && part.outcome === 'refused'
        && (part.errors?.['serviceAccountActorId'] !== undefined || accountRefusals.has(part.code ?? ''))) {
        agent.accountRefused = true;
      }
      const row = this.rowOf(packageId, part);
      if (part.outcome === 'kept-local' || row.takeOver) {
        agent.relinkable = true;
      }
      agent.parts.push(row);
    }
    return { packageId, version, agents, refused: parts.filter(part => part.outcome === 'refused').length };
  }

  private rowOf(packageId: string, part: AgentPackagePart): ImportRow {
    const refusal = part.part === 'agent' ? this.agentRefusal(packageId, part) : null;
    return {
      key: part.part,
      label: this.partLabel(part.part),
      outcome: part.outcome,
      changedLocally: part.changedLocally,
      code: part.code,
      codeKeys: [...(refusal?.keys ?? []), ...nhAssistantCodeKeys(part.code)],
      codeParams: refusal?.params ?? null,
      fields: Object.entries(part.errors ?? {}).map(([field, codes]) => ({ field, codes: [...codes] })),
      locked: this.lockedText(part.lockedFields),
      takeOver: part.part === 'agent' && (this.isRelinked(part.agentId, null) || refusal?.takeOver === true)
    };
  }

  /**
   * What the refusal of an agent the host has with the package's id means here: an agent created here
   * that belongs to no package can be taken over (PL7) or removed first; one of another package must
   * be unlinked from it first; one in code cannot be replaced. The import never chooses another id.
   */
  private agentRefusal(packageId: string, part: AgentPackagePart): { keys: string[]; params: Record<string, unknown>; takeOver: boolean } | null {
    if (part.outcome !== 'refused' || part.code !== agentExists) {
      return null;
    }

    const host = this.agents().find(agent => agent.id === part.agentId);
    const prefix = 'nh-assistant.admin.packages.import-dialog.agent-exists';
    if (host?.source === 'code') {
      return { keys: [`${prefix}-in-code`], params: {}, takeOver: false };
    }
    if (host?.package && host.package.packageId !== packageId) {
      return { keys: [`${prefix}-other-package`], params: { package: host.package.packageId }, takeOver: false };
    }
    return { keys: [prefix], params: {}, takeOver: true };
  }

  private partLabel(key: string): string {
    const part = packagePartOf(key);
    return part ? this.translate.instant(packagePartLabelKey(part), { name: part.name }) as string : key;
  }

  private lockedText(fields: readonly string[]): string | null {
    if (fields.length === 0) {
      return null;
    }
    if (fields.includes(NH_ASSISTANT_PACKAGE_LOCK_ALL)) {
      return this.translate.instant('nh-assistant.admin.packages.lock.whole') as string;
    }
    const names = fields.map(field => {
      const key = packageFieldLabelKey(field);
      const text = this.translate.instant(key) as string;
      return text && text !== key ? text : field;
    });
    return this.translate.instant('nh-assistant.admin.packages.lock.fields', { fields: names.join(', ') }) as string;
  }

  /** Whether the package's run settings of an agent answer to a technical account. */
  private needsAccount(agentId: string): boolean {
    const agent = this.source().package?.agents?.find(item => item?.id === agentId);
    return agent?.source === 'admin' && agent.runSettings?.accountability === 'service-account';
  }

  private focus(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }
}
