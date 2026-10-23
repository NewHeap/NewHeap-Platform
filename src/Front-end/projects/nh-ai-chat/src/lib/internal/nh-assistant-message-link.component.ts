import { LocationStrategy } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Router } from '@angular/router';
import { NhAssistantIconComponent } from './nh-assistant-icon.component';
import { NhAssistantSubjectTarget, nhAssistantLinkTarget, nhAssistantOpenLink } from './nh-assistant-subject-link.component';

/**
 * The link of a message the application posted (internal): a path within the application opens
 * through the router, an `https` address leaves it, and anything else shows nothing, by the same rule
 * as subject links.
 */
@Component({
  selector: 'nh-assistant-message-link',
  standalone: true,
  imports: [NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (target(); as link) {
      <a class="nh-assistant-message-link" [href]="link.href" (click)="open($event, link)">
        <nh-assistant-icon name="link" />{{ label() }}
      </a>
    }
  `,
  styles: `
    :host {
      display: block;
    }

    .nh-assistant-message-link {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: var(--nha-accent);
      font-size: 13px;
      font-weight: 600;
      text-decoration: none;
    }

    .nh-assistant-message-link:hover {
      text-decoration: underline;
    }

    .nh-assistant-message-link:focus-visible {
      outline: 2px solid var(--nha-focus);
      outline-offset: 2px;
      border-radius: 4px;
    }
  `
})
export class NhAssistantMessageLinkComponent {
  private readonly router = inject(Router, { optional: true });
  private readonly locationStrategy = inject(LocationStrategy, { optional: true });

  /** A path within the application or an `https` address. */
  readonly url = input.required<string>();
  /** The text people read. */
  readonly label = input.required<string>();

  readonly target = computed(() => nhAssistantLinkTarget(this.url(), this.router, this.locationStrategy));

  open(event: MouseEvent, target: NhAssistantSubjectTarget): void {
    nhAssistantOpenLink(event, target, this.router);
  }
}
