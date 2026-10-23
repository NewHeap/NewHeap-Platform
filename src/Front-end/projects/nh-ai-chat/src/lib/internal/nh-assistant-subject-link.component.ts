import { LocationStrategy } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  EnvironmentInjector,
  computed,
  inject,
  input,
  runInInjectionContext
} from '@angular/core';
import { Router } from '@angular/router';
import { NH_ASSISTANT_CONFIG } from '../nh-assistant.config';

/** Where a subject link leads: a route of the host application or an `https` address elsewhere. */
export interface NhAssistantSubjectTarget {
  href: string;
  /** The URL the router navigates to; `null` for an address outside the application. */
  route: string | null;
}

/**
 * The name of a subject with the name of its type, linked to where the application shows the
 * subject (internal). The host's `subjectLinks` builder wins over the server's link; a link within
 * the application navigates through the router, an `https` link leaves it, and anything else shows
 * no link. Ctrl-, Cmd-, Shift- and middle-clicks keep the browser's behavior, such as a new tab.
 * A name that already says the type, as "The whole application" does for the application as a
 * whole, is shown once.
 */
@Component({
  selector: 'nh-assistant-subject-link',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (shownTypeName(); as type) {
      <!-- The space keeps the type and the name apart for screen readers; the flex gap shows it. -->
      <span class="nh-assistant-subject-type">{{ type }}</span>{{ ' ' }}
    }
    @if (target(); as link) {
      <a class="nh-assistant-subject-name" [href]="link.href" (click)="open($event, link)">{{ label() }}</a>
    } @else {
      <span class="nh-assistant-subject-name">{{ label() }}</span>
    }
  `,
  styleUrl: './nh-assistant-subject-link.component.scss'
})
export class NhAssistantSubjectLinkComponent {
  private readonly config = inject(NH_ASSISTANT_CONFIG, { optional: true });
  private readonly router = inject(Router, { optional: true });
  private readonly locationStrategy = inject(LocationStrategy, { optional: true });
  private readonly injector = inject(EnvironmentInjector);

  readonly subjectType = input.required<string>();
  readonly subjectId = input.required<string>();
  /** The text people read, usually the subject's display name. */
  readonly label = input.required<string>();
  /** The name of the subject type, such as "Project"; nothing is shown without one. */
  readonly typeName = input<string | null | undefined>(null);
  /** The server's link to the subject. */
  readonly link = input<string | null | undefined>(null);

  /** The type's name, unless the subject's name is the type's name itself, as for the whole application. */
  readonly shownTypeName = computed(() => {
    const type = this.typeName()?.trim();
    if (!type) {
      return null;
    }

    return type.toLocaleLowerCase() === this.label().trim().toLocaleLowerCase() ? null : type;
  });

  readonly target = computed<NhAssistantSubjectTarget | null>(() => {
    const builder = this.config?.subjectLinks?.[this.subjectType()];
    const subject = { type: this.subjectType(), id: this.subjectId() };
    const custom = builder ? runInInjectionContext(this.injector, () => builder(subject)) : undefined;
    if (custom === null) {
      return null;
    }
    if (Array.isArray(custom)) {
      return this.routeTarget(this.router ? this.router.serializeUrl(this.router.createUrlTree([...custom])) : null);
    }

    return this.urlTarget(typeof custom === 'string' ? custom : this.link() ?? null);
  });

  open(event: MouseEvent, target: NhAssistantSubjectTarget): void {
    nhAssistantOpenLink(event, target, this.router);
  }

  private urlTarget(url: string | null): NhAssistantSubjectTarget | null {
    return nhAssistantLinkTarget(url, this.router, this.locationStrategy);
  }

  private routeTarget(url: string | null): NhAssistantSubjectTarget | null {
    return nhAssistantRouteTarget(url, this.router, this.locationStrategy);
  }
}

/**
 * Where a link the server sent leads (internal): a path within the application, which the router
 * opens, an `https` address elsewhere, or `null` for anything else, so no other scheme reaches an
 * anchor. Subject links and the links of application messages follow this rule.
 */
export function nhAssistantLinkTarget(
  url: string | null | undefined,
  router: Router | null,
  locationStrategy: LocationStrategy | null
): NhAssistantSubjectTarget | null {
  if (!url) {
    return null;
  }
  if (url.startsWith('/') && !url.startsWith('//') && !url.startsWith('/\\')) {
    return nhAssistantRouteTarget(url, router, locationStrategy);
  }

  return /^https:\/\/[^/\\\s]+/i.test(url) ? { href: url, route: null } : null;
}

/** A route of the application as the browser shows it and as the router opens it (internal). */
export function nhAssistantRouteTarget(
  url: string | null,
  router: Router | null,
  locationStrategy: LocationStrategy | null
): NhAssistantSubjectTarget | null {
  if (!url) {
    return null;
  }

  return {
    href: locationStrategy ? locationStrategy.prepareExternalUrl(url) : url,
    route: router ? url : null
  };
}

/**
 * Opens a route of the application through the router on a plain click (internal). Ctrl-, Cmd-,
 * Shift-, Alt- and middle-clicks, and addresses outside the application, keep the browser's behavior.
 */
export function nhAssistantOpenLink(event: MouseEvent, target: NhAssistantSubjectTarget, router: Router | null): void {
  if (!target.route || !router || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
    return;
  }

  event.preventDefault();
  void router.navigateByUrl(target.route);
}
