import { AgentUploadExtension, AgentUploads } from '../models/assistant-api.models';

/** The longest file name a file reference takes. */
export const NH_ASSISTANT_MAX_FILE_NAME_LENGTH = 256;

const unknownMediaType = 'application/octet-stream';

const zipAliases = ['application/zip', 'application/x-zip-compressed'];

/**
 * The media types the platform names, with the extensions a file picker offers and a short label.
 * The extensions are the same as the server's table of upload media types.
 */
const knownTypes: Record<string, { extensions: string[]; label: string }> = {
  'application/pdf': { extensions: ['.pdf'], label: 'PDF' },
  'image/png': { extensions: ['.png'], label: 'PNG' },
  'image/jpeg': { extensions: ['.jpg', '.jpeg'], label: 'JPEG' },
  'image/gif': { extensions: ['.gif'], label: 'GIF' },
  'image/webp': { extensions: ['.webp'], label: 'WebP' },
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': { extensions: ['.docx'], label: 'Word' },
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': { extensions: ['.xlsx'], label: 'Excel' },
  'application/vnd.openxmlformats-officedocument.presentationml.presentation': { extensions: ['.pptx'], label: 'PowerPoint' },
  'application/msword': { extensions: ['.doc'], label: 'Word' },
  'application/vnd.ms-excel': { extensions: ['.xls'], label: 'Excel' },
  'text/csv': { extensions: ['.csv'], label: 'CSV' },
  'text/tab-separated-values': { extensions: ['.tsv'], label: 'TSV' },
  'text/plain': { extensions: ['.txt'], label: 'Text' },
  'text/markdown': { extensions: ['.md', '.markdown'], label: 'Markdown' },
  'application/json': { extensions: ['.json'], label: 'JSON' },
  'application/xml': { extensions: ['.xml'], label: 'XML' }
};

/** Types that say nothing about the content, whatever the file's extension. */
const genericTypes = new Set([unknownMediaType, 'binary/octet-stream', 'application/unknown']);

/**
 * The types browsers commonly give a file of an extension instead of its own type: the program
 * that opens it, as Windows does with `application/vnd.ms-excel` for `.csv`, an outdated name or
 * the container format.
 */
const aliasesByExtension: Record<string, string[]> = {
  '.csv': [
    'application/vnd.ms-excel',
    'application/csv',
    'application/x-csv',
    'text/x-csv',
    'text/comma-separated-values',
    'text/x-comma-separated-values'
  ],
  '.md': ['text/x-markdown'],
  '.markdown': ['text/x-markdown'],
  '.jpg': ['image/jpg', 'image/pjpeg'],
  '.jpeg': ['image/jpg', 'image/pjpeg'],
  '.png': ['image/x-png'],
  '.docx': zipAliases,
  '.xlsx': zipAliases,
  '.pptx': zipAliases,
  // Browsers send the older name of XML; an application that accepts only it keeps receiving it.
  '.xml': ['text/xml']
};

/** The extension of a file name in lowercase, such as `.csv`; empty without one. */
function extensionOf(name: string): string {
  const base = name.slice(Math.max(name.lastIndexOf('/'), name.lastIndexOf('\\')) + 1);
  const dot = base.lastIndexOf('.');
  return dot > 0 || (dot === 0 && base.length > 1) ? base.slice(dot).toLowerCase() : '';
}

/** The application's extensions (`AgentUploads.mappedExtensions`) whose type is accepted, lowercase. */
function acceptedMappings(
  acceptedMediaTypes: readonly string[],
  mappedExtensions: readonly AgentUploadExtension[] | null | undefined
): AgentUploadExtension[] {
  return (mappedExtensions ?? [])
    .map(mapping => ({ extension: mapping.extension.toLowerCase(), mediaType: mapping.mediaType.toLowerCase() }))
    .filter(mapping => acceptedMediaTypes.includes(mapping.mediaType));
}

/** The platform's media type of an extension, or `null`. */
function knownMediaTypeOf(extension: string): string | null {
  for (const [mediaType, known] of Object.entries(knownTypes)) {
    if (known.extensions.includes(extension)) {
      return mediaType;
    }
  }

  return null;
}

/**
 * The accepted type an extension names: the application's mapping, otherwise the platform's table;
 * `null` when neither names an accepted type.
 */
function mediaTypeOfExtension(extension: string, acceptedMediaTypes: readonly string[], mappings: readonly AgentUploadExtension[]): string | null {
  if (extension.length === 0) {
    return null;
  }
  const mapped = mappings.find(mapping => mapping.extension === extension);
  if (mapped) {
    return mapped.mediaType;
  }

  const known = knownMediaTypeOf(extension);
  return known && acceptedMediaTypes.includes(known) ? known : null;
}

/**
 * The media type a chosen file uploads as, lowercase and without parameters, by the same rule as the
 * server. A browser gives no type, or a generic one, for a file its system does not know, and some
 * systems name an extension after the program that opens it, such as `application/vnd.ms-excel` for
 * a `.csv` file; the file name's extension then names the type, but only a type of
 * `acceptedMediaTypes`: the type the application mapped the extension to (`mappedExtensions`),
 * otherwise the platform's. A specific type stays as it is, so a type that is not accepted is refused
 * whatever the extension. Without a type and an accepted extension, `application/octet-stream`.
 */
export function nhAssistantMediaTypeOf(
  file: Pick<File, 'name' | 'type'>,
  acceptedMediaTypes: readonly string[],
  mappedExtensions?: readonly AgentUploadExtension[] | null
): string {
  const declared = file.type.split(';')[0].trim().toLowerCase();
  const extension = extensionOf(file.name);
  if (declared.length === 0 || genericTypes.has(declared) || (aliasesByExtension[extension] ?? []).includes(declared)) {
    const derived = mediaTypeOfExtension(extension, acceptedMediaTypes, acceptedMappings(acceptedMediaTypes, mappedExtensions));
    if (derived) {
      return derived;
    }
  }

  return declared.length > 0 ? declared : unknownMediaType;
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

/**
 * The `accept` attribute of a file input for these accepted media types: each type and the extensions
 * that name it, those the application mapped to it (`mappedExtensions`) and those of the platform's
 * table that the application did not map to another accepted type.
 */
export function nhAssistantAcceptAttribute(
  mediaTypes: readonly string[],
  mappedExtensions?: readonly AgentUploadExtension[] | null
): string {
  const mappings = acceptedMappings(mediaTypes, mappedExtensions);
  const values = mediaTypes.flatMap(mediaType => [
    mediaType,
    ...mappings.filter(mapping => mapping.mediaType === mediaType).map(mapping => mapping.extension),
    ...(knownTypes[mediaType]?.extensions ?? []).filter(extension => !mappings.some(mapping => mapping.extension === extension))
  ]);
  return [...new Set(values)].join(',');
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
