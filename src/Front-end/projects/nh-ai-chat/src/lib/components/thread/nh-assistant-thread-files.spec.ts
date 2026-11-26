import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { of, Subject, throwError } from 'rxjs';
import { AgentArtifact } from '../../models/assistant-agent-run.models';
import { Message } from '../../models/assistant-api.models';
import { NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { NhAssistantThreadComponent } from './nh-assistant-thread.component';

const report: AgentArtifact = {
  id: 'f1', messageId: 'a1', name: 'Report.pdf', version: 1, contentType: 'application/pdf',
  sizeBytes: 7758, createdAt: '2026-01-01T12:00:00Z', runId: null, restricted: false
};
const answer = (id: string): Message => ({
  id, role: 'assistant', createdAt: report.createdAt, parts: [{ type: 'text', text: id }]
});

describe('Files within assistant messages', () => {
  let api: jasmine.SpyObj<NhAssistantApiService>;
  let fixture: ComponentFixture<NhAssistantThreadComponent>;
  const open = (messages: Message[]) => {
    const result = TestBed.createComponent(NhAssistantThreadComponent);
    result.componentRef.setInput('conversationId', 'c1');
    result.componentRef.setInput('messages', messages);
    result.nativeElement.style.cssText = 'height:600px;width:375px';
    result.detectChanges();
    return result;
  };

  beforeEach(() => {
    api = jasmine.createSpyObj<NhAssistantApiService>('api', ['getArtifacts', 'downloadArtifact']);
    api.getArtifacts.and.returnValue(of([report]));
    TestBed.configureTestingModule({ providers: [provideTranslateService(), { provide: NhAssistantApiService, useValue: api }] });
  });

  for (const virtual of [false, true]) {
    it('keeps files inside their producing answer' + (virtual ? ' with virtual scrolling' : ''), async () => {
      const messages = [answer('a1'), answer('a2')];
      if (virtual) {
        messages.push(...Array.from({ length: 200 }, (_, i) => answer('later-' + i)));
      }
      fixture = open(messages);
      await fixture.whenStable();
      fixture.detectChanges();
      const host: HTMLElement = fixture.nativeElement;
      expect(host.querySelector('[data-message-id="a1"] nh-assistant-artifacts')?.textContent).toContain('Report.pdf');
      expect(host.querySelector('[data-message-id="a2"] nh-assistant-artifacts')).toBeNull();
      expect(host.querySelectorAll('nh-assistant-artifacts').length).toBe(1);
      expect(api.getArtifacts).toHaveBeenCalledTimes(1);
      const card = host.querySelector('nh-assistant-artifacts') as HTMLElement;
      expect(card.scrollWidth).toBeLessThanOrEqual(card.clientWidth + 1);
      fixture.destroy();
      fixture = open(messages);
      await fixture.whenStable();
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('[data-message-id="a1"] nh-assistant-artifacts')?.textContent).toContain('Report.pdf');
    });
  }

  it('adds new files to the new answer after streaming without moving earlier files', () => {
    fixture = open([answer('a1'), answer('a2')]);
    fixture.componentRef.setInput('streaming', true);
    fixture.detectChanges();
    expect(api.getArtifacts).toHaveBeenCalledTimes(1);
    api.getArtifacts.and.returnValue(of([{ ...report, id: 'f2', messageId: 'a2', version: 2 }, report]));
    fixture.componentRef.setInput('streaming', false);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('[data-message-id="a1"] .card').length).toBe(1);
    expect(fixture.nativeElement.querySelectorAll('[data-message-id="a2"] .card').length).toBe(1);
    expect(api.getArtifacts).toHaveBeenCalledTimes(2);
  });

  it('cancels stale requests when changing conversations and retries a listing failure', () => {
    const pending = new Subject<AgentArtifact[]>();
    api.getArtifacts.and.returnValue(pending);
    fixture = open([answer('a1')]);
    api.getArtifacts.and.returnValue(throwError(() => new Error('offline')));
    fixture.componentRef.setInput('conversationId', 'c2');
    fixture.detectChanges();
    pending.next([report]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('nh-assistant-artifacts')).toBeNull();
    expect(fixture.nativeElement.querySelector('[role="alert"]')).not.toBeNull();
    api.getArtifacts.and.returnValue(of([report]));
    fixture.nativeElement.querySelector('[role="alert"] button').click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-message-id="a1"] nh-assistant-artifacts')).not.toBeNull();
  });

  it('preserves access to legacy files without inventing a message association', () => {
    api.getArtifacts.and.returnValue(of([{ ...report, messageId: null }]));
    fixture = open([answer('a1')]);
    expect(fixture.nativeElement.querySelector('article nh-assistant-artifacts')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Report.pdf');
  });
});
