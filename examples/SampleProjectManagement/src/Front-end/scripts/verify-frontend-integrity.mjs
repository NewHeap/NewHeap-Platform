import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const read = path => readFileSync(resolve(root, path), 'utf8');

const providers = read('projects/sample-project-management-common/src/lib/sample-project-management.providers.ts');
const authService = read('projects/sample-project-management-common/src/lib/sample-auth.service.ts');
const authSession = read('projects/sample-project-management-common/src/lib/sample-auth-session.service.ts');
const connectionState = read('projects/sample-project-management-common/src/lib/sample-api-connection-state.service.ts');
const projectApi = read('projects/sample-project-management-common/src/lib/project-api.service.ts');
const login = read('projects/sample-project-management-common/src/lib/sample-login.component.ts');
const management = read('projects/management/src/app/app.component.ts');
const workspace = read('projects/workspace/src/app/app.component.ts');
const managementTemplate = read('projects/management/src/app/app.component.html');
const workspaceTemplate = read('projects/workspace/src/app/app.component.html');
const managementIndex = read('projects/management/src/index.html');
const workspaceIndex = read('projects/workspace/src/index.html');
const mediaPlayground = read('projects/management/src/app/media-playground/media-playground.component.ts');
const pageLifecycleSample = read('projects/management/src/app/dirty-route-sample/dirty-route-sample.component.ts');

const flattenTranslations = (value, prefix = '', result = new Map()) => {
  for (const [key, item] of Object.entries(value)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (item && typeof item === 'object' && !Array.isArray(item)) {
      flattenTranslations(item, path, result);
    } else {
      result.set(path, item);
    }
  }
  return result;
};

assert.match(providers, /provideEnvironmentInitializer/,
  'The shared providers must eagerly start session expiration handling for both apps.');
assert.match(providers, /language:\s*'en'/,
  'The sample must start with English as its active language.');
assert.match(providers, /defaultLanguage:\s*'en'/,
  'The sample must use English as its fallback language.');
assert.match(providers, /supportedLanguages:\s*\['en',\s*'nl'\]/,
  'The sample must retain Dutch as an optional language alongside English.');
assert.match(providers, /culture:\s*'en-US'/,
  'The sample must start with an English culture.');
assert.match(providers, /defaultCulture:\s*'en-US'/,
  'The sample must use an English fallback culture.');
assert.match(managementIndex, /<html\s+lang="en">/,
  'The management document language must default to English.');
assert.match(workspaceIndex, /<html\s+lang="en">/,
  'The workspace document language must default to English.');
assert.match(mediaPlayground, /language\s*=\s*signal<'nl'\s*\|\s*'en'>\('en'\)/,
  'Language-specific executable examples must also start in English.');
assert.match(providers, /SampleAuthSessionService\)\.start\(\)/,
  'The session coordinator is not started by the root providers.');
assert.match(authSession, /sessionExpirationInformationChanged/,
  'The session coordinator must observe the NewHeap expiration stream.');
assert.match(authSession, /reason:\s*'session-expired'/,
  'Expired sessions must navigate to login with an explicit reason.');
assert.match(authService, /override clearAuthorization/,
  'The sample auth service must also clear authorization that has just expired.');
assert.match(authService, /localStorage\.removeItem\('at'\)/,
  'Expired browser token state must be removed.');
assert.match(login, /queryParamMap\.get\('reason'\) === 'session-expired'/,
  'The login page must explain an expiration redirect.');
assert.match(login, /!\/\[<>\]\/\.test\(item\)/,
  'The login page must never render raw HTML returned by a proxy or API.');
assert.match(login, /translate\.instant\('project\.login-failed'\)/,
  'The login fallback must be safe and localized.');

assert.match(connectionState, /error\.status === 0 \? 'offline' : 'connected'/,
  'Only a browser network failure may enable offline demo mode.');
assert.match(connectionState, /demoMode = computed\(\(\) => this\.mode\(\) === 'offline'\)/,
  'Local simulation must have a separate, explicit connection state.');
assert.match(projectApi, /return this\.updatePartial<void>\(id, \{ status \}\);/,
  'The shared project API service must exercise the NewHeap partial-update helper.');
assert.doesNotMatch(projectApi, /this\.apiService\.put<ProjectViewModel>\([\s\S]*?\/status/,
  'The preferred single-field sample must use PATCH rather than a custom PUT route.');

const detachedLoad = pageLifecycleSample.indexOf(
  'void this.loadProjectSummary().catch(error => this.handleProjectSummaryLoadError(error))'
);
const metadataAfterDetachedLoad = pageLifecycleSample.indexOf(
  "this.pageSettings.title = this.translateService.instant('project.page-lifecycle-title')"
);
assert.ok(detachedLoad >= 0,
  'The page lifecycle sample must explicitly detach independent work and observe failures.');
assert.ok(metadataAfterDetachedLoad > detachedLoad,
  'Independent page metadata must be allowed to continue after starting the detached load.');
assert.doesNotMatch(pageLifecycleSample, /await\s+this\.loadProjectSummary\(/,
  'Awaiting the independent sample load would serialize and slow the remaining page lifecycle.');
assert.match(pageLifecycleSample, /private handleProjectSummaryLoadError\(error: unknown\): void/,
  'A detached lifecycle task must expose explicit error handling.');
assert.match(pageLifecycleSample, /takeUntilDestroyed\(this\.destroyRef\)/,
  'A detached lifecycle request must stop safely when the component is destroyed.');

assert.match(management, /this\.projects\.set\(response\.items\)/,
  'A successful empty API response must replace demo data with an empty collection.');
assert.match(management, /select\.value = project\.status/,
  'A rejected management status change must restore the previous selection.');
assert.match(management, /bulk-update-failed/,
  'Bulk update failures must be reported without optimistic success.');
assert.match(management, /isOnePermissionGranted\(\['app\.project\.manage'\]\)/,
  'Management mutation controls must follow the same manage permission as the API.');
assert.match(managementTemplate, /\[selected\]="option\.id === project\.status"/,
  'Management status controls must render the value from the project model.');
assert.match(workspace, /select\.value = project\.status/,
  'A rejected workspace status change must restore the previous selection.');
assert.doesNotMatch(workspace, /error:\s*localUpdate/,
  'A failed API status update must never be applied locally as success.');
assert.match(workspace, /isOnePermissionGranted\(\['app\.project\.manage'\]\)/,
  'Workspace mutation controls must follow the same manage permission as the API.');
assert.match(workspaceTemplate, /\[selected\]="option\.id === project\.status"/,
  'Workspace status controls must render the value from the project model.');

for (const app of ['management', 'workspace']) {
  const translationsByLanguage = new Map();
  for (const language of ['nl', 'en']) {
    const translations = JSON.parse(read(
      `projects/${app}/public/i18n/${language}.json`
    ));
    translationsByLanguage.set(language, flattenTranslations(translations));
    assert.ok(translations.project['session-expired'],
      `${app}/${language} is missing the session-expired translation.`);
    assert.ok(translations.project['status-update-failed'],
      `${app}/${language} is missing the status failure translation.`);
    assert.ok(translations.project['login-failed'],
      `${app}/${language} is missing the safe login failure translation.`);
    assert.ok(translations.general?.search,
      `${app}/${language} is missing the NewHeap search control translation.`);
    assert.ok(translations.form?.['form-dropdown']?.['default-title'],
      `${app}/${language} is missing the NewHeap dropdown translations.`);
  }

  const english = translationsByLanguage.get('en');
  const dutch = translationsByLanguage.get('nl');
  const missingInEnglish = [...dutch.keys()].filter(key => !english.has(key));
  const missingInDutch = [...english.keys()].filter(key => !dutch.has(key));
  const emptyEnglish = [...english.entries()]
    .filter(([, value]) => typeof value !== 'string' || value.trim().length === 0)
    .map(([key]) => key);

  assert.deepEqual(missingInEnglish, [],
    `${app}/en must provide every sample translation key. Missing: ${missingInEnglish.join(', ')}`);
  assert.deepEqual(missingInDutch, [],
    `${app}/nl must stay aligned with English. Missing: ${missingInDutch.join(', ')}`);
  assert.deepEqual(emptyEnglish, [],
    `${app}/en contains empty or non-text translations: ${emptyEnglish.join(', ')}`);
}

console.log('Verified frontend session, lifecycle scheduling, mutation integrity, and complete English translation coverage.');
