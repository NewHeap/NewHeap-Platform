import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { of, Subject, throwError } from 'rxjs';
import { AgentArtifact } from '../../models/assistant-agent-run.models';
import { NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { NhAssistantLiveService } from '../../services/nh-assistant-live.service';
import { NH_ASSISTANT_AGENT_WORK_RELOAD_DELAY_MS } from '../../internal/nh-assistant-agent-work';
import { NhAssistantArtifactsComponent } from './nh-assistant-artifacts.component';

const report: AgentArtifact = { id: 'f1', name: 'Report.pdf', version: 1, contentType: 'application/pdf', sizeBytes: 6, createdAt: '2026-01-01T12:00:00Z', runId: null, restricted: false };

describe('NhAssistantArtifactsComponent', () => {
  let api: jasmine.SpyObj<NhAssistantApiService>;
  let fixture: ComponentFixture<NhAssistantArtifactsComponent>;
  let resync: Subject<void>;

  beforeEach(() => {
    api = jasmine.createSpyObj<NhAssistantApiService>('api', ['getArtifacts', 'downloadArtifact']);
    api.getArtifacts.and.returnValue(of([report]));
    api.downloadArtifact.and.resolveTo(new Blob(['report'], { type: 'application/pdf' }));
    resync = new Subject<void>();
    TestBed.configureTestingModule({ providers: [
      provideTranslateService(),
      { provide: NhAssistantApiService, useValue: api },
      { provide: NhAssistantLiveService, useValue: { agentWork$: new Subject(), resync$: resync } }
    ] });
    fixture = TestBed.createComponent(NhAssistantArtifactsComponent);
    fixture.componentRef.setInput('conversationId', 'c1');
    fixture.componentRef.setInput('compact', true);
  });

  it('renders supplied message files without fetching all conversation files on reconnect', fakeAsync(() => {
    fixture.componentRef.setInput('items', [report]);
    fixture.detectChanges();
    resync.next();
    tick(NH_ASSISTANT_AGENT_WORK_RELOAD_DELAY_MS);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Report.pdf');
    expect(api.getArtifacts).not.toHaveBeenCalled();
  }));

  it('loads on opening and after streaming, and cancels stale loads when switching chats', () => {
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    expect(api.getArtifacts).not.toHaveBeenCalled();
    fixture.componentRef.setInput('busy', false);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Report.pdf');
    const pending = new Subject<AgentArtifact[]>();
    api.getArtifacts.and.returnValue(pending);
    fixture.componentInstance.load();
    api.getArtifacts.and.returnValue(of([]));
    fixture.componentRef.setInput('conversationId', 'c2');
    fixture.detectChanges();
    pending.next([report]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('Report.pdf');
    expect(fixture.componentInstance.artifacts()).toEqual([]);
  });

  it('downloads the original filename, releases the blob and permits retry after failure', async () => {
    fixture.detectChanges();
    spyOn(URL, 'createObjectURL').and.returnValue('blob:report');
    const revoke = spyOn(URL, 'revokeObjectURL');
    let filename = '';
    spyOn(HTMLAnchorElement.prototype, 'click').and.callFake(function(this: HTMLAnchorElement) { filename = this.download; });
    api.downloadArtifact.and.rejectWith({ code: 'assistant-network' });
    await fixture.componentInstance.download(report);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-download-error]')).not.toBeNull();
    expect(fixture.componentInstance.downloadingId()).toBeNull();
    api.downloadArtifact.and.resolveTo(new Blob(['report']));
    await fixture.componentInstance.download(report);
    expect(api.downloadArtifact).toHaveBeenCalledWith('c1', 'f1');
    expect(filename).toBe('Report.pdf');
    expect(revoke).toHaveBeenCalledWith('blob:report');
    expect(fixture.componentInstance.downloadError()).toBeNull();
  });

  it('retries listing failures and does not download a restricted file', async () => {
    api.getArtifacts.and.returnValue(throwError(() => new Error('offline')));
    fixture.detectChanges();
    expect(fixture.componentInstance.loadFailed()).toBeTrue();
    api.getArtifacts.and.returnValue(of([{ ...report, restricted: true }]));
    fixture.nativeElement.querySelector('button').click();
    fixture.detectChanges();
    expect(fixture.componentInstance.loadFailed()).toBeFalse();
    expect(fixture.nativeElement.querySelector('button')).toBeNull();
    await fixture.componentInstance.download({ ...report, restricted: true });
    expect(api.downloadArtifact).not.toHaveBeenCalled();
  });
});
