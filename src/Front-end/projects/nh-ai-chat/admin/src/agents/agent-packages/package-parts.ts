import {
  AdminAgent,
  AgentPackagePartKind,
  NH_ASSISTANT_PACKAGE_LOCK_ALL,
  NH_ASSISTANT_PACKAGE_LOCK_FIELDS,
  PackagePartLink
} from '@newheap/platform-ai-chat';

/**
 * The parts of an agent that an agent package carries and locks, as the administration names and
 * shows them (internal). A part key is `agent`, `runSettings`, or `{kind}:{id}` such as
 * `skill:intake`; a lock covers the whole part (`*`) or named fields of it.
 */

/** A part of an agent in a package, from its key. */
export interface PackagePart {
  key: string;
  kind: AgentPackagePartKind;
  /** The skill or trigger id, the subject type or the metadata name; `null` for the agent and its run settings. */
  name: string | null;
}

/** How a package links one part on this host, as the forms and lists show it. */
export interface PackageLockView {
  packageId: string;
  packageVersion: number;
  /** The whole part is locked. */
  whole: boolean;
  /** The locked fields by their names in the format; empty while the whole part or nothing is locked. */
  fields: readonly string[];
  /** The part changed here after its import; the next upgrade overwrites its unlocked fields. */
  changedLocally: boolean;
}

const kinds: readonly AgentPackagePartKind[] = ['agent', 'runSettings', 'skill', 'trigger', 'consultation', 'handOff', 'metadata'];

/** The part a key names, or `null` for a key of an unknown kind. */
export function packagePartOf(key: string): PackagePart | null {
  const colon = key.indexOf(':');
  const kind = (colon < 0 ? key : key.slice(0, colon)) as AgentPackagePartKind;
  if (!kinds.includes(kind) || (colon < 0) !== (kind === 'agent' || kind === 'runSettings')) {
    return null;
  }

  return { key, kind, name: colon < 0 ? null : key.slice(colon + 1) };
}

/** The lock a part's link shows, or `null` for a part no package links. */
export function packageLockOf(link: PackagePartLink | null | undefined): PackageLockView | null {
  if (!link) {
    return null;
  }

  const whole = link.lockedFields.includes(NH_ASSISTANT_PACKAGE_LOCK_ALL);
  return {
    packageId: link.packageId,
    packageVersion: link.packageVersion,
    whole,
    fields: whole ? [] : [...link.lockedFields],
    changedLocally: link.changedLocally
  };
}

/** The lock of an agent's definition (part `agent`), or `null` when its package does not link it. */
export function agentDefinitionLockOf(agent: AdminAgent | null | undefined): PackageLockView | null {
  return packageLockOf(agent?.package?.parts.find(part => part.part === 'agent'));
}

/**
 * The package an agent belongs to, as the notice of the whole agent shows it: the package's id and
 * the version this host has, without locks of its own.
 */
export function agentPackageOf(agent: AdminAgent | null | undefined): PackageLockView | null {
  const link = agent?.package;
  return link && link.parts.length > 0
    ? { packageId: link.packageId, packageVersion: link.packageVersion, whole: false, fields: [], changedLocally: false }
    : null;
}

/** Whether a lock keeps a field of its part from changing here. */
export function isPackageFieldLocked(lock: PackageLockView | null | undefined, field: string): boolean {
  return !!lock && (lock.whole || lock.fields.includes(field));
}

/** Whether a lock keeps anything of its part from changing, so the part cannot be removed. */
export function isPackageLocked(lock: PackageLockView | null | undefined): boolean {
  return !!lock && (lock.whole || lock.fields.length > 0);
}

/** The fields a package may lock on a part of `kind`; an agent in code carries only the first six. */
export function lockableFields(kind: AgentPackagePartKind, source: 'code' | 'admin' = 'admin'): readonly string[] {
  const fields = NH_ASSISTANT_PACKAGE_LOCK_FIELDS[kind];
  return kind === 'agent' && source === 'code' ? fields.slice(0, 6) : fields;
}

/** The translation key of a part's name: the kind's text, which takes the part's `name`. */
export function packagePartLabelKey(part: PackagePart): string {
  return `nh-assistant.admin.packages.parts.${dashCase(part.kind)}`;
}

/** The translation key of a field's name in a lock. */
export function packageFieldLabelKey(field: string): string {
  return `nh-assistant.admin.packages.fields.${dashCase(field)}`;
}

/** A camelCase name as a dash-case segment of a translation key. */
function dashCase(value: string): string {
  return value.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`);
}
