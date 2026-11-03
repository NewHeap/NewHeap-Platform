import {
  AdminAgent,
  AgentSubjectType,
  NH_ASSISTANT_SYSTEM_SUBJECT_TYPE,
  RuleScriptRunRecord,
  RuleScriptStatus,
  RuleScriptSubjectRunStatus,
  RuleScriptTool,
  RuleScriptVersion
} from '@newheap/platform-ai-chat';

/** The spaces one press of Tab inserts in the script editor. */
export const ruleScriptIndent = '  ';

/** The scope of a rule script: the agent's own rules (`null`) or one of its skills. */
export type RuleScriptScope = string | null;

/** A place in a script, from 1, as the check and a stopped run name it. */
export interface RuleScriptPosition {
  line: number;
  column: number;
}

/** A text with its selection, after the editor changed it. */
export interface RuleScriptEdit {
  text: string;
  start: number;
  end: number;
}

/** What a dry run can be run on: the whole application when the agent works on it, then its object types. */
export interface RuleScriptSubjectTarget {
  key: string;
  type: AgentSubjectType;
  perDivision: boolean;
}

/** The tone of a version's status badge: live is the version in effect, a candidate waits. */
export function ruleScriptStatusTone(status: RuleScriptStatus): string {
  switch (status) {
    case 'live':
      return 'success';
    case 'candidate':
      return 'warning';
    default:
      return '';
  }
}

/** The tone of one subject's outcome in a dry run or a shadow run. */
export function ruleScriptRunTone(status: RuleScriptSubjectRunStatus): string {
  switch (status) {
    case 'completed':
      return 'success';
    case 'stopped':
      return 'warning';
    default:
      return 'danger';
  }
}

/** Whether every subject of a run completed. */
export function ruleScriptRunCompleted(record: RuleScriptRunRecord): boolean {
  return record.completedCount === record.subjectCount;
}

/** The line and column, from 1, of an offset in a text. */
export function ruleScriptPositionOf(text: string, offset: number): RuleScriptPosition {
  const before = text.slice(0, Math.max(0, Math.min(offset, text.length)));
  const lines = before.split('\n');
  return { line: lines.length, column: lines[lines.length - 1].length + 1 };
}

/** The offset of a line and column, from 1, in a text; a place past the end of a line ends there. */
export function ruleScriptOffsetOf(text: string, position: RuleScriptPosition): number {
  const lines = text.split('\n');
  const line = Math.max(1, Math.min(position.line, lines.length));
  let offset = 0;
  for (let index = 0; index < line - 1; index++) {
    offset += lines[index].length + 1;
  }

  return offset + Math.max(0, Math.min(position.column - 1, lines[line - 1].length));
}

/**
 * Tab in the editor: inserts the indent at the caret, or indents every selected line; with Shift it takes
 * one indent off the selected lines, or off the caret's line.
 */
export function ruleScriptIndentEdit(text: string, start: number, end: number, outdent: boolean): RuleScriptEdit {
  if (!outdent && start === end) {
    return {
      text: text.slice(0, start) + ruleScriptIndent + text.slice(end),
      start: start + ruleScriptIndent.length,
      end: start + ruleScriptIndent.length
    };
  }

  const lineStart = text.lastIndexOf('\n', start - 1) + 1;
  // A selection that ends at the start of a line leaves that line alone.
  const lastEnd = end > start && text[end - 1] === '\n' ? end - 1 : end;
  const block = text.slice(lineStart, lastEnd);
  const lines = block.split('\n');
  let removedFirst = 0;
  let changed = 0;
  const shifted = lines.map((line, index) => {
    if (!outdent) {
      changed += ruleScriptIndent.length;
      return ruleScriptIndent + line;
    }

    const removed = line.startsWith(ruleScriptIndent) ? ruleScriptIndent.length : line.startsWith(' ') ? 1 : 0;
    if (index === 0) {
      removedFirst = removed;
    }
    changed -= removed;
    return line.slice(removed);
  });

  const next = text.slice(0, lineStart) + shifted.join('\n') + text.slice(lastEnd);
  if (!outdent) {
    return { text: next, start: start + ruleScriptIndent.length, end: end + changed };
  }

  return { text: next, start: Math.max(lineStart, start - removedFirst), end: Math.max(lineStart, end + changed) };
}

/** Inserts a snippet at the selection and puts the caret after it. */
export function ruleScriptInsertEdit(text: string, start: number, end: number, snippet: string): RuleScriptEdit {
  const next = text.slice(0, start) + snippet + text.slice(end);
  return { text: next, start: start + snippet.length, end: start + snippet.length };
}

/** The call of a tool as a script writes it. */
export function ruleScriptToolSnippet(tool: RuleScriptTool): string {
  return `const result = await tools.${tool.name}({ });\n`;
}

/** Raising an alert of a kind as a script writes it. */
export function ruleScriptAlertSnippet(kind: string): string {
  return `alerts.raise({ kind: ${JSON.stringify(kind)}, key: "", parameters: { } });\n`;
}

/**
 * What a dry run can be run on, as running the agent now offers it: the whole application when the agent
 * works on it, then the object types it works on, or every type the application registers when it works
 * on all objects.
 */
export function ruleScriptSubjectTargets(
  agent: AdminAgent,
  subjectTypes: readonly AgentSubjectType[],
  systemName: string
): RuleScriptSubjectTarget[] {
  const targets: RuleScriptSubjectTarget[] = [];
  if (agent.worksOnApplication) {
    const system = subjectTypes.find(type => type.isSystem && type.id === NH_ASSISTANT_SYSTEM_SUBJECT_TYPE) ?? {
      id: NH_ASSISTANT_SYSTEM_SUBJECT_TYPE,
      name: systemName,
      pluralName: null,
      entityType: null,
      entityTypeFull: null,
      link: null,
      searchable: false,
      isSystem: true
    };
    targets.push({ key: NH_ASSISTANT_SYSTEM_SUBJECT_TYPE, type: system, perDivision: agent.perDivision === true });
  }

  const named = new Set(agent.worksOnSubjectTypes ?? []);
  const all = agent.worksOnAllSubjects ?? true;
  for (const type of subjectTypes) {
    if (!type.isSystem && (all || named.has(type.id))) {
      targets.push({ key: type.id, type, perDivision: false });
    }
  }

  return targets;
}

/** The version a list names, or `null`. */
export function ruleScriptVersionOf(versions: readonly RuleScriptVersion[], version: number | null): RuleScriptVersion | null {
  return version === null ? null : versions.find(item => item.version === version) ?? null;
}

/** The translation keys of a script code (`ai-script-...`) or an assistant code, then the generic text. */
export function ruleScriptCodeKeys(code: string | null | undefined): string[] {
  return code
    ? [`nh-assistant.admin.rule-scripts.codes.${code}`, `nh-assistant.errors.${code}`, 'nh-assistant.errors.generic']
    : [];
}
