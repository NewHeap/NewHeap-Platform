import { TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import {
  NH_ASSISTANT_ADMIN_TRANSLATIONS,
  startNhAssistantAdminTranslations
} from '../../../admin/src/i18n/nh-assistant-admin-translations';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NH_ASSISTANT_ERROR_CODES } from './nh-assistant-error-codes';
import { NH_ASSISTANT_TRANSLATIONS } from './nh-assistant-translations';

function flatten(value: Record<string, unknown>, prefix = ''): Map<string, unknown> {
  const result = new Map<string, unknown>();
  for (const [key, item] of Object.entries(value)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (item && typeof item === 'object' && !Array.isArray(item)) {
      for (const [nestedKey, nestedValue] of flatten(item as Record<string, unknown>, path)) {
        result.set(nestedKey, nestedValue);
      }
    } else {
      result.set(path, item);
    }
  }
  return result;
}

describe('NH_ASSISTANT_TRANSLATIONS', () => {
  it('has identical, non-empty key sets for en and nl under the nh-assistant prefix', () => {
    const english = flatten(NH_ASSISTANT_TRANSLATIONS.en);
    const dutch = flatten(NH_ASSISTANT_TRANSLATIONS.nl);

    expect([...english.keys()].sort()).toEqual([...dutch.keys()].sort());
    for (const [key, value] of [...english, ...dutch]) {
      expect(key.startsWith('nh-assistant.')).withContext(key).toBeTrue();
      expect(typeof value === 'string' && value.trim().length > 0).withContext(key).toBeTrue();
    }
  });

  it('covers every contract failure code of the API bridge', () => {
    const english = flatten(NH_ASSISTANT_TRANSLATIONS.en);
    for (const code of ['validation', 'unauthenticated', 'forbidden', 'not-found', 'conflict', 'upstream', 'timeout']) {
      expect(english.has(`nh-assistant.result-codes.api-bridge-${code}`)).withContext(code).toBeTrue();
    }
  });

  it('leaves the administration texts to the admin entry point, so they do not load with the panel', () => {
    for (const language of ['en', 'nl'] as const) {
      const keys = [...flatten(NH_ASSISTANT_TRANSLATIONS[language]).keys()];

      expect(keys.filter(key => key.startsWith('nh-assistant.admin.'))).withContext(language).toEqual([]);
    }
  });
});

describe('NH_ASSISTANT_ADMIN_TRANSLATIONS', () => {
  it('has identical, non-empty key sets for en and nl under the nh-assistant.admin prefix', () => {
    const english = flatten(NH_ASSISTANT_ADMIN_TRANSLATIONS.en);
    const dutch = flatten(NH_ASSISTANT_ADMIN_TRANSLATIONS.nl);

    expect(english.size).toBeGreaterThan(0);
    expect([...english.keys()].sort()).toEqual([...dutch.keys()].sort());
    for (const [key, value] of [...english, ...dutch]) {
      expect(key.startsWith('nh-assistant.admin.')).withContext(key).toBeTrue();
      expect(typeof value === 'string' && value.trim().length > 0).withContext(key).toBeTrue();
    }
  });
});

describe('error translations', () => {
  it('translates every assistant failure code in en and nl', () => {
    for (const language of ['en', 'nl'] as const) {
      const keys = flatten(NH_ASSISTANT_TRANSLATIONS[language]);
      for (const code of NH_ASSISTANT_ERROR_CODES) {
        expect(keys.has(`nh-assistant.errors.${code}`)).withContext(`${language}: ${code}`).toBeTrue();
      }
    }
  });

  it('covers the admin codes of contract 12.8 and the chat codes of the back-end', () => {
    const required = [
      'assistant-validation', 'assistant-instructions-too-long', 'assistant-mcp-host-blocked', 'assistant-forbidden',
      'assistant-not-found', 'assistant-context-not-found', 'assistant-mcp-server-not-found', 'assistant-mcp-tool-not-found',
      'assistant-version-conflict', 'assistant-agent-exists', 'assistant-mcp-server-exists', 'assistant-code-agent-not-deletable',
      'assistant-agent-not-code', 'assistant-mcp-unreachable', 'assistant-mcp-unauthorized',
      'assistant-actor-mismatch', 'assistant-agent-forbidden', 'assistant-agent-not-found', 'assistant-approval-decision-invalid',
      'assistant-approval-expired', 'assistant-approval-invalid', 'assistant-approval-not-found', 'assistant-approval-not-pending',
      'assistant-approval-rejected', 'assistant-budget-exhausted', 'assistant-context-unavailable',
      'assistant-conversation-not-found', 'assistant-disabled', 'assistant-message-duplicate', 'assistant-message-invalid',
      'assistant-message-too-long', 'assistant-model-unavailable', 'assistant-proposal-hash-mismatch', 'assistant-title-invalid',
      'assistant-tool-call-limit-reached', 'assistant-tools-disabled', 'assistant-turn-failed', 'assistant-turn-timeout'
    ];

    expect(required.filter(code => !NH_ASSISTANT_ERROR_CODES.includes(code))).toEqual([]);
  });

  it('explains the codes of consultations defined as data in en and nl', () => {
    // The problems of kind consultation, as the server lists them, and the field error of a value
    // the ceiling in code does not allow.
    const problems = [
      'unknown-agent', 'invalid', 'not-worked-on', 'unknown-subject-type', 'unknown-agent-policy', 'conflicts-with-code',
      'conflicts-with-tool', 'not-consultable', 'route-not-allowed', 'agent-answers-to-person', 'agent-has-no-rights',
      'no-approver', 'unknown-policy', 'limit-above-ceiling', 'requests-not-allowed', 'mcp-not-allowed'
    ];
    for (const language of ['en', 'nl'] as const) {
      const keys = new Map([
        ...flatten(NH_ASSISTANT_TRANSLATIONS[language]),
        ...flatten(NH_ASSISTANT_ADMIN_TRANSLATIONS[language])
      ]);
      const missing = [
        'nh-assistant.admin.run-config.problems.kind.consultation',
        'nh-assistant.admin.run-config.field-errors.not-allowed',
        ...problems.map(code => `nh-assistant.admin.run-config.problems.code.${code}`),
        ...['assistant-consultation-in-code', 'assistant-consultation-not-found', 'assistant-consultations-not-allowed']
          .map(code => `nh-assistant.errors.${code}`)
      ].filter(key => !keys.has(key));
      expect(missing).withContext(language).toEqual([]);
    }
    expect(NH_ASSISTANT_ERROR_CODES).toEqual(jasmine.arrayContaining([
      'assistant-consultation-in-code', 'assistant-consultation-not-found', 'assistant-consultations-not-allowed'
    ]));
  });

  it('has no error text without a known code', () => {
    const texts = [...flatten(NH_ASSISTANT_TRANSLATIONS.en).keys()]
      .filter(key => key.startsWith('nh-assistant.errors.') && key !== 'nh-assistant.errors.generic')
      .map(key => key.slice('nh-assistant.errors.'.length));

    expect(texts.filter(code => !NH_ASSISTANT_ERROR_CODES.includes(code))).toEqual([]);
  });
});

const NH_ASSISTANT_DUTCH_CLOSE = (NH_ASSISTANT_TRANSLATIONS.nl as { 'nh-assistant': { panel: { close: string } } })['nh-assistant'].panel.close;
const NH_ASSISTANT_DUTCH_CONTEXT_TITLE = (
  NH_ASSISTANT_ADMIN_TRANSLATIONS.nl as { 'nh-assistant': { admin: { context: { title: string } } } }
)['nh-assistant'].admin.context.title;

describe('bundled translations', () => {
  function setup(translations?: 'bundled' | 'host'): TranslateService {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => null, translations })
      ]
    });
    return TestBed.inject(TranslateService);
  }

  it('merges into languages the host loads without replacing host keys', async () => {
    const translate = setup();

    await firstValueFrom(translate.use('nl'));
    translate.setTranslation('nl', { host: { title: 'Host' } }, true);

    expect(translate.instant('nh-assistant.panel.title')).toBe('Assistent');
    expect(translate.instant('host.title')).toBe('Host');
  });

  it('re-merges after a host loader replaces a language', async () => {
    const translate = setup();
    await firstValueFrom(translate.use('en'));

    translate.setTranslation('en', { host: { title: 'Replaced' } });

    expect(translate.instant('nh-assistant.panel.title')).toBe('Assistant');
  });

  it('keeps host keys under nh-assistant and lets host overrides win', async () => {
    const translate = setup();
    await firstValueFrom(translate.use('en'));

    translate.setTranslation('en', {
      'nh-assistant': { agents: { projects: { name: 'Project assistant' } }, panel: { title: 'Helper' } }
    });

    expect(translate.instant('nh-assistant.agents.projects.name')).toBe('Project assistant');
    expect(translate.instant('nh-assistant.panel.title')).toBe('Helper');
    expect(translate.instant('nh-assistant.panel.close')).toBe('Close assistant');
  });

  it('gives a regional language the texts of its base language, and others English', async () => {
    const translate = setup();

    await firstValueFrom(translate.use('nl-NL'));
    expect(translate.instant('nh-assistant.panel.title')).toBe('Assistent');

    await firstValueFrom(translate.use('nl_BE'));
    expect(translate.instant('nh-assistant.panel.title')).toBe('Assistent');

    await firstValueFrom(translate.use('en-GB'));
    expect(translate.instant('nh-assistant.panel.title')).toBe('Assistant');

    await firstValueFrom(translate.use('de-DE'));
    expect(translate.instant('nh-assistant.panel.title')).toBe('Assistant');
  });

  it('keeps host keys of a regional language', async () => {
    const translate = setup();
    await firstValueFrom(translate.use('nl-NL'));

    translate.setTranslation('nl-NL', { 'nh-assistant': { panel: { title: 'Hulp' } } });

    expect(translate.instant('nh-assistant.panel.title')).toBe('Hulp');
    expect(translate.instant('nh-assistant.panel.close')).toBe(NH_ASSISTANT_DUTCH_CLOSE);
  });

  it('leaves translations to the host in host mode', async () => {
    const translate = setup('host');
    await firstValueFrom(translate.use('en'));

    expect(translate.instant('nh-assistant.panel.title')).toBe('nh-assistant.panel.title');
  });

  it('adds the administration texts once an admin component starts them, and lets host keys win', async () => {
    const translate = setup();
    await firstValueFrom(translate.use('nl'));
    translate.setTranslation('nl', { 'nh-assistant': { admin: { title: 'Beheer' } } }, true);

    expect(translate.instant('nh-assistant.admin.context.title')).toBe('nh-assistant.admin.context.title');

    TestBed.runInInjectionContext(() => startNhAssistantAdminTranslations());

    expect(translate.instant('nh-assistant.admin.context.title')).toBe(NH_ASSISTANT_DUTCH_CONTEXT_TITLE);
    expect(translate.instant('nh-assistant.admin.title')).toBe('Beheer');
    expect(translate.instant('nh-assistant.panel.title')).toBe('Assistent');

    await firstValueFrom(translate.use('en-GB'));
    expect(translate.instant('nh-assistant.admin.context.title')).toBe('Application context');
  });

  it('leaves the administration texts to the host in host mode', async () => {
    const translate = setup('host');
    await firstValueFrom(translate.use('en'));

    TestBed.runInInjectionContext(() => startNhAssistantAdminTranslations());

    expect(translate.instant('nh-assistant.admin.context.title')).toBe('nh-assistant.admin.context.title');
  });
});
