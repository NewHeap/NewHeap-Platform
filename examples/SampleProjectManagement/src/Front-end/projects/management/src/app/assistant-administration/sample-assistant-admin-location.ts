import { Signal, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { NhAssistantAdminLocation, nhAssistantAgentTab } from '@newheap/platform-ai-chat/admin';

/** Where the administration is, as the page's URL keeps it, and how a navigation in it updates the URL. */
export interface SampleAssistantAdminLocation {
  location: Signal<NhAssistantAdminLocation>;
  follow(location: NhAssistantAdminLocation): void;
}

/**
 * SPM-253: keeps the administration's section, the open agent and its tab in the URL's query
 * (`?section=…&agent=…&view=…`), so a link opens the same agent page and the browser's back button
 * returns to the previous one. Call it in the injection context of the page that hosts
 * `nh-assistant-admin`; bind `location` to its `location` and `follow` to its `locationChange`.
 */
export function sampleAssistantAdminLocation(): SampleAssistantAdminLocation {
  const route = inject(ActivatedRoute);
  const router = inject(Router);
  const query = toSignal(route.queryParamMap, { initialValue: route.snapshot.queryParamMap });

  const location = computed<NhAssistantAdminLocation>(() => {
    const params = query();
    const section = params.get('section');
    const agentId = params.get('agent');
    return {
      tab: section === 'mcp-servers' || section === 'context' ? section : 'agents',
      agentId,
      agentTab: agentId ? nhAssistantAgentTab(params.get('view')) : null
    };
  });

  return {
    location,
    follow: next => {
      void router.navigate([], {
        relativeTo: route,
        queryParams: {
          section: next.tab === 'agents' ? null : next.tab,
          agent: next.agentId,
          view: next.agentId && next.agentTab !== 'overview' ? next.agentTab : null
        },
        queryParamsHandling: 'merge'
      });
    }
  };
}
