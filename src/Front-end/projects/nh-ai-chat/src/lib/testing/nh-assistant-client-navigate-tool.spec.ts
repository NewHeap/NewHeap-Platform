import { Component, EnvironmentInjector, inject, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { NhAssistantClientToolInvocation, NhAssistantClientToolResult } from '@newheap/platform-ai-chat';
import { nhAssistantNavigateTool } from '@newheap/platform-ai-chat/client-tools';

@Component({ standalone: true, template: '' })
class PageComponent {
}

const projectIdPattern = /^[0-9a-f-]{36}$/;

/** The application's allow-list: only a project id it recognises reaches a route. */
const openProject = nhAssistantNavigateTool<{ projectId?: unknown }>({
  toolId: 'projects.open',
  route: args => typeof args.projectId === 'string' && projectIdPattern.test(args.projectId)
    ? inject(Router).createUrlTree(['/projects'], { queryParams: { project: args.projectId } })
    : null
});

function invocation(args: Record<string, unknown>): NhAssistantClientToolInvocation {
  return { invocationId: 'i1', toolId: 'projects.open', conversationId: 'c1', arguments: args, expiresAt: '2099-01-01T00:00:00Z' };
}

function run(args: Record<string, unknown>): Promise<NhAssistantClientToolResult> {
  return Promise.resolve(runInInjectionContext(TestBed.inject(EnvironmentInjector), () => openProject.run(invocation(args))));
}

describe('nhAssistantNavigateTool', () => {
  let leave = true;
  const projectId = '7f3c2a90-4d1e-4c55-9a51-0b8f3c6d2e11';

  beforeEach(() => {
    leave = true;
    TestBed.configureTestingModule({
      providers: [provideRouter([
        { path: 'projects', component: PageComponent },
        { path: 'draft', component: PageComponent, canDeactivate: [() => leave] }
      ])]
    });
  });

  it('navigates to the route the application allows for the arguments', async () => {
    const result = await run({ projectId });

    expect(result).toEqual({ status: 'completed', outcome: 'navigated' });
    expect(TestBed.inject(Router).url).toBe(`/projects?project=${projectId}`);
  });

  it('refuses arguments the application does not allow without navigating', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/draft');

    const result = await run({ projectId: 'https://example.com/elsewhere' });

    expect(result).toEqual({ status: 'unavailable', outcome: 'route-not-allowed' });
    expect(router.url).toBe('/draft');
  });

  it('reports a navigation a guard kept as declined', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/draft');
    leave = false;

    const result = await run({ projectId });

    expect(result).toEqual({ status: 'declined', outcome: 'navigation-cancelled' });
    expect(router.url).toBe('/draft');
  });
});
