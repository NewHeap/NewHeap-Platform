import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { NhAssistantToolResult } from '@newheap/platform-ai-chat';

/**
 * SPM-248: the portal's own card of what the project search returned (`projects.search`), which the
 * assistant panel shows instead of the default card. It reads only the trusted presentation the API's
 * result presenter wrote (`SampleAssistantToolResultPresenter`): the title, how many projects matched,
 * each project as its key and name, and the link to the project board, which opens through the router.
 * The raw result and the model's arguments never reach it.
 */
@Component({
  selector: 'app-sample-project-search-result',
  standalone: true,
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="result" [attr.aria-label]="'project.assistant-result-search-label' | translate">
      <header class="header">
        <i class="ph ph-magnifying-glass" aria-hidden="true"></i>
        <h4>{{ presentation().title }}</h4>
      </header>
      @if (presentation().summary; as summary) {
        <p class="summary">{{ summary }}</p>
      }
      @if (projects().length > 0) {
        <ul class="projects">
          @for (project of projects(); track project.key) {
            <li>
              <span class="key">{{ project.key }}</span>
              <span class="name">{{ project.name }}</span>
            </li>
          }
        </ul>
      }
      @if (presentation().link; as link) {
        <a class="link" [href]="link.url" (click)="open($event, link.url)">{{ link.label }}</a>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }

    .result {
      border: 1px solid var(--line);
      border-radius: var(--radius-control);
      padding: 12px;
      color: var(--ink);
      background: var(--surface);
    }

    .header {
      display: flex;
      align-items: center;
      gap: 8px;
      color: var(--muted);
    }

    h4 {
      margin: 0;
      color: var(--ink);
      font-size: 14px;
      font-weight: 700;
      overflow-wrap: anywhere;
    }

    .summary {
      margin: 4px 0 0;
      color: var(--muted);
      font-size: 13px;
    }

    .projects {
      display: grid;
      gap: 4px;
      margin: 10px 0 0;
      padding: 0;
      list-style: none;
    }

    li {
      display: flex;
      align-items: baseline;
      gap: 8px;
      min-width: 0;
      font-size: 13px;
    }

    .key {
      flex: none;
      border-radius: 999px;
      padding: 1px 8px;
      color: var(--ink);
      background: var(--surface-muted);
      font-family: var(--font-mono);
      font-size: 12px;
    }

    .name {
      min-width: 0;
      overflow-wrap: anywhere;
    }

    .link {
      display: inline-block;
      margin-top: 10px;
      /* The assistant's link color, readable on the panel's light and dark surfaces. */
      color: var(--nh-assistant-link);
      font-size: 13px;
      font-weight: 600;
    }

    .link:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
      border-radius: 4px;
    }
  `
})
export class SampleProjectSearchResultComponent {
  private readonly router = inject(Router);
  private readonly result = inject(NhAssistantToolResult);

  readonly presentation = this.result.presentation;
  /** Each project found: the presenter writes its key as the label and its name as the value. */
  readonly projects = computed(() => this.presentation().fields.map(field => ({ key: field.label, name: field.value })));

  /** Opens the board through the router; a modified click keeps the browser's behavior. */
  open(event: MouseEvent, url: string): void {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || !url.startsWith('/')) {
      return;
    }

    event.preventDefault();
    void this.router.navigateByUrl(url);
  }
}
