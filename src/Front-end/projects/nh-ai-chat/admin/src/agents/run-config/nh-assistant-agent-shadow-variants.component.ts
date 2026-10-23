import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AdminAgent,
  AdminAgentInput,
  AgentShadowVariants,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { RunConfigForm } from './run-config-form';
import { RunConfigErrors, dashCase, requireText, runConfigLimits } from './run-config-shared';

let nextId = 0;

/** The longest texts of an agent's definition the server accepts. */
const maxId = 128;
const maxName = 256;
const maxDescription = 512;

/** Why administrators cannot create a data variant now. */
export type ShadowVariantsClosed = 'not-allowed' | 'limit';

/**
 * The shadow variants of an agent (S9): those in code read only and those created as data, each with
 * whether it has run settings, receives the agent's events now and, when it no longer fits, why not.
 * Administrators create a data variant within the ceiling in code (`UseShadowVariantsAsData`): it
 * starts from this agent's definition, gets its own id, name, description and instructions, is never
 * offered in the chat and always works in shadow. `created` hands the new variant to the page, which
 * continues in its run configuration; `openVariant` opens the run configuration of a data variant.
 */
@Component({
  selector: 'nh-assistant-agent-shadow-variants',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-shadow-variants.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantAgentShadowVariantsComponent extends RunConfigForm {
  private readonly admin = inject(NhAssistantAdminApiService);
  private loadSubscription?: Subscription;

  /** The live agent. */
  readonly agent = input.required<AdminAgent>();
  readonly created = output<AdminAgent>();
  readonly openVariant = output<string>();

  readonly id = `nh-assistant-shadow-variants-${nextId++}`;
  readonly maxId = maxId;
  readonly maxName = maxName;
  readonly maxDescription = maxDescription;
  readonly maxInstructions = runConfigLimits.instructions;
  protected readonly fields = ['id', 'displayName', 'description', 'instructions'];

  readonly loading = signal(true);
  readonly loadError = signal<NhAssistantError | null>(null);
  readonly variants = signal<AgentShadowVariants | null>(null);
  readonly creating = signal(false);
  readonly variantId = signal('');
  readonly displayName = signal('');
  readonly description = signal('');
  readonly instructions = signal('');
  readonly loadErrorKeys = computed(() => nhAssistantErrorKeys(this.loadError()));
  readonly dataVariants = computed(() => (this.variants()?.variants ?? []).filter(variant => variant.source === 'data').length);
  readonly closed = computed<ShadowVariantsClosed | null>(() => {
    const variants = this.variants();
    if (!variants?.allowsDataVariants) {
      return 'not-allowed';
    }
    return this.dataVariants() >= variants.maxDataVariants ? 'limit' : null;
  });

  constructor() {
    super();
    inject(DestroyRef).onDestroy(() => this.loadSubscription?.unsubscribe());
    // The page shows another agent's run configuration in the same place: start over for it.
    effect(() => {
      const agentId = this.agent().id;
      untracked(() => {
        this.creating.set(false);
        this.load(agentId);
      });
    });
  }

  load(agentId = this.agent().id): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = this.admin.getShadowVariants(agentId).subscribe({
      next: variants => {
        this.variants.set(variants);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.loadError.set(toNhAssistantError(error));
        this.loading.set(false);
      }
    });
  }

  /** Opens the form, filled in from the live agent: a variant tries what this agent does differently. */
  startCreate(): void {
    const live = this.agent();
    const name = this.textOf(live.source, live.displayName);
    this.variantId.set(`${live.id}-variant`.slice(0, maxId));
    this.displayName.set(this.t('nh-assistant.admin.shadow-variants.form.default-name', { name }));
    this.description.set(this.textOf(live.source, live.description));
    this.instructions.set(live.instructions);
    this.clientErrors.set({});
    this.serverError.set(null);
    this.creating.set(true);
  }

  cancelCreate(): void {
    this.creating.set(false);
  }

  create(): void {
    const errors: RunConfigErrors = {};
    const variantId = this.variantId().trim();
    if (variantId.length === 0) {
      errors['id'] = { code: 'required' };
    } else if (variantId.length > maxId || !dashCase.test(variantId)) {
      errors['id'] = { code: 'dash-case' };
    }
    requireText(errors, 'displayName', this.displayName(), maxName);
    requireText(errors, 'description', this.description(), maxDescription);
    requireText(errors, 'instructions', this.instructions(), this.maxInstructions);

    const live = this.agent();
    const input: AdminAgentInput = {
      id: variantId,
      displayName: this.displayName().trim(),
      description: this.description().trim(),
      instructions: this.instructions(),
      toolSelectors: [...live.toolSelectors],
      mcpServerIds: [...live.mcpServerIds],
      requiredPolicy: live.requiredPolicy,
      autonomy: live.autonomy,
      offeredInChat: false,
      // The same objects, so the variant takes the same events; the policies of an agent in code
      // need not be listed for the administration.
      worksOnSubjectTypes: live.worksOnSubjectTypes,
      worksOnAllSubjects: live.worksOnAllSubjects,
      worksOnApplication: live.worksOnApplication,
      perDivision: live.perDivision,
      runPolicy: live.source === 'admin' ? live.runPolicy : undefined,
      viewPolicy: live.source === 'admin' ? live.viewPolicy : undefined,
      profile: live.source === 'admin' ? live.profile : undefined,
      dataClassification: live.dataClassification,
      shadowOf: live.id
    };
    this.submit(
      errors,
      () => this.admin.createAgent(input),
      agent => {
        this.creating.set(false);
        this.created.emit(agent);
        this.load();
      });
  }

  /** The text of an agent's name or description: a key of the application for an agent in code. */
  private textOf(source: string, text: string): string {
    if (source !== 'code') {
      return text;
    }
    const translated = this.t(text);
    return translated && translated !== text ? translated : text;
  }
}
