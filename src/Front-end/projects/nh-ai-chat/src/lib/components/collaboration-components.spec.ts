import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { ConversationSummary } from '../models/assistant-api.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantActivity, NhAssistantStore } from '../services/nh-assistant.store';
import { NhAssistantActivityComponent } from './activity/nh-assistant-activity.component';
import { NhAssistantConversationListComponent } from './conversation-list/nh-assistant-conversation-list.component';
import { NhAssistantLauncherComponent } from './launcher/nh-assistant-launcher.component';
import { NhAssistantShareComponent } from './share/nh-assistant-share.component';

const scenario: NhAssistantMockScenario = {
  agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }],
  timing: { firstEventDelayMs: 0, eventDelayMs: 0, chunkSize: 64 },
  collaboration: {
    currentUser: { actorId: 'me', displayName: 'Robin' },
    directory: [{ actorId: 'colleague-sam', displayName: 'Sam', detail: 'Planning' }]
  },
  turns: [{ steps: [{ text: 'Answer.' }] }]
};

async function until(condition: () => boolean): Promise<void> {
  const started = Date.now();
  while (!condition()) {
    if (Date.now() - started > 3_000) {
      throw new Error('The condition was not met in time.');
    }
    TestBed.tick();
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}

function activity(id: string, status: NhAssistantActivity['status'], unread = false, active = false): NhAssistantActivity {
  return { id, title: `Conversation ${id}`, status, unread, shared: id === 'shared', active, updatedAt: '2026-10-02T10:00:00Z' };
}

@Component({
  standalone: true,
  imports: [NhAssistantActivityComponent],
  template: `<nh-assistant-activity [items]="items()" (conversationSelect)="selected = $event" />`
})
class ActivityHostComponent {
  readonly items = signal<NhAssistantActivity[]>([]);
  selected: string | null = null;
}

@Component({
  standalone: true,
  imports: [NhAssistantConversationListComponent],
  template: `<nh-assistant-conversation-list [conversations]="conversations()" (conversationDelete)="deleted = $event" />`
})
class ListHostComponent {
  readonly conversations = signal<ConversationSummary[]>([]);
  deleted: string | null = null;
}

describe('collaboration components', () => {
  beforeEach(() => {
    spyOn(document, 'hasFocus').and.returnValue(true);
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
        provideNhAssistantMockApi(scenario)
      ]
    });
  });

  describe('nh-assistant-activity', () => {
    let fixture: ComponentFixture<ActivityHostComponent>;

    beforeEach(() => {
      fixture = TestBed.createComponent(ActivityHostComponent);
      fixture.detectChanges();
    });

    it('takes no space without other conversations that need attention', () => {
      fixture.componentInstance.items.set([activity('open', 'running', false, true)]);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.strip')).toBeNull();
    });

    it('shows a chip per other conversation with its state and opens it', () => {
      fixture.componentInstance.items.set([
        activity('open', 'idle', false, true),
        activity('work', 'running'),
        activity('wait', 'waiting-for-approval'),
        activity('shared', 'idle', true)
      ]);
      fixture.detectChanges();

      const chips = Array.from(fixture.nativeElement.querySelectorAll('.chip')) as HTMLButtonElement[];
      expect(chips.map(chip => chip.dataset['state'])).toEqual(['running', 'waiting', 'unread']);
      expect(chips[2].querySelector('.shared')).not.toBeNull();
      chips[1].click();
      expect(fixture.componentInstance.selected).toBe('wait');
    });
  });

  describe('nh-assistant-conversation-list', () => {
    it('marks unread and running conversations and lets a participant leave', () => {
      const fixture = TestBed.createComponent(ListHostComponent);
      fixture.componentInstance.conversations.set([
        { id: 'a', agentId: 'projects', title: 'Mine', status: 'idle', createdAt: '', updatedAt: '', lastMessageSequence: 4, lastReadSequence: 2 },
        { id: 'b', agentId: 'projects', title: 'Busy', status: 'running', createdAt: '', updatedAt: '', lastMessageSequence: 5, lastReadSequence: 3 },
        { id: 'c', agentId: 'projects', title: 'Theirs', status: 'idle', createdAt: '', updatedAt: '', role: 'participant', participantCount: 2, lastMessageSequence: 1, lastReadSequence: 1 }
      ]);
      fixture.detectChanges();

      const rows = Array.from(fixture.nativeElement.querySelectorAll('li')) as HTMLElement[];
      expect(rows.map(row => row.classList.contains('unread'))).toEqual([true, false, false]);
      expect(rows.map(row => (row.querySelector('.indicator') as HTMLElement).dataset['state'] ?? null)).toEqual(['unread', 'running', null]);
      expect(rows[2].querySelector('.shared')).not.toBeNull();
      const leave = rows[2].querySelector('.delete') as HTMLButtonElement;
      expect(leave.getAttribute('title')).toBe('nh-assistant.conversation-list.leave');
      leave.click();
      expect(fixture.componentInstance.deleted).toBe('c');
      expect((rows[1].querySelector('.delete') as HTMLButtonElement).disabled).toBeTrue();
    });
  });

  describe('with the store', () => {
    let store: NhAssistantStore;

    beforeEach(async () => {
      store = TestBed.inject(NhAssistantStore);
      await store.initialize();
      await store.send('Hello');
      await until(() => store.activeConversation()?.status === 'idle');
    });

    it('lets the owner create, copy and stop an invitation link and invite a colleague', async () => {
      spyOn(navigator.clipboard, 'writeText').and.resolveTo();
      const fixture = TestBed.createComponent(NhAssistantShareComponent);
      fixture.detectChanges();

      (fixture.nativeElement.querySelector('button.primary') as HTMLButtonElement).click();
      await until(() => !!store.activeConversation()?.shareToken);
      fixture.detectChanges();
      const link = fixture.nativeElement.querySelector('.link-row input') as HTMLInputElement;
      expect(link.value).toContain(`#nh-assistant-join=${store.activeConversation()!.id}.`);
      (fixture.nativeElement.querySelector('.link-row button') as HTMLButtonElement).click();
      await until(() => fixture.componentInstance.copied());
      expect(navigator.clipboard.writeText).toHaveBeenCalledWith(link.value);

      fixture.componentInstance.onSearch({ target: { value: 'sa' } } as unknown as Event);
      await until(() => fixture.componentInstance.candidates().length === 1);
      await fixture.componentInstance.invite(fixture.componentInstance.candidates()[0]);
      expect(store.members().map(member => member.displayName)).toEqual(['Robin', 'Sam']);

      await fixture.componentInstance.revokeLink();
      expect(store.activeConversation()!.shareToken).toBeNull();
    });

    it('shows one launcher badge for unread answers', async () => {
      const fixture = TestBed.createComponent(NhAssistantLauncherComponent);
      fixture.detectChanges();
      await until(() => store.unreadCount() === 1);
      fixture.detectChanges();

      const badge = fixture.nativeElement.querySelector('.badge') as HTMLElement;
      expect(badge.dataset['state']).toBe('unread');
      expect(badge.textContent?.trim()).toBe('1');
      expect(TestBed.inject(NhAssistantMockBackend).requests.some(request => request.path.endsWith('/read'))).toBeFalse();
    });
  });
});
