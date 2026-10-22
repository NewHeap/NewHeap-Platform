import { Pipe, PipeTransform } from '@angular/core';
import { ApprovalPart, ConnectionRequiredPart, FilePart, MessagePart, TextPart, ToolCallPart } from '../models/assistant-api.models';

/**
 * A message part as the thread renders it: consecutive tool calls form one group, and so do
 * consecutive attached files.
 */
export type NhAssistantPartGroup =
  | { kind: 'text'; key: string; part: TextPart }
  | { kind: 'approval'; key: string; part: ApprovalPart }
  | { kind: 'connection'; key: string; part: ConnectionRequiredPart }
  | { kind: 'tools'; key: string; calls: ToolCallPart[] }
  | { kind: 'files'; key: string; files: FilePart[] };

/**
 * Groups consecutive tool calls of a message, and consecutive files. Text and approvals break a
 * group, so an approval always stays visible. A tool group's key is its first invocation id, so it
 * keeps its identity while later calls of the same run are appended.
 */
export function groupNhAssistantParts(parts: readonly MessagePart[]): NhAssistantPartGroup[] {
  const groups: NhAssistantPartGroup[] = [];
  let tools: { kind: 'tools'; key: string; calls: ToolCallPart[] } | null = null;
  let files: { kind: 'files'; key: string; files: FilePart[] } | null = null;

  parts.forEach((part, index) => {
    if (part.type === 'tool-call') {
      files = null;
      if (!tools) {
        tools = { kind: 'tools', key: `tools:${part.invocationId}`, calls: [] };
        groups.push(tools);
      }
      tools.calls.push(part);
      return;
    }

    tools = null;
    if (part.type === 'file') {
      if (!files) {
        files = { kind: 'files', key: `files:${index}`, files: [] };
        groups.push(files);
      }
      files.files.push(part);
      return;
    }

    files = null;
    if (part.type === 'approval') {
      groups.push({ kind: 'approval', key: `approval:${part.approvalId}`, part });
    } else if (part.type === 'connection-required') {
      groups.push({ kind: 'connection', key: `connection:${part.serverId}:${index}`, part });
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
