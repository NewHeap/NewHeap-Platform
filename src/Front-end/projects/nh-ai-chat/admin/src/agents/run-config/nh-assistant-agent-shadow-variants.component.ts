import { ChangeDetectionStrategy, Component, DestroyRef, afterNextRender, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AdminAgent,
  AdminAgentInput,
  AgentShadowVariants,
  NhAssistantAdminApiService,
  NhAssistantError,
  ShadowVariant,
  ShadowVariantPromotion,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { agentDisplayName } from '../agent-texts';
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
 * After a confirmation, a data variant is promoted onto this agent (LS31): its definition becomes this
 * agent's next version, its skills move along when asked, and the variant is removed; `promoted` hands
 * the outcome to the page, which shows the agent's new version. A variant linked to an agent package
 * (FB11) also offers "As the package's next version": all its parts replace this agent's as an import
 * does and this agent is linked to that package version.
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
  private promoteSubscription?: Subscription;
  /** The agent the last promotion note belongs to. */
  private shownFor: string | null = null;

  /** The live agent. */
  readonly agent = input.required<AdminAgent>();
  readonly created = output<AdminAgent>();
  readonly openVariant = output<string>();
  readonly promoted = output<ShadowVariantPromotion>();

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

  /** The data variant whose promotion onto this agent is being confirmed; `null` while none is. */
  readonly promoting = signal<string | null>(null);
  readonly moveSkills = signal(false);
  /** The variant's package version becomes this agent's; its skills come along then. */
  readonly asPackageVersion = signal(false);
  readonly promoteBusy = signal(false);
  readonly promoteError = signal<NhAssistantError | null>(null);
  readonly promoteErrorKeys = computed(() => nhAssistantErrorKeys(this.promoteError()));
  /** The last promotion onto this agent, which a status announces. */
  readonly promotedNote = signal<{ variant: string; version: number } | null>(null);
  /** This agent's name as the agent list shows it, in the current language. */
  readonly liveName = computed(() => {
    this.language();
    return agentDisplayName(this.translate, this.agent());
  });

  constructor() {
    super();
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.promoteSubscription?.unsubscribe();
    });
    // The page shows another agent's run configuration in the same place: start over for it. After a
    // promotion the page shows this agent's new version, so the note of the promotion stays.
    effect(() => {
      const agentId = this.agent().id;
      untracked(() => {
        this.creating.set(false);
        this.promoting.set(null);
        this.promoteError.set(null);
        if (this.shownFor !== agentId) {
          this.promotedNote.set(null);
          this.shownFor = agentId;
        }
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
    const name = agentDisplayName(this.translate, live);
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

  /** Asks to confirm the promotion of a data variant onto this agent; its skills stay unless asked. */
  startPromote(variant: ShadowVariant): void {
    this.promoting.set(variant.agentId);
    this.moveSkills.set(false);
    this.asPackageVersion.set(false);
    this.promoteError.set(null);
    this.promotedNote.set(null);
    afterNextRender(() => {
      this.host.nativeElement.querySelector<HTMLButtonElement>('[data-promote-confirm-button]')?.focus();
    }, { injector: this.injector });
  }

  cancelPromote(): void {
    const variantId = this.promoting();
    this.promoting.set(null);
    this.promoteError.set(null);
    afterNextRender(() => {
      Array.from(this.host.nativeElement.querySelectorAll<HTMLButtonElement>('[data-promote-variant]'))
        .find(button => button.dataset['promoteVariant'] === variantId)
        ?.focus();
    }, { injector: this.injector });
  }

  /**
   * Promotes the variant onto this agent at the versions shown: this agent's, and the variant's when
   * the list reports it, so a change by someone else in the meantime is refused.
   */
  promote(variant: ShadowVariant): void {
    const name = this.variantName(variant);
    this.promoteBusy.set(true);
    this.promoteError.set(null);
    this.promoteSubscription?.unsubscribe();
    // As the package's version the skills are parts of it: they move along without being asked.
    const asPackageVersion = this.asPackageVersion() && !!variant.packageId;
    this.promoteSubscription = this.admin.promoteShadowVariant(variant.agentId, {
      expectedVersion: this.agent().version,
      moveSkills: asPackageVersion ? false : this.moveSkills(),
      expectedVariantVersion: variant.version ?? null,
      ...(asPackageVersion ? { asPackageVersion: true } : {})
    }).subscribe({
      next: promotion => {
        this.promoteBusy.set(false);
        this.promoting.set(null);
        this.promotedNote.set({ variant: name, version: promotion.agent.version });
        this.promoted.emit(promotion);
        this.load();
      },
      error: (error: unknown) => {
        this.promoteBusy.set(false);
        this.promoteError.set(toNhAssistantError(error));
      }
    });
  }

  /**
   * A variant's name as the agent list shows it: a variant in code is an agent in code, which names
   * itself with a key of the application.
   */
  variantName(variant: ShadowVariant): string {
    return agentDisplayName(this.translate, { source: variant.source === 'code' ? 'code' : 'admin', displayName: variant.displayName });
  }

  /** The text of an agent's description: a key of the application for an agent in code. */
  private textOf(source: string, text: string): string {
    if (source !== 'code') {
      return text;
    }
    const translated = this.t(text);
    return translated && translated !== text ? translated : text;
  }
}
