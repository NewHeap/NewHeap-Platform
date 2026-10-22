import { AgentUploads, FileReference, NH_ASSISTANT_UPLOAD_HEADER } from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure } from './nh-assistant-mock-admin-shared';

/** An upload as the application's file source receives it, after the assistant's checks. */
export interface NhAssistantMockUpload {
  conversationId: string;
  name: string;
  mediaType: string;
  size: number;
}

/** The longest file name a reference takes, as on the server. */
const maxNameLength = 256;

const mediaTypePattern = /^[a-z0-9][a-z0-9!#$&^_.+-]*\/[a-z0-9][a-z0-9!#$&^_.+-]*$/;

/** How files of the platform's types start; other accepted types are the application's to check. */
const signatures: Record<string, (head: Uint8Array) => boolean> = {
  'application/pdf': head => indexOf(head, [0x25, 0x50, 0x44, 0x46, 0x2d]) >= 0,
  'image/png': head => startsWith(head, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  'image/jpeg': head => startsWith(head, [0xff, 0xd8, 0xff]),
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': head => startsWith(head, [0x50, 0x4b, 0x03, 0x04]),
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': head => startsWith(head, [0x50, 0x4b, 0x03, 0x04])
};

/**
 * The files of the mock API: `POST conversations/{id}/files` with the server's checks, and the
 * attachments of a message, which a turn opens again as the server's file source would.
 */
export class NhAssistantMockFiles {
  private readonly stored = new Map<string, NhAssistantMockUpload & { reference: FileReference }>();
  private next = 0;

  constructor(private readonly refuse?: (upload: NhAssistantMockUpload) => string | null) {}

  /** The uploads the mock stored, oldest first. */
  get uploads(): readonly (NhAssistantMockUpload & { reference: FileReference })[] {
    return [...this.stored.values()];
  }

  /**
   * Checks an upload as the server does, in its order: a file source, an agent that accepts files,
   * the upload header, one file part named `file` with a name and content, an accepted media type
   * whose content matches, the agent's largest size, and finally the application's own refusal.
   */
  async upload(
    conversationId: string,
    offered: boolean,
    uploads: AgentUploads | null,
    headers: Record<string, string>,
    form: FormData
  ): Promise<NhAssistantMockResult> {
    if (!offered) {
      return mockFailure('assistant-files-unavailable');
    }
    if (!uploads) {
      return mockFailure('assistant-files-not-accepted');
    }

    const header = Object.entries(headers).find(([name]) => name.toLowerCase() === NH_ASSISTANT_UPLOAD_HEADER.toLowerCase());
    let parts = 0;
    form.forEach(() => parts++);
    const file = form.get('file');
    if (!header?.[1] || parts !== 1 || !(file instanceof Blob)) {
      return mockFailure('assistant-file-upload-invalid');
    }

    const name = nameOf(file instanceof File ? file.name : '');
    if (name === null || file.size === 0) {
      return mockFailure('assistant-file-upload-invalid');
    }
    const mediaType = normalizeMediaType(file.type);
    if (mediaType === null || !uploads.acceptedMediaTypes.includes(mediaType)) {
      return mockFailure('assistant-file-type-not-accepted');
    }
    if (file.size > uploads.maxFileBytes) {
      return mockFailure('assistant-file-too-large');
    }
    const head = new Uint8Array(await file.slice(0, 1024).arrayBuffer());
    if (!(signatures[mediaType]?.(head) ?? true)) {
      return mockFailure('assistant-file-type-not-accepted');
    }

    const upload: NhAssistantMockUpload = { conversationId, name, mediaType, size: file.size };
    const refusal = this.refuse?.(upload) ?? null;
    if (refusal) {
      return mockFailure(refusalCode(refusal));
    }

    this.next++;
    const reference: FileReference = { id: `mock-file-${this.next}`, name, mediaType, size: file.size, hash: null };
    this.stored.set(reference.id, { ...upload, reference });
    return { status: 200, body: reference };
  }

  /**
   * The code the server refuses a message's `files` with before the turn starts, or `null`: without
   * a file source, more files than one message carries, or a reference that is not one.
   */
  checkMessage(files: unknown, offered: boolean, maxFiles: number): string | null {
    if (files === undefined || files === null || (Array.isArray(files) && files.length === 0)) {
      return null;
    }
    if (!Array.isArray(files)) {
      return 'assistant-file-reference-invalid';
    }
    if (!offered) {
      return 'assistant-files-unavailable';
    }
    if (files.length > maxFiles) {
      return 'assistant-file-count-limit';
    }

    return files.every(isReference) ? null : 'assistant-file-reference-invalid';
  }

  /**
   * The code of the first attachment the turn cannot open, as the server's file source refuses it,
   * or `null` when it opens them all. A reference the mock never stored, or one of another
   * conversation, is not found.
   */
  openCode(conversationId: string, files: readonly FileReference[]): string | null {
    const missing = files.find(file => this.stored.get(file.id)?.conversationId !== conversationId);
    return missing ? 'assistant-file-not-found' : null;
  }
}

/** The file name without a path; `null` when nothing usable is left, as on the server. */
function nameOf(fileName: string): string | null {
  const name = fileName.slice(Math.max(fileName.lastIndexOf('/'), fileName.lastIndexOf('\\')) + 1).trim();
  return name.length === 0 || name.length > maxNameLength || /[\u0000-\u001f\u007f]/.test(name) ? null : name;
}

function normalizeMediaType(value: string): string | null {
  const essence = value.split(';')[0].trim().toLowerCase();
  return essence.length <= 128 && mediaTypePattern.test(essence) ? essence : null;
}

function isReference(value: unknown): value is FileReference {
  const file = value as Partial<FileReference> | null;
  return !!file
    && isText(file.id)
    && isText(file.name)
    && typeof file.mediaType === 'string' && normalizeMediaType(file.mediaType) !== null
    && typeof file.size === 'number' && file.size >= 0;
}

function isText(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= maxNameLength && !/[\u0000-\u001f\u007f]/.test(value);
}

/** The application's refusal as the server passes it on. */
function refusalCode(code: string): string {
  switch (code) {
    case 'assistant-files-unavailable':
    case 'assistant-file-too-large':
      return code;
    case 'assistant-file-type-not-accepted':
    case 'assistant-file-type-not-supported':
      return 'assistant-file-type-not-accepted';
    case 'assistant-file-upload-failed':
    case 'assistant-file-unavailable':
      return 'assistant-file-upload-failed';
    default:
      return 'assistant-file-upload-refused';
  }
}

function startsWith(head: Uint8Array, signature: number[]): boolean {
  return head.length >= signature.length && signature.every((byte, index) => head[index] === byte);
}

function indexOf(head: Uint8Array, signature: number[]): number {
  for (let start = 0; start + signature.length <= head.length; start++) {
    if (signature.every((byte, index) => head[start + index] === byte)) {
      return start;
    }
  }
  return -1;
}
