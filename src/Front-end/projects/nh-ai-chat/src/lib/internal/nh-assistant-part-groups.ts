import { Pipe, PipeTransform } from '@angular/core';
import { ApprovalPart, MessagePart, TextPart, ToolCallPart } from '../models/assistant-api.models';

/** A message part as the thread renders it: consecutive tool calls form one group. */
export type NhAssistantPartGroup =
  | { kind: 'text'; key: string; part: TextPart }
  | { kind: 'approval'; key: string; part: ApprovalPart }
  | { kind: 'tools'; key: string; calls: ToolCallPart[] };

/**
 * Groups consecutive tool calls of a message. Text and approvals break a group, so an
 * approval always stays visible. A group's key is its first invocation id, so it keeps
 * its identity while later calls of the same run are appended.
 */
export function groupNhAssistantParts(parts: readonly MessagePart[]): NhAssistantPartGroup[] {
  const groups: NhAssistantPartGroup[] = [];
  let tools: { kind: 'tools'; key: string; calls: ToolCallPart[] } | null = null;

  parts.forEach((part, index) => {
    if (part.type === 'tool-call') {
      if (!tools) {
        tools = { kind: 'tools', key: `tools:${part.invocationId}`, calls: [] };
        groups.push(tools);
      }
      tools.calls.push(part);
      return;
    }

    tools = null;
    if (part.type === 'approval') {
      groups.push({ kind: 'approval', key: `approval:${part.approvalId}`, part });
    } else {
      groups.push({ kind: 'text', key: `text:${index}`, part });
    }
  });

  return groups;
}

/** Pure, so the groups are only rebuilt when the reducer replaces a message's parts. */
@Pipe({ name: 'nhAssistantPartGroups', standalone: true })
export class NhAssistantPartGroupsPipe implements PipeTransform {
  transform(parts: readonly MessagePart[]): NhAssistantPartGroup[] {
    return groupNhAssistantParts(parts);
  }
}
