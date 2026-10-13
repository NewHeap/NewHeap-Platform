import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { of, throwError } from 'rxjs';
import { AgentProposalFeedback } from '../../models/assistant-agent-run.models';
import { provideNhAssistant } from '../../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../../services/nh-assistant-agent-run-api.service';
import { NhAssistantApiError } from '../../services/nh-assistant-transport';
import { NhAssistantProposalFeedbackComponent, NhAssistantProposalFeedbackSaved } from './nh-assistant-proposal-feedback.component';

function stored(category: AgentProposalFeedback['category'], text: string | null = null): AgentProposalFeedback {
  return {
    approvalId: 'approval-1',
    category,
    group: 'wrong',
    text,
    bulk: false,
    createdAt: '2026-10-13T10:00:00Z',
    updatedAt: '2026-10-13T10:00:00Z'
  };
}

describe('NhAssistantProposalFeedbackComponent', () => {
  let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;
  let fixture: ComponentFixture<NhAssistantProposalFeedbackComponent>;
  let saved: NhAssistantProposalFeedbackSaved[];
  let dismissed: number;

  beforeEach(() => {
    api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', ['giveFeedback', 'giveFeedbackMany']);
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
        { provide: NhAssistantAgentRunApiService, useValue: api }
      ]
    });
    fixture = TestBed.createComponent(NhAssistantProposalFeedbackComponent);
    saved = [];
    dismissed = 0;
    fixture.componentInstance.saved.subscribe(value => saved.push(value));
    fixture.componentInstance.dismissed.subscribe(() => dismissed++);
  });

  function render(approvalIds: string[], initial: AgentProposalFeedback | null = null): HTMLElement {
    fixture.componentRef.setInput('approvalIds', approvalIds);
    fixture.componentRef.setInput('initial', initial);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function send(): HTMLButtonElement {
    return (fixture.nativeElement as HTMLElement).querySelector('button[type="submit"]') as HTMLButtonElement;
  }

  function choose(category: string): void {
    ((fixture.nativeElement as HTMLElement).querySelector(`input[value="${category}"]`) as HTMLInputElement).click();
    fixture.detectChanges();
  }

  function type(text: string): void {
    const textarea = (fixture.nativeElement as HTMLElement).querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = text;
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('offers the categories as one labelled radio group in a fixed order and keeps Send disabled until one is chosen', () => {
    const element = render(['approval-1']);

    const fieldset = element.querySelector('fieldset') as HTMLFieldSetElement;
    expect(fieldset.querySelector('legend')?.textContent).toContain('nh-assistant.proposal-feedback.legend');
    const radios = Array.from(fieldset.querySelectorAll<HTMLInputElement>('input[type="radio"]'));
    expect(radios.map(radio => radio.value)).toEqual([
      'incorrect', 'wrong-value', 'not-needed', 'not-now', 'already-done', 'not-allowed', 'other'
    ]);
    expect(new Set(radios.map(radio => radio.name)).size).toBe(1);
    expect(radios.every(radio => radio.closest('label') !== null && !!radio.getAttribute('aria-describedby'))).toBeTrue();
    expect(element.querySelector('textarea')?.getAttribute('maxlength')).toBe('1000');
    expect(send().disabled).toBeTrue();

    choose('not-needed');

    expect(send().disabled).toBeFalse();
    expect(element.querySelector('.chip.selected input')?.getAttribute('value')).toBe('not-needed');
  });

  it('sends one proposal through its own endpoint with the trimmed text and thanks the person', () => {
    api.giveFeedback.and.returnValue(of(stored('wrong-value', 'Too high')));
    const element = render(['approval-1']);
    choose('wrong-value');
    type('  Too high  ');

    send().click();
    fixture.detectChanges();

    expect(api.giveFeedback).toHaveBeenCalledOnceWith('approval-1', 'wrong-value', 'Too high');
    expect(api.giveFeedbackMany).not.toHaveBeenCalled();
    expect(saved).toEqual([{ approvalIds: ['approval-1'], category: 'wrong-value', text: 'Too high' }]);
    const status = element.querySelector('[role="status"]') as HTMLElement;
    expect(status.textContent).toContain('nh-assistant.proposal-feedback.thanks');
    expect(element.querySelector('form')).toBeNull();
  });

  it('sends proposals that were rejected together once, as bulk feedback', () => {
    api.giveFeedbackMany.and.returnValue(of({
      results: [
        { approvalId: 'approval-1', succeeded: true, code: null },
        { approvalId: 'approval-2', succeeded: true, code: null }
      ]
    }));
    const element = render(['approval-1', 'approval-2']);
    expect(element.textContent).toContain('nh-assistant.proposal-feedback.bulk');
    choose('not-now');

    send().click();
    fixture.detectChanges();

    expect(api.giveFeedbackMany).toHaveBeenCalledOnceWith(['approval-1', 'approval-2'], 'not-now', null);
    expect(api.giveFeedback).not.toHaveBeenCalled();
    expect(saved[0].approvalIds).toEqual(['approval-1', 'approval-2']);
  });

  it('shows why the server accepted none of the bulk feedback', () => {
    api.giveFeedbackMany.and.returnValue(of({
      results: [
        { approvalId: 'approval-1', succeeded: false, code: 'assistant-feedback-not-accepted' },
        { approvalId: 'approval-2', succeeded: false, code: 'assistant-feedback-not-accepted' }
      ]
    }));
    const element = render(['approval-1', 'approval-2']);
    choose('incorrect');

    send().click();
    fixture.detectChanges();

    expect(saved).toEqual([]);
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('assistant-feedback-not-accepted');
    expect(send().disabled).toBeFalse();
  });

  it('shows a refusal as a safe error and keeps the answer for another attempt', () => {
    api.giveFeedback.and.returnValue(throwError(() => new NhAssistantApiError(
      400, 'assistant-feedback-invalid', 'nh-assistant.errors.assistant-feedback-invalid')));
    const element = render(['approval-1']);
    choose('other');
    type('Explained');

    send().click();
    fixture.detectChanges();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('assistant-feedback-invalid');
    expect((element.querySelector('textarea') as HTMLTextAreaElement).value).toBe('Explained');
    expect(saved).toEqual([]);
  });

  it('skips without sending anything', () => {
    const element = render(['approval-1']);
    const skip = Array.from(element.querySelectorAll('button')).find(button => button.textContent?.includes('skip'))!;

    skip.click();

    expect(dismissed).toBe(1);
    expect(api.giveFeedback).not.toHaveBeenCalled();
    expect(api.giveFeedbackMany).not.toHaveBeenCalled();
  });

  it('starts from the caller\'s own feedback when changing it', () => {
    api.giveFeedback.and.returnValue(of(stored('already-done')));
    const element = render(['approval-1'], stored('not-now', 'Next sprint'));

    expect((element.querySelector('input[value="not-now"]') as HTMLInputElement).checked).toBeTrue();
    expect((element.querySelector('textarea') as HTMLTextAreaElement).value).toBe('Next sprint');
    expect(element.textContent).toContain('nh-assistant.proposal-feedback.cancel');

    choose('already-done');
    send().click();

    expect(api.giveFeedback).toHaveBeenCalledOnceWith('approval-1', 'already-done', 'Next sprint');
  });
});
