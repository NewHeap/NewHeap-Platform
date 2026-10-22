import { WritableSignal, signal } from '@angular/core';
import {
  ApprovalPolicySummary,
  AssistantDataClassification,
  FileLimits,
  LowerFileLimits,
  NhAssistantError,
  RunCatalog,
  ToolCatalogEntry,
  ɵNhAssistantPickerOption as NhAssistantPickerOption
} from '@newheap/platform-ai-chat';
import { nhAssistantSelectorMatches } from '../nh-assistant-admin-agent-editor.component';

/** The longest texts and numbers the server accepts in the run configuration. */
export const runConfigLimits = {
  reason: 500,
  description: 300,
  instructions: 20_000,
  message: 2_000,
  retries: [0, 10],
  concurrent: [1, 100],
  perSubject: [1, 1000],
  debounce: [0, 3600]
} as const;

export const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;

/** A field error: a code such as `required`, with its translation parameters. */
export interface RunConfigFieldError {
  code: string;
  params?: Record<string, unknown>;
}

export type RunConfigErrors = Record<string, RunConfigFieldError>;

/** The translation key of a field error code, shared by the forms. */
export function fieldErrorKey(error: RunConfigFieldError | null | undefined): string | null {
  return error ? `nh-assistant.admin.run-config.field-errors.${error.code}` : null;
}

/**
 * The field errors of a refused save, by field; the server's codes are `required`, `invalid`,
 * `too-long` and `not-found`.
 */
export function serverFieldErrors(error: NhAssistantError | null): RunConfigErrors {
  const result: RunConfigErrors = {};
  for (const [field, codes] of Object.entries(error?.fieldErrors ?? {})) {
    if (codes.length > 0) {
      result[field] = { code: codes[0] };
    }
  }
  return result;
}

/** Requires text, optionally within a length. */
export function requireText(errors: RunConfigErrors, field: string, value: string, max?: number): void {
  const text = value.trim();
  if (text.length === 0) {
    errors[field] = { code: 'required' };
  } else if (max !== undefined && text.length > max) {
    errors[field] = { code: 'too-long', params: { max } };
  }
}

/** Accepts an empty value or a whole number within the range. */
export function checkWhole(errors: RunConfigErrors, field: string, value: string, min: number, max?: number, required = false): void {
  const text = value.trim();
  if (text.length === 0) {
    if (required) {
      errors[field] = { code: 'required' };
    }
    return;
  }
  const parsed = Number(text);
  if (!Number.isInteger(parsed) || parsed < min || (max !== undefined && parsed > max)) {
    errors[field] = max === undefined
      ? { code: 'at-least', params: { min } }
      : { code: 'between', params: { min, max } };
  }
}

/** A daily budget limits both tool calls and tokens, or neither. */
export function checkBudget(errors: RunConfigErrors, calls: string, tokens: string): void {
  checkWhole(errors, 'dailyToolCalls', calls, 1);
  checkWhole(errors, 'dailyTokens', tokens, 1);
  if ((calls.trim() === '') !== (tokens.trim() === '') && !errors['dailyToolCalls'] && !errors['dailyTokens']) {
    errors[calls.trim() === '' ? 'dailyToolCalls' : 'dailyTokens'] = { code: 'budget-pair' };
  }
}

/**
 * The server reports one `dailyBudget` error for both budget fields; the forms show it at the field
 * that is filled in least.
 */
export function withBudgetField(errors: RunConfigErrors, calls: string): RunConfigErrors {
  const budget = errors['dailyBudget'];
  if (!budget) {
    return errors;
  }
  const { dailyBudget: _, ...rest } = errors;
  return { ...rest, [calls.trim() === '' ? 'dailyToolCalls' : 'dailyTokens']: budget };
}

/**
 * Moves the focus to the first invalid control of a form, after Angular rendered the errors; a
 * picker's text field carries `aria-invalid` too.
 */
export function focusFirstInvalid(form: HTMLElement | null | undefined): void {
  const invalid = form?.querySelector<HTMLElement>('[aria-invalid="true"]');
  invalid?.focus();
  invalid?.scrollIntoView?.({ block: 'center' });
}

export function ruleOptions(catalog: RunCatalog | null): NhAssistantPickerOption[] {
  return (catalog?.ruleHandlers ?? []).map(block => ({ value: block.name, label: block.name, description: block.description }));
}

/** The platform's object type of the whole application. */
export const systemSubjectType = 'nh-system';

/** What an agent works on, as far as the trigger events and schedules it is offered depend on it. */
export interface WorksOnStatement {
  worksOnSubjectTypes?: string[];
  worksOnAllSubjects?: boolean;
  worksOnApplication?: boolean;
  /** Its work for the whole application, such as a schedule without a subject set, runs once per division. */
  perDivision?: boolean;
}

/** Whether an agent works on subjects of the type, as the server's rule decides it. */
export function worksOnCovers(statement: WorksOnStatement | null | undefined, subjectType: string): boolean {
  if (subjectType === systemSubjectType) {
    return statement?.worksOnApplication === true;
  }
  if (subjectType.startsWith('nh-')) {
    return false;
  }
  return !statement || statement.worksOnAllSubjects !== false || (statement.worksOnSubjectTypes ?? []).includes(subjectType);
}

/**
 * The trigger events an agent can start on: an event that names its object type up front is offered
 * only to an agent that works on that type.
 */
export function eventOptions(catalog: RunCatalog | null, worksOn?: WorksOnStatement | null): NhAssistantPickerOption[] {
  return (catalog?.triggerEvents ?? [])
    .filter(block => !block.subjectType || worksOnCovers(worksOn, block.subjectType))
    .map(block => ({ value: block.name, label: block.name, description: block.description }));
}

export function subjectSetOptions(catalog: RunCatalog | null): NhAssistantPickerOption[] {
  return (catalog?.subjectSets ?? []).map(block => ({ value: block.name, label: block.name, description: block.description }));
}

/**
 * What a schedule runs for each time: the whole application (once, or once per division for an
 * agent that works per division), or every object in a subject set.
 */
export type ScheduleTarget = 'application' | 'set';

/**
 * The server refuses a schedule without a subject set with `subjectSet: required` when the agent
 * does not work on the whole application; a form that chose the whole application says why there.
 */
export function withScheduleTargetError(errors: RunConfigErrors, target: ScheduleTarget): RunConfigErrors {
  return target === 'application' && errors['subjectSet']
    ? { ...errors, subjectSet: { code: 'application-not-worked-on' } }
    : errors;
}

/** The data classifications a skill can choose, from the least to the most sensitive. */
export const dataClassifications: readonly AssistantDataClassification[] = ['public', 'internal', 'confidential', 'restricted'];

export function profileOptions(catalog: RunCatalog | null): NhAssistantPickerOption[] {
  return (catalog?.profiles ?? []).map(profile => ({ value: profile, label: profile }));
}

/** The approval policies, each with its summary when the server sent one. */
export function policyOptions(catalog: RunCatalog | null, describe: (summary: ApprovalPolicySummary) => string): NhAssistantPickerOption[] {
  const summaries = new Map((catalog?.approvalPolicySummaries ?? []).map(summary => [summary.name, summary]));
  return (catalog?.approvalPolicies ?? []).map(policy => {
    const summary = summaries.get(policy);
    return { value: policy, label: policy, description: summary ? describe(summary) : null };
  });
}

/** The IANA time zones of the browser, UTC first. */
export function timeZoneOptions(): NhAssistantPickerOption[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  const zones = intl.supportedValuesOf?.('timeZone') ?? ['Europe/Amsterdam', 'Europe/London'];
  return ['UTC', ...zones.filter(zone => zone !== 'UTC')].map(zone => ({ value: zone, label: zone }));
}

/** Common schedules, as examples next to the cron field. */
export const cronExamples: readonly { cron: string; key: string }[] = [
  { cron: '0 8 * * 1-5', key: 'weekdays-8' },
  { cron: '0 7 * * *', key: 'daily-7' },
  { cron: '0 9 * * 1', key: 'monday-9' },
  { cron: '0 * * * *', key: 'hourly' }
];

/** One of the agent's tool selectors with what it offers, for the tool choice of a skill. */
export interface ToolChoice {
  selector: string;
  /** The tool's description, for a selector that names one tool of the catalog. */
  description: string | null;
  /** How many tools of the catalog a pattern matches; `null` for a single tool. */
  matches: number | null;
}

/** The agent's tool selectors with the description of their tool, or how many tools a pattern matches. */
export function toolChoices(selectors: readonly string[], catalog: readonly ToolCatalogEntry[]): ToolChoice[] {
  return selectors.map(selector => {
    if (selector.includes('*')) {
      return { selector, description: null, matches: catalog.filter(tool => nhAssistantSelectorMatches(tool.id, selector)).length };
    }
    const tool = catalog.find(item => item.id === selector);
    return { selector, description: tool?.description || null, matches: null };
  });
}

export function blank(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

export function toNumber(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return null;
  }
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : null;
}

export function numberText(value: number | null | undefined): string {
  return value === null || value === undefined ? '' : String(value);
}

/** A readable id from a name: lowercase letters, digits and dashes. */
export function toDashCase(name: string): string {
  return name
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .replace(/^[0-9-]+/, '')
    .slice(0, 64)
    .replace(/-+$/, '');
}

type Translate = (key: string, params?: Record<string, unknown>) => string;

/**
 * An approval policy in one or two sentences: who decides, how long a proposal waits, whether the
 * run waits, and four eyes.
 */
export function describePolicy(summary: ApprovalPolicySummary, t: Translate): string {
  const base = 'nh-assistant.admin.run-config.policy-summary';
  const approvers = summary.approvers.map(approver => {
    switch (approver.kind) {
      case 'initiator':
        return t(`${base}.approver.initiator`);
      case 'subject-role':
        return approver.namedRole
          ? t(`${base}.approver.named-role`, { role: approver.namedRole })
          : t(`${base}.approver.subject-role.${approver.role ?? 'approver'}`);
      case 'policy':
        return t(`${base}.approver.policy`, { policy: approver.policy ?? '' });
      default:
        return approver.kind;
    }
  });
  const who = approvers.length === 0
    ? t(`${base}.nobody`)
    : t(`${base}.who`, { approvers: joinWithOr(approvers, t(`${base}.or`)) });
  const parts = [
    who,
    t(`${base}.wait`, { duration: describeDuration(summary.lifetimeMinutes, t) }),
    t(`${base}.mode.${summary.mode === 'deferred' ? 'deferred' : 'inline'}`)
  ];
  if (summary.fourEyes) {
    parts.push(t(`${base}.four-eyes`));
  }
  return parts.join(' ');
}

function joinWithOr(items: string[], or: string): string {
  return items.length <= 1 ? items.join('') : `${items.slice(0, -1).join(', ')} ${or} ${items[items.length - 1]}`;
}

function describeDuration(minutes: number, t: Translate): string {
  const base = 'nh-assistant.admin.run-config.policy-summary.duration';
  if (minutes >= 1440 && minutes % 1440 === 0) {
    const days = minutes / 1440;
    return t(days === 1 ? `${base}.day` : `${base}.days`, { count: days });
  }
  if (minutes >= 60 && minutes % 60 === 0) {
    const hours = minutes / 60;
    return t(hours === 1 ? `${base}.hour` : `${base}.hours`, { count: hours });
  }
  return t(minutes === 1 ? `${base}.minute` : `${base}.minutes`, { count: minutes });
}

/** The bytes of one megabyte, as the forms show a file size. */
const bytesPerMegabyte = 1024 * 1024;

/** The lower file limits of an agent or a skill, as their fields name them on the server. */
export type FileLimitField = keyof FileLimits;

/** The file limit fields in the order the forms show them. */
export const fileLimitFields: readonly FileLimitField[] = ['maxFileBytes', 'maxFilePages', 'maxFilesPerTurn', 'maxFileTokensPerTurn'];

/** A size in megabytes as a form shows it: whole megabytes plainly, others with up to two decimals. */
export function megabytesText(bytes: number): string {
  const megabytes = bytes / bytesPerMegabyte;
  return Number.isInteger(megabytes) ? String(megabytes) : String(Math.round(megabytes * 100) / 100);
}

/**
 * The lower file limits of an agent or a skill as a form edits them: the largest file in megabytes,
 * the others as whole numbers; an empty field keeps the application's limit (for a skill, the
 * agent's). A size shown with decimals keeps its exact bytes until the administrator changes it.
 */
export class FileLimitsDraft {
  readonly megabytes = signal('');
  readonly pages = signal('');
  readonly files = signal('');
  readonly tokens = signal('');
  private loadedBytes: number | null = null;
  private loadedMegabytes = '';

  load(limits: LowerFileLimits | null | undefined): void {
    this.loadedBytes = limits?.maxFileBytes ?? null;
    this.loadedMegabytes = this.loadedBytes === null ? '' : megabytesText(this.loadedBytes);
    this.megabytes.set(this.loadedMegabytes);
    this.pages.set(numberText(limits?.maxFilePages));
    this.files.set(numberText(limits?.maxFilesPerTurn));
    this.tokens.set(numberText(limits?.maxFileTokensPerTurn));
  }

  /** The text of a field, by its server name. */
  text(field: FileLimitField): WritableSignal<string> {
    switch (field) {
      case 'maxFileBytes':
        return this.megabytes;
      case 'maxFilePages':
        return this.pages;
      case 'maxFilesPerTurn':
        return this.files;
      default:
        return this.tokens;
    }
  }

  /** Checks the fields against the application's limits, as the server does; only next to accepting files. */
  check(errors: RunConfigErrors, application: FileLimits | null | undefined): void {
    const bytes = this.bytes();
    if (this.megabytes().trim() !== '' && (bytes === null || bytes < 1 || (application && bytes > application.maxFileBytes))) {
      errors['maxFileBytes'] = application
        ? { code: 'megabytes-up-to', params: { max: megabytesText(application.maxFileBytes) } }
        : { code: 'megabytes-positive' };
    }
    checkWhole(errors, 'maxFilePages', this.pages(), 1, application?.maxFilePages);
    checkWhole(errors, 'maxFilesPerTurn', this.files(), 1, application?.maxFilesPerTurn);
    checkWhole(errors, 'maxFileTokensPerTurn', this.tokens(), 1, application?.maxFileTokensPerTurn);
  }

  /** The limits a save sends: none when the agent or skill accepts no files. */
  toRequest(acceptsFiles: boolean): Required<LowerFileLimits> {
    return {
      maxFileBytes: acceptsFiles ? this.bytes() : null,
      maxFilePages: acceptsFiles ? toNumber(this.pages()) : null,
      maxFilesPerTurn: acceptsFiles ? toNumber(this.files()) : null,
      maxFileTokensPerTurn: acceptsFiles ? toNumber(this.tokens()) : null
    };
  }

  private bytes(): number | null {
    const text = this.megabytes().trim();
    if (text === '') {
      return null;
    }
    if (text === this.loadedMegabytes && this.loadedBytes !== null) {
      return this.loadedBytes;
    }
    const megabytes = Number(text.replace(',', '.'));
    return Number.isFinite(megabytes) ? Math.round(megabytes * bytesPerMegabyte) : null;
  }
}
