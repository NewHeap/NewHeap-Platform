import { ClientContext, ClientContextEntity } from '../models/assistant-api.models';

/** Limits of the page context (contract 15.2). */
export const NH_ASSISTANT_PAGE_CONTEXT_LIMITS = {
  route: 200,
  title: 120,
  entities: 5,
  entityId: 64,
  entityLabel: 120
} as const;

const dashCase = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/**
 * Validates host-provided page context and truncates it to the contract limits.
 * Returns null for an invalid shape (not an object or no route), so the message is sent
 * without page context. Route, title and labels are truncated; entities beyond five are
 * dropped, and entities with a type that is not dash-case, an empty id or an id longer
 * than 64 characters are dropped because a truncated id would point at another entity.
 */
export function normalizeNhAssistantClientContext(value: unknown): ClientContext | null {
  if (!isRecord(value)) {
    return null;
  }

  const route = typeof value['route'] === 'string' ? value['route'].trim() : '';
  if (route.length === 0) {
    return null;
  }

  const result: ClientContext = { route: truncate(route, NH_ASSISTANT_PAGE_CONTEXT_LIMITS.route) };

  const title = typeof value['title'] === 'string' ? value['title'].trim() : '';
  if (title.length > 0) {
    result.title = truncate(title, NH_ASSISTANT_PAGE_CONTEXT_LIMITS.title);
  }

  if (Array.isArray(value['entities'])) {
    const entities = value['entities']
      .map(normalizeEntity)
      .filter((entity): entity is ClientContextEntity => entity !== null)
      .slice(0, NH_ASSISTANT_PAGE_CONTEXT_LIMITS.entities);
    if (entities.length > 0) {
      result.entities = entities;
    }
  }

  return result;
}

/** Short text for the page-context chip: the first entity's label, the title or the route. */
export function describeNhAssistantClientContext(context: ClientContext): string {
  const entity = context.entities?.[0];
  if (entity) {
    return entity.label ?? `${entity.type} ${entity.id}`;
  }

  return context.title ?? context.route;
}

function normalizeEntity(value: unknown): ClientContextEntity | null {
  if (!isRecord(value)) {
    return null;
  }

  const type = typeof value['type'] === 'string' ? value['type'].trim() : '';
  const id = typeof value['id'] === 'string' ? value['id'].trim() : '';
  if (!dashCase.test(type) || id.length === 0 || id.length > NH_ASSISTANT_PAGE_CONTEXT_LIMITS.entityId) {
    return null;
  }

  const entity: ClientContextEntity = { type, id };
  const label = typeof value['label'] === 'string' ? value['label'].trim() : '';
  if (label.length > 0) {
    entity.label = truncate(label, NH_ASSISTANT_PAGE_CONTEXT_LIMITS.entityLabel);
  }
  return entity;
}

function truncate(value: string, max: number): string {
  return value.length <= max ? value : value.slice(0, max);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}
