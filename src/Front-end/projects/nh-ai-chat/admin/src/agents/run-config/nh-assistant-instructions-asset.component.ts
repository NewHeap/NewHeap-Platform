import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, model, output, signal } from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  InstructionAssetReference,
  InstructionAssetSummary,
  InstructionAssetText,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { RunConfigFieldError, dashCase } from './run-config-shared';

/** Where the instructions of a skill version or an agent created here come from. */
export type InstructionsSource = 'inline' | 'asset';

/** The first characters of a hash that the editor shows; the full hash is in its title. */
const shownHashLength = 12;

/**
 * Chooses whether the instructions of a skill version or of an agent created in the administration
 * are written here or come from one of the application's text assets, and which one. With assets the
 * application lists, the administrator picks one; otherwise types its id and version. The text of the
 * chosen asset is read from the application and shown read-only with its hash, which the reference
 * stores; a newer version of the same asset is pointed out, never chosen on its own. The problem of a
 * saved reference that no longer resolves and the save's error show at the choice. The host shows its
 * own text field while the source is `inline`.
 */
@Component({
  selector: 'nh-assistant-instructions-asset',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-instructions-asset.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss', './nh-assistant-instructions-asset.component.scss']
})
export class NhAssistantInstructionsAssetComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  private listSubscription?: Subscription;
  private readSubscription?: Subscription;

  /** The prefix of the controls' ids. */
  readonly idPrefix = input.required<string>();
  /** Whether the application offers its assets (the run catalog's `instructionAssets`). */
  readonly available = input(false);
  readonly source = model<InstructionsSource>('inline');
  /** The reference the version or agent will store; `null` while none is chosen and read. */
  readonly reference = model<InstructionAssetReference | null>(null);
  /** The problem of the saved reference, such as `asset-changed`; `null` when it resolves. */
  readonly problem = input<string | null>(null);
  /** The save's error of the reference: `required`, `not-found`, `changed`, `invalid` or `not-allowed`. */
  readonly error = input<RunConfigFieldError | null>(null);
  /** The administrator changed the choice, so the host clears its error. */
  readonly touched = output<void>();

  readonly assets = signal<InstructionAssetSummary[] | null>(null);
  readonly listError = signal<NhAssistantError | null>(null);
  readonly assetId = signal('');
  readonly version = signal('');
  readonly text = signal<InstructionAssetText | null>(null);
  readonly reading = signal(false);
  readonly readError = signal<NhAssistantError | null>(null);
  /** The typed id or version is not a valid reference. */
  readonly typedInvalid = signal(false);

  readonly listed = computed(() => (this.assets() ?? []).length > 0);
  readonly listErrorKeys = computed(() => nhAssistantErrorKeys(this.listError()));
  readonly readErrorKeys = computed(() => nhAssistantErrorKeys(this.readError()));
  readonly options = computed(() => {
    this.language();
    return (this.assets() ?? []).map(asset => ({
      value: valueOf(asset.assetId, asset.version),
      label: this.t('nh-assistant.admin.instruction-assets.option', { asset: asset.assetId, version: asset.version }),
      description: asset.deprecatedAt
        ? this.t(asset.replacedByAssetId ? 'nh-assistant.admin.instruction-assets.deprecated-by' : 'nh-assistant.admin.instruction-assets.deprecated', { asset: asset.replacedByAssetId })
        : null
    }));
  });
  readonly chosen = computed(() => {
    const version = Number(this.version());
    return this.assetId() && Number.isInteger(version) && version >= 1 ? valueOf(this.assetId(), version) : null;
  });
  /** A newer version of the chosen asset the application lists: saving it is an explicit change. */
  readonly newerVersion = computed(() => {
    const current = this.reference();
    if (!current) {
      return null;
    }
    const newer = (this.assets() ?? [])
      .filter(asset => asset.assetId === current.assetId && asset.version > current.version)
      .map(asset => asset.version);
    return newer.length > 0 ? Math.max(...newer) : null;
  });
  readonly shownHash = computed(() => this.reference()?.hash.slice(0, shownHashLength) ?? null);
  readonly errorKey = computed(() => {
    const error = this.error();
    return error ? `nh-assistant.admin.instruction-assets.errors.${error.code}` : null;
  });
  readonly problemKey = computed(() => {
    const problem = this.problem();
    return problem ? `nh-assistant.admin.instruction-assets.problems.${problem}` : null;
  });

  private readonly language = signal(this.translate.currentLang);

  constructor() {
    const languages = this.translate.onLangChange.subscribe(event => this.language.set(event.lang));
    inject(DestroyRef).onDestroy(() => {
      languages.unsubscribe();
      this.listSubscription?.unsubscribe();
      this.readSubscription?.unsubscribe();
    });
  }

  ngOnInit(): void {
    const reference = this.reference();
    if (reference) {
      this.assetId.set(reference.assetId);
      this.version.set(String(reference.version));
    }
    if (this.source() === 'asset') {
      this.load();
    }
  }

  /** Switches between text written here and an asset of the application. */
  choose(source: InstructionsSource): void {
    if (this.source() === source) {
      return;
    }
    this.source.set(source);
    this.touched.emit();
    if (source === 'asset' && this.assets() === null) {
      this.load();
    }
  }

  /** An asset picked from the application's list. */
  pick(value: string): void {
    const separator = value.lastIndexOf('@');
    if (separator <= 0) {
      return;
    }
    this.assetId.set(value.slice(0, separator));
    this.version.set(value.slice(separator + 1));
    this.read();
  }

  /** A typed id or version changes the choice, which is read again before it can be saved. */
  type(field: 'assetId' | 'version', value: string): void {
    (field === 'assetId' ? this.assetId : this.version).set(value.trim());
    this.typedInvalid.set(false);
    this.text.set(null);
    this.readError.set(null);
    if (this.reference() !== null) {
      this.reference.set(null);
    }
    this.touched.emit();
  }

  /** Reads the chosen asset's text from the application and makes it the reference. */
  read(): void {
    const assetId = this.assetId();
    const version = Number(this.version());
    if (!dashCase.test(assetId) || assetId.length > 128 || !Number.isInteger(version) || version < 1) {
      this.typedInvalid.set(true);
      return;
    }
    this.typedInvalid.set(false);
    this.reading.set(true);
    this.readError.set(null);
    this.readSubscription?.unsubscribe();
    this.readSubscription = this.admin.getInstructionAsset(assetId, version).subscribe({
      next: text => {
        this.reading.set(false);
        this.text.set(text);
        this.reference.set({ assetId: text.assetId, version: text.version, hash: text.hash });
        this.touched.emit();
      },
      error: (error: unknown) => {
        this.reading.set(false);
        this.text.set(null);
        this.reference.set(null);
        this.readError.set(toNhAssistantError(error));
      }
    });
  }

  /** Lists the application's assets and reads the text of the saved reference. */
  load(): void {
    this.listError.set(null);
    this.listSubscription?.unsubscribe();
    this.listSubscription = this.admin.listInstructionAssets().subscribe({
      next: list => this.assets.set(list.assets),
      error: (error: unknown) => {
        this.assets.set([]);
        this.listError.set(toNhAssistantError(error));
      }
    });
    if (this.reference() && this.text() === null) {
      this.readSaved();
    }
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  /**
   * Reads the saved reference's text to show it, without changing the reference: a saved asset whose
   * text changed keeps the hash it was saved with, and its problem says so.
   */
  private readSaved(): void {
    const reference = this.reference()!;
    this.reading.set(true);
    this.readSubscription?.unsubscribe();
    this.readSubscription = this.admin.getInstructionAsset(reference.assetId, reference.version).subscribe({
      next: text => {
        this.reading.set(false);
        this.text.set(text);
      },
      error: (error: unknown) => {
        this.reading.set(false);
        this.readError.set(toNhAssistantError(error));
      }
    });
  }

  private t(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}

/** Whether two references name the same asset, version and hash. */
export function sameInstructionAsset(left: InstructionAssetReference | null | undefined, right: InstructionAssetReference | null | undefined): boolean {
  return !!left && !!right && left.assetId === right.assetId && left.version === right.version && left.hash === right.hash;
}

function valueOf(assetId: string, version: number): string {
  return `${assetId}@${version}`;
}
