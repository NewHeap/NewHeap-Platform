import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { Subject, firstValueFrom } from 'rxjs';
import { NhAssistantApiService, NhAssistantUploadEvent } from '../services/nh-assistant-api.service';
import { NhAssistantScreenshotComponent } from '../components/screenshot/nh-assistant-screenshot.component';
import { NhAssistantScreenshotCapture } from '../components/screenshot/nh-assistant-screenshot-capture';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantStore } from '../services/nh-assistant.store';

describe('Consent-first page screenshots', () => {
  let fixture: ComponentFixture<NhAssistantScreenshotComponent>;
  let store: NhAssistantStore;
  let backend: NhAssistantMockBackend;
  let capture: jasmine.Spy<NhAssistantScreenshotCapture['capture']>;
  const image = () => new File([Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7GkAAAAASUVORK5CYII='), character => character.charCodeAt(0))], 'page-screenshot.png', { type: 'image/png' });

  async function setup(enabled = true, png = true): Promise<void> {
    TestBed.configureTestingModule({ providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideRouter([{ path: 'another-page', children: [] }]),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', screenshots: enabled }),
      provideNhAssistantMockApi({
        agents: [{ id: 'projects', version: 1, displayNameKey: 'Projects', descriptionKey: 'Projects', canMutate: false,
          uploads: { acceptedMediaTypes: png ? ['image/png'] : ['application/pdf'], maxFiles: 3, maxFileBytes: 1024 } }],
        timing: { firstEventDelayMs: 0, eventDelayMs: 0 },
        turns: [
          { match: 'request-screen', steps: [
            { screenshot: { reason: 'Seeing the controls would help explain this page.' } },
            { text: 'Please review the request.' }
          ] },
          { steps: [{ text: 'Image received.' }] }
        ]
      })
    ] });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    store = TestBed.inject(NhAssistantStore);
    backend = TestBed.inject(NhAssistantMockBackend);
    await store.initialize();
    capture = spyOn(TestBed.inject(NhAssistantScreenshotCapture), 'capture').and.resolveTo(image());
    fixture = TestBed.createComponent(NhAssistantScreenshotComponent);
    fixture.detectChanges();
  }

  function uploads(): number {
    return backend.requests.filter(request => request.path.endsWith('/files')).length;
  }

  async function finished(): Promise<void> {
    const deadline = performance.now() + 10_000;
    while (store.activeConversation()?.status === 'running' && performance.now() < deadline) {
      await new Promise(resolve => setTimeout(resolve, 0));
      fixture.detectChanges();
    }
    expect(store.activeConversation()?.status).toBe('idle');
  }

  it('opens an AI request without capturing, and never replays it when the panel is reopened', async () => {
    await setup();
    await store.submit('request-screen');
    await finished();
    const component = fixture.componentInstance;
    expect(component.phase()).toBe('consent');
    expect(component.reason()).toBe('Seeing the controls would help explain this page.');
    expect(capture).not.toHaveBeenCalled();
    expect(uploads()).toBe(0);
    expect(backend.requests.find(request => request.path.endsWith('/messages'))?.body)
      .toEqual(jasmine.objectContaining({ pageScreenshotAvailable: true }));
    component.close();
    fixture.destroy();
    fixture = TestBed.createComponent(NhAssistantScreenshotComponent);
    fixture.detectChanges();
    expect(fixture.componentInstance.phase()).toBe('closed');
    expect(capture).not.toHaveBeenCalled();
  });

  it('ignores AI requests and does not advertise capture when the host opted out', async () => {
    await setup(false);
    await store.submit('request-screen');
    await finished();
    expect(fixture.componentInstance.phase()).toBe('closed');
    expect(capture).not.toHaveBeenCalled();
    expect(uploads()).toBe(0);
    expect(backend.requests.find(request => request.path.endsWith('/messages'))?.body)
      .not.toEqual(jasmine.objectContaining({ pageScreenshotAvailable: true }));
  });

  async function uploaded(): Promise<void> {
    const deadline = performance.now() + 10_000;
    while (store.attachments()[0]?.state === 'uploading' && performance.now() < deadline) {
      await new Promise(resolve => setTimeout(resolve, 0));
    }
    expect(store.attachments()[0]?.state).toBe('uploaded');
  }

  it('captures only after consent, previews locally, then uploads and sends through the existing file flow', async () => {
    await setup();
    const component = fixture.componentInstance;
    (fixture.nativeElement.querySelector('.trigger') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(component.phase()).toBe('consent');
    expect(fixture.nativeElement.querySelector('dialog').open).toBeTrue();
    expect(capture).not.toHaveBeenCalled();
    expect(uploads()).toBe(0);

    let finish!: (file: File) => void;
    let capturing!: () => void;
    capture.and.callFake(onCapture => {
      capturing = onCapture;
      return new Promise(resolve => { finish = resolve; });
    });
    const taking = component.take();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="status"]').textContent).toContain('Preparing');
    expect(fixture.nativeElement.querySelector('.primary')).toBeNull();
    await component.take();
    expect(capture).toHaveBeenCalledTimes(1);
    capturing();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="status"]').textContent).toContain('Creating');
    finish(image());
    await taking;
    fixture.detectChanges();
    expect(component.phase()).toBe('preview');
    expect(fixture.nativeElement.querySelector('img').src).toMatch(/^blob:/);
    expect(uploads()).toBe(0);

    const revoke = spyOn(URL, 'revokeObjectURL').and.callThrough();
    component.share();
    await new Promise(resolve => setTimeout(resolve, 0));
    await uploaded();
    expect(revoke).toHaveBeenCalledTimes(1);
    expect(uploads()).toBe(1);
    expect(component.preview()).toBeNull();
    expect(backend.requests.some(request => request.path.endsWith('/messages'))).toBeFalse();
    const files = store.attachedFiles();
    expect(await store.submit('Explain this screenshot.', files)).toBeTrue();
    expect(backend.requests.find(request => request.path.endsWith('/messages'))?.body).toEqual(jasmine.objectContaining({ files }));
  });

  async function followUp(): Promise<void> {
    const deadline = performance.now() + 10_000;
    while ((backend.requests.filter(request => request.path.endsWith('/messages')).length < 2
      || store.activeConversation()?.status !== 'idle') && performance.now() < deadline) {
      fixture.detectChanges();
      await new Promise(resolve => setTimeout(resolve, 0));
    }
    expect(backend.requests.filter(request => request.path.endsWith('/messages')).length).toBe(2);
    expect(store.activeConversation()?.status).toBe('idle');
  }

  it('sends an approved AI-requested screenshot once, preserving draft text and unrelated files', async () => {
    await setup();
    await store.submit('request-screen');
    await finished();
    await store.attach([image()]);
    await uploaded();
    const other = store.attachedFiles()[0];
    store.keepUnsentMessage({ text: 'Not ready to send this draft.', prepared: false });
    const component = fixture.componentInstance;
    await component.take();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.primary').textContent).toContain('Send to assistant');
    component.share();
    component.share();
    await followUp();
    const body = backend.requests.filter(request => request.path.endsWith('/messages'))[1].body as { text: string; files: { id: string }[] };
    expect(body.text).toBe('Here is the requested screenshot of the current page. Please continue.');
    expect(body.files.length).toBe(1);
    expect(body.files[0].id).not.toBe(other.id);
    expect(store.attachedFiles()).toEqual([other]);
    expect(store.unsentMessage()?.text).toBe('Not ready to send this draft.');
  });

  it('waits for upload completion before automatically sending the approved image', async () => {
    await setup();
    await store.submit('request-screen');
    await finished();
    const upload = new Subject<NhAssistantUploadEvent>();
    spyOn(TestBed.inject(NhAssistantApiService), 'uploadFile').and.returnValue(upload);
    await fixture.componentInstance.take();
    fixture.componentInstance.share();
    fixture.detectChanges();
    expect(backend.requests.filter(request => request.path.endsWith('/messages')).length).toBe(1);
    const file = { id: 'approved-page', name: 'page.png', mediaType: 'image/png', size: 100 };
    upload.next({ type: 'uploaded', file });
    upload.complete();
    await followUp();
    expect(backend.requests.filter(request => request.path.endsWith('/messages'))[1].body)
      .toEqual(jasmine.objectContaining({ files: [file] }));
  });

  for (const interruption of ['navigation', 'conversation', 'failure', 'destroy'] as const) {
    it('does not automatically send a pending screenshot after ' + interruption, async () => {
      await setup();
      await store.submit('request-screen');
      await finished();
      const upload = new Subject<NhAssistantUploadEvent>();
      spyOn(TestBed.inject(NhAssistantApiService), 'uploadFile').and.returnValue(upload);
      const submit = spyOn(store, 'submit').and.callThrough();
      await fixture.componentInstance.take();
      fixture.componentInstance.share();
      if (interruption === 'navigation') {
        await TestBed.inject(Router).navigateByUrl('/another-page');
      } else if (interruption === 'conversation') {
        store.startNewConversation();
      } else if (interruption === 'failure') {
        upload.error(new Error('Upload failed'));
      } else {
        fixture.destroy();
      }
      if (interruption !== 'destroy') {
        fixture.detectChanges();
      }
      upload.next({ type: 'uploaded', file: { id: 'late-page', name: 'page.png', mediaType: 'image/png', size: 100 } });
      upload.complete();
      if (interruption !== 'destroy') {
        fixture.detectChanges();
      }
      expect(submit).not.toHaveBeenCalled();
    });
  }

  it('discards a cancelled capture, ignores its late completion and asks for consent again', async () => {
    await setup();
    let finish!: (file: File) => void;
    capture.and.returnValue(new Promise(resolve => { finish = resolve; }));
    const component = fixture.componentInstance;
    component.open();
    const taking = component.take();
    component.close();
    finish(image());
    await taking;
    expect(component.phase()).toBe('closed');
    expect(component.preview()).toBeNull();
    expect(uploads()).toBe(0);
    component.open();
    expect(component.phase()).toBe('consent');
  });

  it('clears the preview on navigation and conversation changes', async () => {
    await setup();
    const component = fixture.componentInstance;
    component.open();
    await component.take();
    await TestBed.inject(Router).navigateByUrl('/another-page');
    expect(component.preview()).toBeNull();
    expect(component.phase()).toBe('closed');
    component.open();
    await component.take();
    await store.submit('A new conversation');
    fixture.detectChanges();
    expect(component.phase()).toBe('closed');
    expect(uploads()).toBe(0);
  });

  it('offers retry after failure, refuses oversized images and never uploads either', async () => {
    await setup();
    const component = fixture.componentInstance;
    capture.and.rejectWith(new Error('A diagnostic that must not be shown'));
    component.open();
    await component.take();
    fixture.detectChanges();
    expect(component.phase()).toBe('error');
    expect(fixture.nativeElement.textContent).not.toContain('diagnostic');
    capture.and.resolveTo(new File(['x'.repeat(2048)], 'large.png', { type: 'image/png' }));
    await component.take();
    expect(component.error()).toBe('too-large');
    expect(uploads()).toBe(0);
    capture.and.resolveTo(image());
    await component.take();
    expect(component.phase()).toBe('preview');
    component.close();
    expect(uploads()).toBe(0);
  });

  it('hides capture when the host did not opt in', async () => {
    await setup(false);
    expect(fixture.nativeElement.querySelector('.trigger')).toBeNull();
    fixture.componentInstance.open();
    expect(fixture.componentInstance.phase()).toBe('closed');
  });

  it('hides capture when the agent cannot receive PNG', async () => {
    await setup(true, false);
    expect(fixture.nativeElement.querySelector('.trigger')).toBeNull();
  });
});

describe('Page screenshot renderer', () => {
  it('captures the viewport as PNG and excludes private content without changing the live page', async () => {
    const surface = document.createElement('div');
    surface.style.cssText = 'position:fixed;inset:0;background:linear-gradient(color(srgb 1 1 1), oklch(100% 0 0));color:color-mix(in srgb, black 80%, white);z-index:99999';
    surface.innerHTML = '<div data-nh-assistant-screenshot-exclude style="width:100px;height:100px;background:red">Private</div>'
      + '<input type="password" value="secret" style="background:red;width:100px;height:100px">';
    document.body.append(surface);
    try {
      const file = await TestBed.inject(NhAssistantScreenshotCapture).capture(() => undefined, new AbortController().signal);
      expect(file.type).toBe('image/png');
      const bitmap = await createImageBitmap(file);
      const scale = Math.min(1, 1920 / Math.max(innerWidth, innerHeight));
      expect(bitmap.width).toBe(Math.floor(innerWidth * scale));
      expect(bitmap.height).toBe(Math.floor(innerHeight * scale));
      const canvas = document.createElement('canvas');
      canvas.width = bitmap.width;
      canvas.height = bitmap.height;
      const context = canvas.getContext('2d')!;
      context.drawImage(bitmap, 0, 0);
      expect([...context.getImageData(10, 10, 1, 1).data]).toEqual([255, 255, 255, 255]);
      expect(surface.querySelector('input')!.value).toBe('secret');
      expect(surface.querySelector('div')!.style.visibility).toBe('');
      bitmap.close();
    } finally {
      surface.remove();
    }
  }, 20_000);
});
