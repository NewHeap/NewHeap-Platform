import { AgentUploads } from '../models/assistant-api.models';

/** The longest file name a file reference takes. */
export const NH_ASSISTANT_MAX_FILE_NAME_LENGTH = 256;

const unknownMediaType = 'application/octet-stream';

/** The media types the platform names, with the extensions a file picker offers and a short label. */
const knownTypes: Record<string, { extensions: string[]; label: string }> = {
  'application/pdf': { extensions: ['.pdf'], label: 'PDF' },
  'image/png': { extensions: ['.png'], label: 'PNG' },
  'image/jpeg': { extensions: ['.jpg', '.jpeg'], label: 'JPEG' },
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': { extensions: ['.docx'], label: 'Word' },
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': { extensions: ['.xlsx'], label: 'Excel' }
};

/**
 * The media type of a chosen file, lowercase and without parameters. A browser that does not know
 * the type, as happens for Office documents, gives none; the extension of a known type then names
 * it. Otherwise `application/octet-stream`.
 */
export function nhAssistantMediaTypeOf(file: Pick<File, 'name' | 'type'>): string {
  const declared = file.type.split(';')[0].trim().toLowerCase();
  if (declared.length > 0 && declared !== unknownMediaType) {
    return declared;
  }

  const name = file.name.toLowerCase();
  for (const [mediaType, known] of Object.entries(knownTypes)) {
    if (known.extensions.some(extension => name.endsWith(extension))) {
      return mediaType;
    }
  }

  return unknownMediaType;
}

/**
 * The failure code of a file before it is uploaded, or `null` when it may be: the most files of one
 * message, an accepted media type, content and a usable name, and the largest size. `attached` counts
 * the files the message already carries.
 */
export function nhAssistantFileProblem(
  file: Pick<File, 'name' | 'size'>,
  mediaType: string,
  uploads: AgentUploads,
  attached: number
): string | null {
  if (attached >= uploads.maxFiles) {
    return 'assistant-file-count-limit';
  }
  if (!uploads.acceptedMediaTypes.includes(mediaType)) {
    return 'assistant-file-type-not-accepted';
  }
  if (file.size === 0 || file.name.trim().length === 0 || file.name.length > NH_ASSISTANT_MAX_FILE_NAME_LENGTH) {
    return 'assistant-file-upload-invalid';
  }
  if (file.size > uploads.maxFileBytes) {
    return 'assistant-file-too-large';
  }

  return null;
}

/** The `accept` attribute of a file input for these media types: each type and its known extensions. */
export function nhAssistantAcceptAttribute(mediaTypes: readonly string[]): string {
  return mediaTypes
    .flatMap(mediaType => [mediaType, ...(knownTypes[mediaType]?.extensions ?? [])])
    .join(',');
}

/** A short name of a media type, such as `PDF` or `Word`; otherwise its subtype in capitals. */
export function nhAssistantFileTypeLabel(mediaType: string): string {
  const known = knownTypes[mediaType];
  if (known) {
    return known.label;
  }

  const subtype = mediaType.split('/')[1] ?? mediaType;
  return subtype.split(/[.+]/).pop()!.toUpperCase();
}

/** A size in bytes as people read it, such as `820 KB` or `1.4 MB`, in the given language. */
export function nhAssistantFormatFileSize(bytes: number, language?: string | null): string {
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  let format: Intl.NumberFormat;
  try {
    format = new Intl.NumberFormat(language || undefined, { maximumFractionDigits: unit === 0 || value >= 10 ? 0 : 1 });
  } catch {
    format = new Intl.NumberFormat(undefined, { maximumFractionDigits: unit === 0 || value >= 10 ? 0 : 1 });
  }
  return `${format.format(value)} ${units[unit]}`;
}
