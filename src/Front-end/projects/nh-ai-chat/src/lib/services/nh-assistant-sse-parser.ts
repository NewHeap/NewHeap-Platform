/** One raw server-sent event before its data is interpreted. */
export interface NhAssistantRawSseMessage {
  event: string;
  data: string;
}

/**
 * Incremental parser for the `text/event-stream` format.
 *
 * Feed decoded text chunks in arrival order. Chunks may split lines, fields and line
 * terminators anywhere. Comment lines (starting with `:`, such as the `: keep-alive`
 * heartbeat) are ignored; `id` and `retry` fields are accepted and ignored. An event is
 * dispatched at the empty line that terminates it. An unterminated event at the end of
 * the stream is discarded, as the SSE specification requires.
 */
export class NhAssistantSseParser {
  private buffer = '';
  private eventName = '';
  private dataLines: string[] = [];

  push(chunk: string): NhAssistantRawSseMessage[] {
    this.buffer += chunk;

    const messages: NhAssistantRawSseMessage[] = [];
    let lineStart = 0;

    for (let index = 0; index < this.buffer.length; index++) {
      const character = this.buffer[index];
      if (character !== '\n' && character !== '\r') {
        continue;
      }

      if (character === '\r' && index === this.buffer.length - 1) {
        // A trailing CR may be the first half of a CRLF split across chunks.
        break;
      }

      const line = this.buffer.slice(lineStart, index);
      if (character === '\r' && this.buffer[index + 1] === '\n') {
        index++;
      }
      lineStart = index + 1;

      const message = this.processLine(line);
      if (message) {
        messages.push(message);
      }
    }

    this.buffer = this.buffer.slice(lineStart);
    return messages;
  }

  /** Signals the end of the stream and returns an event that a trailing CR completed. */
  end(): NhAssistantRawSseMessage[] {
    const messages: NhAssistantRawSseMessage[] = [];
    if (this.buffer.endsWith('\r')) {
      const message = this.processLine(this.buffer.slice(0, -1));
      if (message) {
        messages.push(message);
      }
    }

    this.buffer = '';
    this.eventName = '';
    this.dataLines = [];
    return messages;
  }

  private processLine(line: string): NhAssistantRawSseMessage | null {
    if (line.length === 0) {
      return this.dispatch();
    }

    if (line.startsWith(':')) {
      return null;
    }

    const colonIndex = line.indexOf(':');
    const field = colonIndex < 0 ? line : line.slice(0, colonIndex);
    let value = colonIndex < 0 ? '' : line.slice(colonIndex + 1);
    if (value.startsWith(' ')) {
      value = value.slice(1);
    }

    if (field === 'event') {
      this.eventName = value;
    } else if (field === 'data') {
      this.dataLines.push(value);
    }

    return null;
  }

  private dispatch(): NhAssistantRawSseMessage | null {
    const hasData = this.dataLines.length > 0;
    const message: NhAssistantRawSseMessage = {
      event: this.eventName || 'message',
      data: this.dataLines.join('\n')
    };

    this.eventName = '';
    this.dataLines = [];

    return hasData ? message : null;
  }
}
