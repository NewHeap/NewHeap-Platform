import { AgentSubjectSearch, AgentSubjectType, AgentSubjectWork } from '@newheap/platform-ai-chat';
import { NhAssistantMockDivision, NhAssistantMockSubjectType } from './nh-assistant-mock.models';

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
/** The whole application in one division, for agents that work per division. */
const divisionPrefix = 'division-';
const systemNames: Record<string, string> = { en: 'The whole application', nl: 'De hele applicatie' };

/** A subject as the mock's resolvers describe it to the caller. */
export interface NhAssistantMockDescribedSubject {
  subjectType: string;
  subjectId: string;
  displayName: string;
  typeName: string | null;
  link: string | null;
  role: 'reader' | 'participant' | 'approver';
  /** The whole application app-wide (`global`) or in one division. */
  scope: 'object' | 'global' | 'division';
}

/**
 * The subject types of the mock API, as the application's resolvers describe them on the server:
 * their names per language in the run catalog, the search people use to choose a subject and the
 * name of a subject chosen by id. The scenario is checked like the server checks the registration
 * at startup, and every request is checked like the server checks it. Next to the scenario's types
 * the catalog lists the platform's `nh-system`, the whole application, as the server does: app-wide
 * as `nh-system/global`, and with the scenario's `divisions` per division, which people then find
 * by name.
 */
export class NhAssistantMockSubjects {
  private readonly types: NhAssistantMockSubjectType[];
  private readonly divisions: NhAssistantMockDivision[];

  constructor(types: readonly NhAssistantMockSubjectType[] = [], divisions: readonly NhAssistantMockDivision[] = []) {
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
    for (const division of divisions) {
      if (!/^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i.test(division.id)) {
        throw new Error(`Mock division '${division.id}' is not a GUID.`);
      }
    }
    this.divisions = [...divisions].sort((left, right) => left.name.localeCompare(right.name));
  }

  /**
   * The subject as the caller sees it, or `null` when it does not exist or the caller may not read
   * it; `undefined` for an unknown type. The caller reads the whole application, and decides there
   * as an administrator.
   */
  describe(typeId: string, subjectId: string, language: string | null, administrator: boolean): NhAssistantMockDescribedSubject | null | undefined {
    if (typeId === systemType) {
      const name = systemName(language);
      if (subjectId === systemGlobalId) {
        return { subjectType: systemType, subjectId, displayName: name, typeName: name, link: null, role: administrator ? 'approver' : 'reader', scope: 'global' };
      }
      const division = this.division(subjectId);
      return division
        ? { subjectType: systemType, subjectId, displayName: division.name, typeName: name, link: null, role: administrator ? 'approver' : 'reader', scope: 'division' }
        : null;
    }
    const type = this.types.find(item => item.id === typeId);
    if (!type) {
      return undefined;
    }
    const subject = (type.subjects ?? []).find(item => item.id === subjectId && item.readable !== false);
    return subject
      ? {
        subjectType: type.id,
        subjectId: subject.id,
        displayName: subject.displayName,
        typeName: type.names ? pick(type.names, type.fallbackLanguage ?? 'en', language) : null,
        link: linkFor(type, subject.id),
        role: subject.role ?? 'participant',
        scope: 'object'
      }
      : null;
  }

  /** A listed division the caller works in, by the id of its subject `division-{id:N}`. */
  private division(subjectId: string): NhAssistantMockDivision | undefined {
    return this.divisions.find(division => division.readable !== false && divisionSubjectId(division) === subjectId);
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
      // With listed divisions the whole application is searched for them.
      searchable: this.divisions.length > 0,
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
    // The whole application is not searched, but its divisions are, when the application lists them.
    if (!type) {
      return this.divisions.length > 0 ? this.searchDivisions(text, query) : failure(404, 'assistant-subject-search-unavailable');
    }
    if (!isSearchable(type)) {
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

  /** The divisions the caller works in whose name contains the text, ordered by name. */
  private searchDivisions(text: string, query: URLSearchParams): MockResult {
    const limit = Math.min(Math.max(Number(query.get('limit') ?? defaultLimit) || defaultLimit, 1), maxLimit);
    const lowered = text.toLocaleLowerCase();
    const items = this.divisions
      .filter(division => division.readable !== false && division.name.toLocaleLowerCase().includes(lowered))
      .slice(0, limit)
      .map(division => ({
        type: systemType,
        id: divisionSubjectId(division),
        displayName: division.name,
        scopeKey: `division:${guidN(division.id)}`,
        divisionId: division.id,
        link: null
      }));
    const body: AgentSubjectSearch = { subjectType: systemType, items };
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
   * without a link, and in a division the division's name. A division the scenario does not list, or
   * that the caller does not work in, is not found.
   */
  private systemWork(subjectId: string, language: string | null): MockResult {
    const division = subjectId === systemGlobalId ? null : this.division(subjectId);
    if (subjectId !== systemGlobalId && !division) {
      return failure(404, 'assistant-subject-not-found');
    }

    const name = systemName(language);
    const body: AgentSubjectWork = {
      subjectType: systemType,
      subjectId,
      subjectDisplayName: division?.name ?? name,
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

/** The subject of the whole application in a division: `division-` and the id without dashes. */
export function divisionSubjectId(division: NhAssistantMockDivision): string {
  return divisionPrefix + guidN(division.id);
}

function guidN(id: string): string {
  return id.replace(/-/g, '').toLowerCase();
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
