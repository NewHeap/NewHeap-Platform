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
  output,
  signal,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  NH_ASSISTANT_RULE_SCRIPT_MAX_LENGTH,
  NH_ASSISTANT_RULE_SCRIPT_MAX_REASON,
  NhAssistantAdminApiService,
  NhAssistantError,
  RuleScript,
  RuleScriptCatalog,
  RuleScriptCheck,
  RuleScriptTool,
  RuleScriptVersion,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import {
  RuleScriptEdit,
  RuleScriptPosition,
  RuleScriptScope,
  ruleScriptAlertSnippet,
  ruleScriptCodeKeys,
  ruleScriptIndentEdit,
  ruleScriptInsertEdit,
  ruleScriptOffsetOf,
  ruleScriptPositionOf,
  ruleScriptToolSnippet,
  ruleScriptVersionOf
} from './rule-scripts';

/** How a new version is saved: as the candidate, as the candidate in shadow, or straight live (RB-3). */
export type RuleScriptSaveMode = 'candidate' | 'shadow' | 'live';

let nextId = 0;

/**
 * Writes the next version of a rule script (internal). A plain text area in a monospaced font, where Tab
 * indents and Escape lets the next Tab leave the field, with the line and column of the caret and of a
 * problem the check finds. Next to it what the script sees, from the catalog for the writes chosen now:
 * the tools with their signatures, which are writes and which it may not call and why, the alert kinds,
 * the platform functions and the limits, each with a helper that inserts its call. Below it the writes
 * the version may make, only within the code's ceiling, and how it is saved: as the candidate, as the
 * candidate in shadow, or straight live, which supersedes the live version and takes the place of the
 * code rule. The save expects the rule script's revision; a conflict asks to reload, which keeps the text.
 */
@Component({
  selector: 'nh-assistant-rule-script-editor',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-rule-script-editor.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', '../page/agent-page.scss', './rule-scripts.scss']
})
export class NhAssistantRuleScriptEditorComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly injector = inject(Injector);
  private readonly textarea = viewChild<ElementRef<HTMLTextAreaElement>>('scriptField');
  private catalogSubscription?: Subscription;
  private checkSubscription?: Subscription;
  private saveSubscription?: Subscription;

  readonly agentId = input.required<string>();
  readonly skillId = input.required<RuleScriptScope>();
  /** The rule script as last read: its revision, ceiling, live version and code rule. */
  readonly ruleScript = input.required<RuleScript>();
  /** The version the new one starts from; `null` starts empty. */
  readonly base = input<RuleScriptVersion | null>(null);

  readonly saved = output<RuleScriptVersion>();
  readonly cancelled = output<void>();
  /** The rule script changed since it was read: the host reads it again, the text stays. */
  readonly reloadRequested = output<void>();

  readonly id = `nh-assistant-rule-script-editor-${nextId++}`;
  readonly maxLength = NH_ASSISTANT_RULE_SCRIPT_MAX_LENGTH;
  readonly maxReason = NH_ASSISTANT_RULE_SCRIPT_MAX_REASON;
  readonly codeKeys = ruleScriptCodeKeys;
  readonly script = signal('');
  readonly writes = signal<ReadonlySet<string>>(new Set());
  readonly reason = signal('');
  readonly mode = signal<RuleScriptSaveMode>('candidate');
  readonly caret = signal<RuleScriptPosition>({ line: 1, column: 1 });
  /** Escape was pressed: the next Tab leaves the field instead of indenting. */
  readonly tabLeaves = signal(false);
  readonly catalog = signal<RuleScriptCatalog | null>(null);
  readonly catalogFailed = signal(false);
  readonly checking = signal(false);
  readonly check = signal<RuleScriptCheck | null>(null);
  /** The check reflects this text; editing makes it stale. */
  readonly checkedScript = signal<string | null>(null);
  readonly saving = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

  readonly ceiling = computed(() => this.ruleScript().ceiling);
  readonly fourEyes = computed(() => this.ceiling().requireFourEyes);
  readonly live = computed(() => ruleScriptVersionOf(this.ruleScript().versions, this.ruleScript().liveVersion));
  readonly candidate = computed(() => ruleScriptVersionOf(this.ruleScript().versions, this.ruleScript().candidateVersion));
  readonly length = computed(() => this.script().length);
  readonly tooLong = computed(() => this.length() > this.maxLength);
  readonly empty = computed(() => this.script().trim().length === 0);
  readonly checkCurrent = computed(() => this.checkedScript() === this.script() ? this.check() : null);
  /** The writes the ceiling allows, with the catalog's tool where it names one. */
  readonly writeChoices = computed(() => {
    const ceiling = this.ceiling();
    const tools = this.catalog()?.tools ?? [];
    return ceiling.writes.map(toolId => {
      const tool = tools.find(item => item.toolId === toolId) ?? null;
      const destructive = tool?.destructive === true || ceiling.destructive.includes(toolId);
      return {
        toolId,
        tool,
        destructive,
        // A destructive write the ceiling does not let run in a script would never run.
        blocked: tool?.destructive === true && !ceiling.destructive.includes(toolId)
      };
    });
  });
  readonly readTools = computed(() => (this.catalog()?.tools ?? []).filter(tool => !tool.write));
  readonly writeTools = computed(() => (this.catalog()?.tools ?? []).filter(tool => tool.write));
  readonly scriptErrors = computed(() => this.error()?.fieldErrors?.['script'] ?? []);
  readonly writeErrors = computed(() => this.error()?.fieldErrors?.['writes'] ?? []);
  readonly reasonErrors = computed(() => this.error()?.fieldErrors?.['reason'] ?? []);
  readonly conflict = computed(() => this.error()?.code === 'assistant-version-conflict');

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.catalogSubscription?.unsubscribe();
      this.checkSubscription?.unsubscribe();
      this.saveSubscription?.unsubscribe();
    });
  }

  ngOnInit(): void {
    const base = this.base();
    this.script.set(base?.script ?? '');
    // A new version starts with the writes of the version it builds on that the ceiling still allows.
    const allowed = new Set(this.ceiling().writes);
    this.writes.set(new Set((base?.writes ?? []).filter(write => allowed.has(write))));
    this.loadCatalog();
    // The script opens at its first line, with the caret there, not scrolled to its end.
    afterNextRender(() => {
      const element = this.textarea()?.nativeElement;
      if (!element) {
        return;
      }

      element.setSelectionRange(0, 0);
      element.scrollTop = 0;
      element.scrollLeft = 0;
      element.focus({ preventScroll: true });
      this.track(element);
    }, { injector: this.injector });
  }

  /** Reads what a version with the chosen writes sees. */
  loadCatalog(): void {
    this.catalogFailed.set(false);
    this.catalogSubscription?.unsubscribe();
    this.catalogSubscription = this.admin.getRuleScriptCatalog(this.agentId(), this.skillId(), [...this.writes()]).subscribe({
      next: catalog => this.catalog.set(catalog),
      error: () => this.catalogFailed.set(true)
    });
  }

  onInput(event: Event): void {
    const element = event.target as HTMLTextAreaElement;
    this.script.set(element.value);
    this.track(element);
  }

  /** Tab indents and Shift+Tab outdents; after Escape the next Tab leaves the field, as people expect. */
  onKeydown(event: KeyboardEvent): void {
    const element = event.target as HTMLTextAreaElement;
    if (event.key === 'Escape') {
      this.tabLeaves.set(true);
      return;
    }
    if (event.key !== 'Tab' || event.ctrlKey || event.altKey || event.metaKey) {
      this.tabLeaves.set(false);
      return;
    }
    if (this.tabLeaves()) {
      this.tabLeaves.set(false);
      return;
    }

    event.preventDefault();
    this.apply(element, ruleScriptIndentEdit(element.value, element.selectionStart, element.selectionEnd, event.shiftKey));
  }

  /** Follows the caret, for its line and column. */
  track(element: HTMLTextAreaElement): void {
    this.caret.set(ruleScriptPositionOf(element.value, element.selectionStart));
  }

  insertTool(tool: RuleScriptTool): void {
    this.insert(ruleScriptToolSnippet(tool));
  }

  insertAlert(kind: string): void {
    this.insert(ruleScriptAlertSnippet(kind));
  }

  /** Puts the caret where the check or a run found the problem. */
  goTo(position: RuleScriptPosition): void {
    const element = this.textarea()?.nativeElement;
    if (!element) {
      return;
    }

    const offset = ruleScriptOffsetOf(element.value, position);
    element.focus();
    element.setSelectionRange(offset, offset);
    this.track(element);
  }

  toggleWrite(toolId: string, checked: boolean): void {
    const next = new Set(this.writes());
    if (checked) {
      next.add(toolId);
    } else {
      next.delete(toolId);
    }
    this.writes.set(next);
    this.loadCatalog();
  }

  runCheck(): void {
    const script = this.script();
    this.checking.set(true);
    this.checkSubscription?.unsubscribe();
    this.checkSubscription = this.admin.checkRuleScript(this.agentId(), this.skillId(), script).subscribe({
      next: check => {
        this.checking.set(false);
        this.check.set(check);
        this.checkedScript.set(script);
      },
      error: (error: unknown) => {
        this.checking.set(false);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  save(): void {
    if (this.empty() || this.tooLong() || this.saving() || (this.mode() === 'live' && this.fourEyes())) {
      return;
    }

    const reason = this.reason().trim();
    const mode = this.mode();
    this.saving.set(true);
    this.error.set(null);
    this.saveSubscription?.unsubscribe();
    this.saveSubscription = this.admin
      .saveRuleScriptVersion(this.agentId(), this.skillId(), {
        script: this.script(),
        writes: [...this.writes()].sort(),
        expectedRevision: this.ruleScript().revision,
        reason: reason.length > 0 ? reason : null,
        live: mode === 'live',
        shadow: mode === 'shadow'
      })
      .subscribe({
        next: version => {
          this.saving.set(false);
          this.saved.emit(version);
        },
        error: (error: unknown) => {
          this.saving.set(false);
          this.error.set(toNhAssistantError(error));
        }
      });
  }

  reload(): void {
    this.error.set(null);
    this.reloadRequested.emit();
  }

  chooseMode(mode: RuleScriptSaveMode): void {
    this.mode.set(mode);
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  private insert(snippet: string): void {
    const element = this.textarea()?.nativeElement;
    if (!element) {
      return;
    }

    element.focus();
    this.apply(element, ruleScriptInsertEdit(element.value, element.selectionStart, element.selectionEnd, snippet));
  }

  private apply(element: HTMLTextAreaElement, edit: RuleScriptEdit): void {
    element.value = edit.text;
    element.setSelectionRange(edit.start, edit.end);
    this.script.set(edit.text);
    this.track(element);
  }
}
