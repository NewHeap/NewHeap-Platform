import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { AgentUploads } from '../../models/assistant-api.models';
import { provideNhAssistant } from '../../provide-nh-assistant';
import { NhAssistantAttachment } from '../../services/nh-assistant.store';
import { NhAssistantComposerComponent } from './nh-assistant-composer.component';

/**
 * The attach control of the composer: a labelled button that opens the file picker for the types the
 * agent accepts, the attachments with their progress, size or the reason they failed at the control,
 * removal by keyboard, and sending that waits while a file uploads.
 */
const uploads: AgentUploads = {
  acceptedMediaTypes: ['application/pdf', 'image/png'],
  maxFileBytes: 2 * 1024 * 1024,
  maxFiles: 3
};

function attachment(overrides: Partial<NhAssistantAttachment> = {}): NhAssistantAttachment {
  return {
    id: 'plan',
    name: 'Plan.pdf',
    mediaType: 'application/pdf',
    size: 1536,
    state: 'uploaded',
    progress: 1,
    file: { id: 'file-1', name: 'Plan.pdf', mediaType: 'application/pdf', size: 1536 },
    error: null,
    ...overrides
  };
}

describe('NhAssistantComposerComponent attachments', () => {
  let fixture: ComponentFixture<NhAssistantComposerComponent>;
  let element: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => null })
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    fixture = TestBed.createComponent(NhAssistantComposerComponent);
    element = fixture.nativeElement;
    fixture.detectChanges();
  });

  function set(name: 'uploads' | 'attachments', value: unknown): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  function attachButton(): HTMLButtonElement | null {
    return element.querySelector<HTMLButtonElement>('button.attach');
  }

  function fileInput(): HTMLInputElement {
    return element.querySelector<HTMLInputElement>('input[type="file"]')!;
  }

  it('offers the attach button only when the agent takes uploads, with what it accepts', () => {
    expect(attachButton()).toBeNull();
    expect(element.querySelector('input[type="file"]')).toBeNull();

    set('uploads', uploads);

    const button = attachButton()!;
    expect(button.getAttribute('aria-label')).toBe('Attach files');
    const hint = element.querySelector(`#${button.getAttribute('aria-describedby')}`)!.textContent!.trim();
    expect(hint).toBe('PDF, PNG, up to 2 MB each, at most 3 files per message');
    expect(button.getAttribute('title')).toBe(hint);
    // The picker offers the accepted types; the input itself stays out of the tab order.
    expect(fileInput().accept).toBe('application/pdf,.pdf,image/png,.png');
    expect(fileInput().multiple).toBeTrue();
    expect(fileInput().tabIndex).toBe(-1);
    expect(fileInput().getAttribute('aria-hidden')).toBe('true');
  });

  it('offers the extensions of every accepted type it knows, so a picker shows .csv files for text/csv', () => {
    set('uploads', { ...uploads, acceptedMediaTypes: ['text/csv', 'application/x-sample'] });

    // A system that names .csv after Excel still lists the files; an unknown type stays as it is.
    expect(fileInput().accept).toBe('text/csv,.csv,application/x-sample');
    const hint = element.querySelector(`#${attachButton()!.getAttribute('aria-describedby')}`)!.textContent!.trim();
    expect(hint).toBe('CSV, X-SAMPLE, up to 2 MB each, at most 3 files per message');
  });

  it('offers the extensions the application mapped to an accepted type', () => {
    set('uploads', {
      ...uploads,
      acceptedMediaTypes: ['application/acad', 'text/xml', 'application/xml'],
      mappedExtensions: [
        { extension: '.dwg', mediaType: 'application/acad' },
        { extension: '.dxf', mediaType: 'application/acad' },
        // The application's mapping takes .xml from the platform's application/xml.
        { extension: '.xml', mediaType: 'text/xml' },
        // A mapping to a type that is not accepted is not offered.
        { extension: '.ifc', mediaType: 'application/x-step' }
      ]
    });

    expect(fileInput().accept).toBe('application/acad,.dwg,.dxf,text/xml,.xml,application/xml');
  });

  it('opens the file picker from the button and hands the chosen files over', () => {
    set('uploads', uploads);
    const chosen: File[][] = [];
    fixture.componentInstance.attach.subscribe(files => chosen.push(files));
    const click = spyOn(fileInput(), 'click');

    attachButton()!.click();
    const plan = new File(['%PDF-1.7'], 'Plan.pdf', { type: 'application/pdf' });
    const transfer = new DataTransfer();
    transfer.items.add(plan);
    fileInput().files = transfer.files;
    fileInput().dispatchEvent(new Event('change'));

    expect(click).toHaveBeenCalledTimes(1);
    expect(chosen).toEqual([[plan]]);
    // The same file can be chosen again after it was removed.
    expect(fileInput().value).toBe('');
  });

  it('shows the progress, the size or why a file failed, at the control', () => {
    set('uploads', uploads);
    set('attachments', [
      attachment({ id: 'scan', name: 'Scan.png', mediaType: 'image/png', state: 'uploading', progress: 0.4, file: null }),
      attachment(),
      attachment({
        id: 'large',
        name: 'Large.pdf',
        state: 'failed',
        file: null,
        error: { code: 'assistant-file-too-large', messageKey: 'nh-assistant.errors.assistant-file-too-large' }
      }),
      attachment({
        id: 'virus',
        name: 'Virus.pdf',
        state: 'failed',
        file: null,
        error: { code: 'assistant-file-upload-refused', messageKey: 'nh-assistant.errors.assistant-file-upload-refused' }
      })
    ]);

    const list = element.querySelector('ul.attachments')!;
    expect(list.getAttribute('aria-label')).toBe('Attached files');
    const items = Array.from(list.querySelectorAll('li'));
    const progress = items[0].querySelector('progress')!;
    expect(progress.value).toBe(40);
    expect(progress.getAttribute('aria-label')).toBe('Uploading Scan.png');
    expect(items[1].querySelector('.attachment-meta')!.textContent!.trim()).toBe('1.5 KB');
    // A check before the upload explains itself with the limit; a server refusal with its code's text.
    const errors = items.slice(2).map(item => item.querySelector('[role="alert"]')!.textContent!.trim());
    expect(errors).toEqual([
      'The file is larger than 2 MB.',
      'The application refused this file.'
    ]);
  });

  it('waits with sending until every file is uploaded', () => {
    set('uploads', uploads);
    const textarea = element.querySelector('textarea')!;
    textarea.value = 'What does the plan say?';
    textarea.dispatchEvent(new Event('input'));
    set('attachments', [attachment({ state: 'uploading', progress: 0.5, file: null })]);
    const send = element.querySelector<HTMLButtonElement>('button.send')!;

    expect(send.disabled).toBeTrue();
    expect(element.querySelector('.hint')!.textContent).toContain('The message can be sent once the files are uploaded');

    set('attachments', [attachment()]);

    expect(send.disabled).toBeFalse();
  });

  it('fits a 375px screen without horizontal overflow', () => {
    // A phone of 375px wide, less the panel's 16px gutters.
    element.style.display = 'block';
    element.style.width = '343px';
    set('uploads', uploads);
    set('attachments', [
      attachment({ id: 'long', name: `Quarterly launch plan ${'with a very long name '.repeat(6)}.pdf`, state: 'uploading', progress: 0.3, file: null }),
      attachment(),
      attachment({
        id: 'failed',
        name: 'Notes-without-any-spaces-in-the-name-at-all-whatsoever.txt',
        state: 'failed',
        file: null,
        error: { code: 'assistant-file-type-not-accepted', messageKey: 'nh-assistant.errors.assistant-file-type-not-accepted' }
      })
    ]);

    const host = element.getBoundingClientRect();
    expect(element.scrollWidth).toBeLessThanOrEqual(element.clientWidth);
    // The long name is cut off with an ellipsis instead of widening the composer.
    const name = element.querySelector<HTMLElement>('.attachment-name')!;
    expect(name.scrollWidth).toBeGreaterThan(name.clientWidth);
    for (const item of Array.from(element.querySelectorAll<HTMLElement>('li.attachment, button.attach, button.send'))) {
      expect(item.getBoundingClientRect().right).withContext(item.className).toBeLessThanOrEqual(host.right + 0.5);
    }
  });

  it('removes an attachment with its own labelled button and keeps the keyboard in the composer', async () => {
    set('uploads', uploads);
    set('attachments', [attachment()]);
    const removed: string[] = [];
    fixture.componentInstance.removeAttachment.subscribe(id => removed.push(id));
    const remove = element.querySelector<HTMLButtonElement>('button.attachment-remove')!;

    expect(remove.getAttribute('aria-label')).toBe('Remove Plan.pdf');
    remove.focus();
    remove.click();
    await Promise.resolve();

    expect(removed).toEqual(['plan']);
    expect(document.activeElement).toBe(attachButton());
  });
});

/**
 * Prepared drafts: a host's draft shows in the input without being sent and gives way to what the
 * person typed, while the person's own refused message is put back as it was.
 */
describe('NhAssistantComposerComponent drafts', () => {
  let fixture: ComponentFixture<NhAssistantComposerComponent>;
  let element: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => null })
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    fixture = TestBed.createComponent(NhAssistantComposerComponent);
    element = fixture.nativeElement;
    fixture.detectChanges();
  });

  it('shows a prepared draft without sending it and keeps what the person typed', () => {
    const sent: string[] = [];
    let handled = 0;
    fixture.componentInstance.send.subscribe(text => sent.push(text));
    fixture.componentInstance.restored.subscribe(() => handled++);
    const textarea = element.querySelector('textarea')!;

    fixture.componentRef.setInput('restore', { text: 'Write a status brief on Alpha.', replace: false });
    fixture.detectChanges();
    expect(textarea.value).toBe('Write a status brief on Alpha.');
    // A draft the person did not change gives way to the next one.
    fixture.componentRef.setInput('restore', { text: 'Write a status brief on Gamma.', replace: false });
    fixture.detectChanges();
    expect(textarea.value).toBe('Write a status brief on Gamma.');

    textarea.value = 'My own question';
    textarea.dispatchEvent(new Event('input'));
    fixture.componentRef.setInput('restore', { text: 'Another brief', replace: false });
    fixture.detectChanges();
    expect(textarea.value).toBe('My own question');

    fixture.componentRef.setInput('restore', { text: 'The brief that replaces it', replace: true });
    fixture.detectChanges();
    expect(textarea.value).toBe('The brief that replaces it');
    // Every draft was handled, also the one that kept the person's text, and none was sent.
    expect(handled).toBe(4);
    expect(sent).toEqual([]);
  });

  it('keeps a message the server did not accept as the person\'s own text', () => {
    const textarea = element.querySelector('textarea')!;

    fixture.componentRef.setInput('restore', 'My refused question');
    fixture.detectChanges();
    fixture.componentRef.setInput('restore', { text: 'A prepared brief', replace: false });
    fixture.detectChanges();

    expect(textarea.value).toBe('My refused question');
  });
});

/**
 * The unsent message (FB41): with `unsent` bound, the host keeps what the input holds, so the text
 * outlives the composer, and the composer shows what the host kept whenever it changes.
 */
describe('NhAssistantComposerComponent unsent message', () => {
  let fixture: ComponentFixture<NhAssistantComposerComponent>;
  let element: HTMLElement;
  let events: string[];

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => null })
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    fixture = TestBed.createComponent(NhAssistantComposerComponent);
    element = fixture.nativeElement;
    events = [];
    fixture.componentInstance.unsentChange.subscribe(message => events.push(message === null
      ? 'unsent: none'
      : `unsent: ${message.text}${message.prepared ? ' (prepared)' : ''}`));
    fixture.componentInstance.send.subscribe(text => events.push(`send: ${text}`));
    fixture.componentRef.setInput('unsent', null);
    fixture.detectChanges();
  });

  function textarea(): HTMLTextAreaElement {
    return element.querySelector('textarea')!;
  }

  function type(text: string): void {
    textarea().value = text;
    textarea().dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('shows the kept message, reports every change, and reports none before it sends', () => {
    fixture.componentRef.setInput('unsent', { text: 'Half a question ', prepared: false });
    fixture.detectChanges();
    expect(textarea().value).toBe('Half a question ');

    type('Half a question about Alpha');
    textarea().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));
    fixture.detectChanges();

    // The host forgets the text before it learns of the message, which may come back refused.
    expect(events).toEqual(['unsent: Half a question about Alpha', 'unsent: none', 'send: Half a question about Alpha']);
    expect(textarea().value).toBe('');
  });

  it('empties the input after a message typed and sent between two renders', () => {
    // Fast automated typing sends before the input rendered what was typed; the input still empties.
    textarea().value = 'Typed and sent at once';
    textarea().dispatchEvent(new Event('input'));
    textarea().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));
    fixture.detectChanges();

    expect(events).toEqual(['unsent: Typed and sent at once', 'unsent: none', 'send: Typed and sent at once']);
    expect(textarea().value).toBe('');
  });

  it('shows a replacing draft over text typed since the last render', () => {
    fixture.componentRef.setInput('restore', { text: 'A brief', replace: false });
    fixture.detectChanges();
    textarea().value = 'A brief, longer';
    textarea().dispatchEvent(new Event('input'));
    fixture.componentRef.setInput('restore', { text: 'A brief', replace: true });
    fixture.detectChanges();

    expect(textarea().value).toBe('A brief');
  });

  it('shows another conversation\'s message, or an empty input, when the host keeps another one', () => {
    fixture.componentRef.setInput('unsent', { text: 'About planning', prepared: false });
    fixture.detectChanges();
    fixture.componentRef.setInput('unsent', null);
    fixture.detectChanges();
    expect(textarea().value).toBe('');

    fixture.componentRef.setInput('unsent', { text: 'About the budget', prepared: false });
    fixture.detectChanges();
    expect(textarea().value).toBe('About the budget');
    // Showing what the host kept is no change to report.
    expect(events).toEqual([]);
  });

  it('decides a draft against the kept message that arrives with it', () => {
    // The person's own text stays; the draft is handled and dropped.
    fixture.componentRef.setInput('unsent', { text: 'My own question', prepared: false });
    fixture.componentRef.setInput('restore', { text: 'A brief', replace: false });
    fixture.detectChanges();
    expect(textarea().value).toBe('My own question');

    // A kept draft the person did not change gives way to the next one, which the host then keeps.
    fixture.componentRef.setInput('unsent', { text: 'A brief', prepared: true });
    fixture.componentRef.setInput('restore', { text: 'The next brief', replace: false });
    fixture.detectChanges();
    expect(textarea().value).toBe('The next brief');
    expect(events).toContain('unsent: The next brief (prepared)');

    // A new chat's empty composer takes the draft.
    fixture.componentRef.setInput('unsent', null);
    fixture.componentRef.setInput('restore', { text: 'A brief for the new chat', replace: false });
    fixture.detectChanges();
    expect(textarea().value).toBe('A brief for the new chat');

    // Changed by the person, the draft is their own text.
    type('A brief for the new chat, shorter');
    expect(events[events.length - 1]).toBe('unsent: A brief for the new chat, shorter');
  });

  it('reports a refused message it puts back as the person\'s own text', () => {
    fixture.componentRef.setInput('restore', 'My refused question');
    fixture.detectChanges();

    expect(textarea().value).toBe('My refused question');
    expect(events).toEqual(['unsent: My refused question']);
  });
});
