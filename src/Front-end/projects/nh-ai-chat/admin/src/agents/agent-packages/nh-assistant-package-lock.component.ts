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
  signal
} from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { PackageLockView, packageFieldLabelKey } from './package-parts';

/** The longest reason the server stores with an unlink. */
const maxUnlinkReason = 500;

let nextId = 0;

/**
 * The notice of a part, or a whole agent, that an agent package links (internal): a lock with the
 * package's id and version and what it locks, and "Unlink" with an optional reason. Unlinking makes the
 * part local: it changes here again, and a later version of the package keeps it as it is (`kept-local`)
 * unless the import relinks it. Without `part` the notice concerns the whole agent and unlinks every
 * part of it. `unlinked` tells the host to read the part again.
 */
@Component({
  selector: 'nh-assistant-package-lock',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-package-lock.component.html',
  styleUrl: '../../nh-assistant-admin-shared.scss',
  styles: [':host { display: block; } .package-lock { display: flex; flex-direction: column; gap: 8px; }']
})
export class NhAssistantPackageLockComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private subscription?: Subscription;

  /** The link of the part; nothing shows without one. */
  readonly lock = input<PackageLockView | null>(null);
  readonly agentId = input.required<string>();
  /** The part's key, such as `skill:intake`; `null` for the whole agent. */
  readonly part = input<string | null>(null);
  /** Whether "Unlink" is offered, as it is wherever the part is shown with its actions. */
  readonly unlinkable = input(true);
  /** The part, or the whole agent, was unlinked. */
  readonly unlinked = output<void>();

  readonly id = `nh-assistant-package-lock-${nextId++}`;
  readonly maxReason = maxUnlinkReason;
  readonly asking = signal(false);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** The locked fields by their names in the reader's language. */
  readonly fieldNames = computed(() => (this.lock()?.fields ?? [])
    .map(field => {
      const key = packageFieldLabelKey(field);
      const text = this.translate.instant(key) as string;
      return text && text !== key ? text : field;
    })
    .join(', '));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  ask(): void {
    this.asking.set(true);
    this.reason.set('');
    this.error.set(null);
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLInputElement>('[data-unlink-reason]')?.focus(), { injector: this.injector });
  }

  cancel(): void {
    this.asking.set(false);
    this.error.set(null);
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  unlink(): void {
    if (this.busy()) {
      return;
    }

    const reason = this.reason().trim();
    this.busy.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.unlinkAgentPackage(this.agentId(), {
      part: this.part(),
      reason: reason.length > 0 ? reason : null
    }).subscribe({
      next: () => {
        this.busy.set(false);
        this.asking.set(false);
        this.unlinked.emit();
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        this.error.set(toNhAssistantError(failure));
      }
    });
  }
}
