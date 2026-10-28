import { OverlayContainer } from '@angular/cdk/overlay';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantPanelComponent } from '../components/panel/nh-assistant-panel.component';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../services/nh-assistant.store';

/** A page of the application that shows the chat in place, for long sessions. */
@Component({
  standalone: true,
  imports: [NhAssistantPanelComponent],
  template: '<div style="height: 640px"><nh-assistant-panel mode="page" /></div>'
})
class ChatPageComponent {}

function scenario(): NhAssistantMockScenario {
  const conversation = (id: string, title: string) => ({
    id,
    agentId: 'projects',
    agentVersion: 1,
    title,
    status: 'idle' as const,
    createdAt: '2026-10-27T09:00:00Z',
    updatedAt: '2026-10-27T09:00:00Z',
    messages: [],
    pendingApproval: null
  });
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: false }],
    conversations: [conversation('c1', 'Weekly planning'), conversation('c2', 'Budget review')],
    turns: []
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

async function render(): Promise<{ fixture: ComponentFixture<ChatPageComponent>; element: HTMLElement }> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  const fixture = TestBed.createComponent(ChatPageComponent);
  return { fixture, element: await settle(fixture) };
}

describe('NhAssistantPanelComponent in a page (mode="page")', () => {
  it('renders the chat in place as a region with nothing to close and leaves the drawer to the layout', async () => {
    const { fixture, element } = await render();

    const page = element.querySelector('.drawer.page') as HTMLElement;
    expect(page).not.toBeNull();
    expect(page.getAttribute('role')).toBe('region');
    expect(page.getAttribute('aria-modal')).toBeNull();
    expect(element.querySelector('nh-assistant-panel')!.classList).toContain('nh-assistant-page');
    expect(page.querySelector('button[aria-label="Close assistant"]')).toBeNull();
    expect(page.querySelector('textarea')).not.toBeNull();
    // Every control keeps its accessible name.
    for (const button of Array.from(page.querySelectorAll('button'))) {
      expect(button.getAttribute('aria-label') ?? button.textContent?.trim()).withContext(button.outerHTML).toBeTruthy();
    }

    // The page did not register as the drawer: opening the panel puts nothing in the overlay.
    TestBed.inject(NhAssistantPanelService).open();
    await settle(fixture);
    expect(TestBed.inject(OverlayContainer).getContainerElement().querySelector('.drawer')).toBeNull();
  });

  it('lists the conversations beside the thread and opens one from the list', async () => {
    const { fixture, element } = await render();
    const store = TestBed.inject(NhAssistantStore);

    const sidebar = element.querySelector('nh-assistant-conversation-list.sidebar') as HTMLElement;
    expect(sidebar).not.toBeNull();
    const titles = Array.from(sidebar.querySelectorAll('.item .title')).map(title => title.textContent?.trim());
    expect(titles).toEqual(jasmine.arrayContaining(['Weekly planning', 'Budget review']));

    const budget = Array.from(sidebar.querySelectorAll<HTMLButtonElement>('.item'))
      .find(item => item.textContent?.includes('Budget review'))!;
    budget.click();
    await settle(fixture);
    expect(store.activeConversation()?.id).toBe('c2');
    expect(sidebar.querySelector('.item[aria-current="true"]')?.textContent).toContain('Budget review');
  });
});
