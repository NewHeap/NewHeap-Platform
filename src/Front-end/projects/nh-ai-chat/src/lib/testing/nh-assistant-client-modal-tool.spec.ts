import { EnvironmentInjector, EventEmitter, Type, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NhModalOptions, NhModalService } from '@newheap/platform-common';
import { NhAssistantClientToolInvocation, NhAssistantClientToolResult } from '@newheap/platform-ai-chat';
import { NhAssistantModalToolContent, nhAssistantModalTool } from '@newheap/platform-ai-chat/client-tools';

interface SavedTask {
  id: string;
  title: string;
}

/** Stands in for mutate modal content: the outputs and close of `NhModalMutateBaseComponent`. */
class TaskFormContent implements NhAssistantModalToolContent<SavedTask> {
  readonly created = new EventEmitter<SavedTask>();
  readonly updated = new EventEmitter<SavedTask>();
  closed = false;

  constructor(private readonly modal: FakeModal) {
  }

  closeDialog(): void {
    this.closed = true;
    this.modal.close();
  }
}

/** Stands in for the modal reference: destroying the modal ends every way it closes. */
class FakeModal {
  readonly contentComponent = new TaskFormContent(this);
  private readonly destroyed: (() => void)[] = [];
  readonly componentRef = { onDestroy: (callback: () => void) => this.destroyed.push(callback) };

  close(): void {
    this.destroyed.splice(0).forEach(callback => callback());
  }
}

class FakeModalService {
  readonly opened: { component: Type<unknown>; options: NhModalOptions; inputs: Record<string, unknown>; modal: FakeModal }[] = [];

  open(component: Type<unknown>, options: NhModalOptions, inputs: Record<string, unknown>): FakeModal {
    const modal = new FakeModal();
    this.opened.push({ component, options, inputs, modal });
    return modal;
  }
}

class TaskFormComponent {
}

const createTask = nhAssistantModalTool<{ projectId: string; title: string }, SavedTask>({
  toolId: 'projects.create-task',
  component: TaskFormComponent as unknown as Type<NhAssistantModalToolContent<SavedTask>>,
  options: () => ({ title: 'Create task', modalClasses: 'large' }),
  inputs: args => ({ prefill: { projectId: args.projectId, title: args.title } }),
  resultId: saved => saved.id
});

describe('nhAssistantModalTool', () => {
  let modals: FakeModalService;

  beforeEach(() => {
    modals = new FakeModalService();
    TestBed.configureTestingModule({ providers: [{ provide: NhModalService, useValue: modals }] });
  });

  function open(): Promise<NhAssistantClientToolResult> {
    const invocation: NhAssistantClientToolInvocation = {
      invocationId: 'i1',
      toolId: 'projects.create-task',
      conversationId: 'c1',
      arguments: { projectId: 'p-1', title: 'Book the venue' },
      expiresAt: '2099-01-01T00:00:00Z'
    };
    return Promise.resolve(runInInjectionContext(TestBed.inject(EnvironmentInjector), () => createTask.run(invocation)));
  }

  it('opens the registered form prefilled and reports that the person saved it, with the id the application returns', async () => {
    const result = open();
    const opened = modals.opened[0];
    expect(opened.component).toBe(TaskFormComponent);
    expect(opened.options).toEqual(jasmine.objectContaining({ title: 'Create task', modalClasses: 'large' }));
    expect(opened.inputs).toEqual({ prefill: { projectId: 'p-1', title: 'Book the venue' } });

    opened.modal.contentComponent.created.emit({ id: 'task-7', title: 'Book the venue' });

    expect(await result).toEqual({ status: 'completed', outcome: 'saved', id: 'task-7' });
  });

  it('reports that the person cancelled the form with its own close', async () => {
    const result = open();

    modals.opened[0].modal.contentComponent.closeDialog();

    expect(await result).toEqual({ status: 'completed', outcome: 'cancelled' });
    expect(modals.opened[0].modal.contentComponent.closed).toBeTrue();
  });

  it('reports that the modal closed another way, such as its close button or the page that held it', async () => {
    const result = open();

    modals.opened[0].modal.close();

    expect(await result).toEqual({ status: 'completed', outcome: 'closed' });
  });

  it('opens nothing for arguments the application does not accept and loads its form on first use', async () => {
    const loaded = nhAssistantModalTool<{ projectId?: unknown }, SavedTask>({
      toolId: 'projects.create-task',
      loadComponent: () => Promise.resolve(TaskFormComponent as unknown as Type<NhAssistantModalToolContent<SavedTask>>),
      accepts: args => args.projectId === 'p-1'
    });
    const run = (projectId: string) => Promise.resolve(runInInjectionContext(
      TestBed.inject(EnvironmentInjector),
      () => loaded.run({ invocationId: 'i1', toolId: 'projects.create-task', conversationId: 'c1', arguments: { projectId }, expiresAt: '2099-01-01T00:00:00Z' })));

    expect(await run('elsewhere')).toEqual({ status: 'unavailable', outcome: 'arguments-not-allowed' });
    expect(modals.opened).toEqual([]);

    const result = run('p-1');
    await Promise.resolve();
    await Promise.resolve();
    expect(modals.opened[0].component).toBe(TaskFormComponent);
    modals.opened[0].modal.contentComponent.closeDialog();
    expect(await result).toEqual({ status: 'completed', outcome: 'cancelled' });
    expect(() => nhAssistantModalTool({ toolId: 'projects.create-task' })).toThrowError(/exactly one of component and loadComponent/);
  });
});
