import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { NH_ASSISTANT_FETCH, NhAssistantFetch } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from './nh-assistant-admin-api.service';
import { NhAssistantApiError } from './nh-assistant-api.service';

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });
}

/**
 * The rule script methods of `NhAssistantAdminApiService` (RB-1 to RB-6): the routes of an agent's own rules
 * and of one skill, the bodies, the catalog's writes as a query, the latest dry run that answers 204 when
 * there is none, and the codes of refusals.
 */
describe('NhAssistantAdminApiService rule scripts', () => {
  let fetchSpy: jasmine.Spy<NhAssistantFetch>;
  let admin: NhAssistantAdminApiService;

  beforeEach(() => {
    fetchSpy = jasmine.createSpy<NhAssistantFetch>('fetch');
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'admin-token' }),
        { provide: NH_ASSISTANT_FETCH, useFactory: () => fetchSpy }
      ]
    });
    admin = TestBed.inject(NhAssistantAdminApiService);
  });

  function lastCall(): { url: string; method: string; body: unknown } {
    const [url, init] = fetchSpy.calls.mostRecent().args;
    return {
      url,
      method: init.method ?? 'GET',
      body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined
    };
  }

  it('calls every rule script endpoint of an agent and of a skill with the documented method, path and body', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse({})));
    const agent = 'admin/agents/document%20check/rule-script';
    const skill = 'admin/agents/document%20check/skills/follow%20up/rule-script';
    const calls: [() => Promise<unknown>, string, string, unknown][] = [
      [() => firstValueFrom(admin.getRuleScript('document check', null)), 'GET', agent, undefined],
      [() => firstValueFrom(admin.getRuleScript('document check', 'follow up')), 'GET', skill, undefined],
      [() => firstValueFrom(admin.getRuleScriptCatalog('document check', null)), 'GET', `${agent}/catalog`, undefined],
      [() => firstValueFrom(admin.getRuleScriptCatalog('document check', null, ['projects.request-review', 'tasks.complete'])), 'GET', `${agent}/catalog?writes=projects.request-review%2Ctasks.complete`, undefined],
      [() => firstValueFrom(admin.getRuleScriptCatalog('document check', 'follow up', [])), 'GET', `${skill}/catalog?writes=`, undefined],
      [() => firstValueFrom(admin.checkRuleScript('document check', null, 'done("x");')), 'POST', `${agent}/check`, { script: 'done("x");' }],
      [
        () => firstValueFrom(admin.saveRuleScriptVersion('document check', 'follow up', { script: 'done("x");', writes: ['projects.request-review'], expectedRevision: 3, reason: 'Why', shadow: true })),
        'POST',
        `${skill}/versions`,
        { script: 'done("x");', writes: ['projects.request-review'], expectedRevision: 3, reason: 'Why', shadow: true }
      ],
      [
        () => firstValueFrom(admin.dryRunRuleScriptVersion('document check', null, 2, { subjects: [{ type: 'project', id: 'PRJ-ALPHA' }] })),
        'POST',
        `${agent}/versions/2/dry-run`,
        { subjects: [{ type: 'project', id: 'PRJ-ALPHA' }] }
      ],
      [() => firstValueFrom(admin.getRuleScriptDryRun('document check', 'follow up', 2)), 'GET', `${skill}/versions/2/dry-run`, undefined],
      [() => firstValueFrom(admin.getRuleScriptShadowRuns('document check', null, 2)), 'GET', `${agent}/versions/2/shadow-runs`, undefined],
      [() => firstValueFrom(admin.setRuleScriptVersionLive('document check', null, 2, { expectedRevision: 4, reason: 'Checked' })), 'POST', `${agent}/versions/2/live`, { expectedRevision: 4, reason: 'Checked' }],
      [() => firstValueFrom(admin.setRuleScriptVersionShadow('document check', 'follow up', 2, { expectedRevision: 4, shadow: false })), 'POST', `${skill}/versions/2/shadow`, { expectedRevision: 4, shadow: false }],
      [() => firstValueFrom(admin.discardRuleScriptVersion('document check', null, 2, { expectedRevision: 4 })), 'POST', `${agent}/versions/2/discard`, { expectedRevision: 4 }],
      [() => firstValueFrom(admin.compareRuleScriptVersions('document check', null, 3)), 'GET', `${agent}/compare?version=3`, undefined],
      [() => firstValueFrom(admin.compareRuleScriptVersions('document check', 'follow up', 3, 1)), 'GET', `${skill}/compare?version=3&with=1`, undefined]
    ];

    for (const [invoke, method, path, body] of calls) {
      await invoke();
      const call = lastCall();
      expect(`${call.method} ${call.url}`).withContext(path).toBe(`${method} /api/assistant/${path}`);
      expect(call.body).withContext(path).toEqual(body);
    }
  });

  it('answers null for a version without a dry run, as the server answers 204', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(new Response(null, { status: 204 })));

    const record = await firstValueFrom(admin.getRuleScriptDryRun('document-check', null, 1));

    expect(record).toBeNull();
  });

  it('keeps the code and the field errors of a refused save', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse({
      code: 'assistant-rule-script-write-not-allowed',
      messageKey: 'nh-assistant.errors.assistant-rule-script-write-not-allowed',
      errors: { writes: ['not-allowed'] }
    }, 400)));

    const error = await firstValueFrom(admin.saveRuleScriptVersion('document-check', null, { script: 'done("x");', writes: ['projects.delete'], expectedRevision: 0 }))
      .catch((failure: unknown) => failure);

    expect(error).toEqual(jasmine.any(NhAssistantApiError));
    expect((error as NhAssistantApiError).code).toBe('assistant-rule-script-write-not-allowed');
    expect((error as NhAssistantApiError).fieldErrors).toEqual({ writes: ['not-allowed'] });
  });

  it('keeps four eyes apart from a stale revision, which both answer 409', async () => {
    fetchSpy.and.returnValues(
      Promise.resolve(jsonResponse({ code: 'assistant-rule-script-four-eyes', messageKey: 'nh-assistant.errors.assistant-rule-script-four-eyes' }, 409)),
      Promise.resolve(new Response(null, { status: 409 })));

    const fourEyes = await firstValueFrom(admin.setRuleScriptVersionLive('document-check', null, 2, { expectedRevision: 3 })).catch((failure: unknown) => failure);
    const stale = await firstValueFrom(admin.setRuleScriptVersionLive('document-check', null, 2, { expectedRevision: 2 })).catch((failure: unknown) => failure);

    expect((fourEyes as NhAssistantApiError).code).toBe('assistant-rule-script-four-eyes');
    expect((stale as NhAssistantApiError).code).toBe('assistant-version-conflict');
  });
});
