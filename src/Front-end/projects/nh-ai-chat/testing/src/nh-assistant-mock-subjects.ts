import { AgentSubjectSearch, AgentSubjectType, AgentSubjectWork } from '@newheap/platform-ai-chat';
import { NhAssistantMockSubjectType } from './nh-assistant-mock.models';

/** A mock response: status plus optional JSON body. */
interface MockResult {
  status: number;
  body?: unknown;
}

const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const minQueryLength = 2;
const maxQueryLength = 100;
const defaultLimit = 20;
const maxLimit = 50;
const placeholder = '{id}';

/** The platform's type of the whole application, which the server registers itself. */
const systemType = 'nh-system';
/** The whole application as one subject, for agents that work app-wide. */
const systemGlobalId = 'global';
const systemNames: Record<string, string> = { en: 'The whole application', nl: 'De hele applicatie' };

/**
 * The subject types of the mock API, as the application's resolvers describe them on the server:
 * their names per language in the run catalog, the search people use to choose a subject and the
 * name of a subject chosen by id. The scenario is checked like the server checks the registration
 * at startup, and every request is checked like the server checks it. Next to the scenario's types
 * the catalog lists the platform's `nh-system`, the whole application, as the server does; the mock
 * lists no divisions, so it names only the app-wide `nh-system/global`.
 */
export class NhAssistantMockSubjects {
  private readonly types: NhAssistantMockSubjectType[];

  constructor(types: readonly NhAssistantMockSubjectType[] = []) {
    const ids = new Set<string>();
    for (const type of types) {
      if (!dashCase.test(type.id) || type.id.length > 100) {
        throw new Error(`Mock subject type '${type.id}' is not a dash-case id of at most 100 characters.`);
      }
      if (type.id.startsWith('nh-')) {
        throw new Error(`Mock subject type '${type.id}' starts with 'nh-', which the platform reserves.`);
      }
      if (ids.has(type.id)) {
        throw new Error(`Mock subject type '${type.id}' is listed twice.`);
      }
      ids.add(type.id);
      validateNames(type.id, type.names, type.fallbackLanguage ?? 'en');
      validateNames(type.id, type.pluralNames, type.fallbackLanguage ?? 'en');
      if (type.pluralNames && !type.names) {
        throw new Error(`Mock subject type '${type.id}' has plural names but no names.`);
      }
      if (type.link != null && !isValidLinkTemplate(type.link)) {
        throw new Error(`Mock subject type '${type.id}' has an invalid link template '${type.link}'.`);
      }
    }
    this.types = [...types].sort((left, right) => left.id.localeCompare(right.id));
  }

  /** The run catalog's subject types in the request's language, the platform's among them, by id. */
  catalog(language: string | null): AgentSubjectType[] {
    const application: AgentSubjectType[] = this.types.map(type => ({
      id: type.id,
      name: pick(type.names, type.fallbackLanguage ?? 'en', language) ?? type.id,
      pluralName: pick(type.pluralNames, type.fallbackLanguage ?? 'en', language),
      entityType: type.entityType ?? null,
      entityTypeFull: type.entityTypeFull ?? null,
      link: type.link ?? null,
      searchable: isSearchable(type),
      isSystem: false
    }));
    const system: AgentSubjectType = {
      id: systemType,
      name: systemName(language),
      pluralName: null,
      entityType: null,
      entityTypeFull: null,
      link: null,
      searchable: false,
      isSystem: true
    };
    return [...application, system].sort((left, right) => left.id.localeCompare(right.id));
  }

  /** `GET subjects/{type}/search` and `GET subjects/{type}/{id}/work`; `null` for other paths. */
  handle(method: string, segments: string[], query: URLSearchParams, language: string | null): MockResult | null {
    if (segments.length === 3 && segments[2] === 'search') {
      return method === 'GET' ? this.search(segments[1], query) : { status: 405 };
    }
    if (segments.length === 4 && segments[3] === 'work') {
      return method === 'GET' ? this.work(segments[1], segments[2], language) : { status: 405 };
    }
    return null;
  }

  private search(typeId: string, query: URLSearchParams): MockResult {
    const type = this.types.find(item => item.id === typeId);
    if (!type && typeId !== systemType) {
      return failure(404, 'assistant-subject-type-unknown');
    }
    const text = (query.get('query') ?? '').trim();
    if (text.length < minQueryLength || text.length > maxQueryLength) {
      return failure(400, 'assistant-validation', { query: ['invalid'] });
    }
    // The whole application is not searched; people choose it as such.
    if (!type || !isSearchable(type)) {
      return failure(404, 'assistant-subject-search-unavailable');
    }

    const limit = Math.min(Math.max(Number(query.get('limit') ?? defaultLimit) || defaultLimit, 1), maxLimit);
    const lowered = text.toLocaleLowerCase();
    const items = (type.subjects ?? [])
      .filter(subject => subject.readable !== false)
      .filter(subject => subject.displayName.toLocaleLowerCase().includes(lowered) || subject.id.toLocaleLowerCase().includes(lowered))
      .slice(0, limit)
      .map(subject => ({
        type: type.id,
        id: subject.id,
        displayName: subject.displayName,
        scopeKey: subject.scopeKey ?? 'global',
        divisionId: subject.divisionId ?? null,
        link: linkFor(type, subject.id)
      }));
    const body: AgentSubjectSearch = { subjectType: type.id, items };
    return { status: 200, body };
  }

  /** The name of a subject chosen by id; the mock runs no agents, so it has no agent work. */
  private work(typeId: string, subjectId: string, language: string | null): MockResult {
    if (typeId === systemType) {
      return this.systemWork(subjectId, language);
    }
    const type = this.types.find(item => item.id === typeId);
    if (!type) {
      return failure(404, 'assistant-subject-type-unknown');
    }
    const subject = (type.subjects ?? []).find(item => item.id === subjectId && item.readable !== false);
    if (!subject) {
      return failure(404, 'assistant-subject-not-found');
    }

    const body: AgentSubjectWork = {
      subjectType: type.id,
      subjectId: subject.id,
      subjectDisplayName: subject.displayName,
      subjectTypeName: type.names ? pick(type.names, type.fallbackLanguage ?? 'en', language) : null,
      subjectLink: linkFor(type, subject.id),
      role: 'reader',
      agents: [],
      openProposals: []
    };
    return { status: 200, body };
  }

  /**
   * The whole application as the server names it: "The whole application" in the reader's language,
   * without a link. The mock lists no divisions, so `division-…` subjects are not found.
   */
  private systemWork(subjectId: string, language: string | null): MockResult {
    if (subjectId !== systemGlobalId) {
      return failure(404, 'assistant-subject-not-found');
    }

    const name = systemName(language);
    const body: AgentSubjectWork = {
      subjectType: systemType,
      subjectId,
      subjectDisplayName: name,
      subjectTypeName: name,
      subjectLink: null,
      role: 'reader',
      agents: [],
      openProposals: []
    };
    return { status: 200, body };
  }
}

/** The name of the whole application in the request's language, English otherwise. */
function systemName(language: string | null): string {
  return pick(systemNames, 'en', language) ?? systemNames['en'];
}

function isSearchable(type: NhAssistantMockSubjectType): boolean {
  return type.searchable ?? (type.subjects?.length ?? 0) > 0;
}

function linkFor(type: NhAssistantMockSubjectType, subjectId: string): string | null {
  return type.link ? type.link.replace(placeholder, encodeURIComponent(subjectId)) : null;
}

function validateNames(typeId: string, names: Record<string, string> | undefined, fallbackLanguage: string): void {
  if (!names) {
    return;
  }
  for (const [language, name] of Object.entries(names)) {
    if (!/^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$/.test(language)) {
      throw new Error(`Mock subject type '${typeId}' has a name in '${language}', which is not a culture name.`);
    }
    if (name.trim().length === 0 || name.trim().length > 100) {
      throw new Error(`Mock subject type '${typeId}' has a name in '${language}' that is empty or longer than 100 characters.`);
    }
  }
  if (!Object.keys(names).some(language => language.toLowerCase() === fallbackLanguage.toLowerCase())) {
    throw new Error(`Mock subject type '${typeId}' has no name in its fallback language '${fallbackLanguage}'.`);
  }
}

/** The name in the language, another culture of that language, else the fallback language, as the server picks. */
function pick(names: Record<string, string> | undefined, fallbackLanguage: string, language: string | null): string | null {
  if (!names) {
    return null;
  }
  const entries = Object.entries(names);
  const find = (wanted: string) => entries.find(([key]) => key.toLowerCase() === wanted.toLowerCase())?.[1];
  const requested = language?.split(',')[0]?.split(';')[0]?.trim() ?? '';
  const base = requested.split('-')[0];
  return (requested ? find(requested) : undefined)
    ?? (base ? find(base) : undefined)
    ?? (base ? entries.filter(([key]) => key.split('-')[0].toLowerCase() === base.toLowerCase()).sort(([left], [right]) => left.localeCompare(right))[0]?.[1] : undefined)
    ?? find(fallbackLanguage)
    ?? null;
}

/** One `{id}` in a path that starts with one slash, or in the path or query of an `https` address. */
function isValidLinkTemplate(template: string): boolean {
  if (/[\s{}]/.test(template.split(placeholder).join('')) || template.split(placeholder).length !== 2) {
    return false;
  }
  if (template.startsWith('/')) {
    return template.length > 1 && template[1] !== '/' && template[1] !== '\\';
  }
  if (!template.toLowerCase().startsWith('https://')) {
    return false;
  }
  const pathStart = template.indexOf('/', 'https://'.length);
  return pathStart > 0 && template.indexOf(placeholder) > pathStart;
}

function failure(status: number, code: string, errors?: Record<string, string[]>): MockResult {
  return { status, body: { code, messageKey: `nh-assistant.errors.${code}`, ...(errors ? { errors } : {}) } };
}
