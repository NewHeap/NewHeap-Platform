import { NhAssistantSseParser } from './nh-assistant-sse-parser';

describe('NhAssistantSseParser', () => {
  it('parses one event with a name and JSON data', () => {
    const parser = new NhAssistantSseParser();

    const messages = parser.push('event: message.delta\ndata: {"messageId":"m1","text":"Hi"}\n\n');

    expect(messages).toEqual([{ event: 'message.delta', data: '{"messageId":"m1","text":"Hi"}' }]);
  });

  it('reassembles events split across arbitrary chunk boundaries', () => {
    const stream = 'event: turn.started\ndata: {"turnId":"t1","userMessageId":"u1","assistantMessageId":"a1"}\n\n' +
      'event: message.delta\ndata: {"messageId":"a1","text":"Hello"}\n\n';
    const expected = [
      { event: 'turn.started', data: '{"turnId":"t1","userMessageId":"u1","assistantMessageId":"a1"}' },
      { event: 'message.delta', data: '{"messageId":"a1","text":"Hello"}' }
    ];

    for (const size of [1, 2, 3, 7, 13, 50]) {
      const parser = new NhAssistantSseParser();
      const messages = [];
      for (let index = 0; index < stream.length; index += size) {
        messages.push(...parser.push(stream.slice(index, index + size)));
      }

      expect(messages).withContext(`chunk size ${size}`).toEqual(expected);
    }
  });

  it('ignores keep-alive comment lines between and inside events', () => {
    const parser = new NhAssistantSseParser();

    const messages = [
      ...parser.push(': keep-alive\n\n'),
      ...parser.push('event: tool.completed\n: keep-alive\ndata: {"invocationId":"i1"}\n\n'),
      ...parser.push(': keep-alive\n\n')
    ];

    expect(messages).toEqual([{ event: 'tool.completed', data: '{"invocationId":"i1"}' }]);
  });

  it('accepts CRLF and CR line terminators, also when a CRLF is split', () => {
    const parser = new NhAssistantSseParser();

    const messages = [
      ...parser.push('event: error\r'),
      ...parser.push('\ndata: {"code":"x","messageKey":"k"}\r\n\r'),
      ...parser.push('\nevent: error\rdata: {"code":"y","messageKey":"k"}\r\r'),
      ...parser.end()
    ];

    expect(messages).toEqual([
      { event: 'error', data: '{"code":"x","messageKey":"k"}' },
      { event: 'error', data: '{"code":"y","messageKey":"k"}' }
    ]);
  });

  it('joins multiple data lines with a newline and strips one leading space', () => {
    const parser = new NhAssistantSseParser();

    const messages = parser.push('event: message.delta\ndata:{"a":\ndata:  1}\n\n');

    expect(messages).toEqual([{ event: 'message.delta', data: '{"a":\n 1}' }]);
  });

  it('does not dispatch an event without data and ignores id and retry fields', () => {
    const parser = new NhAssistantSseParser();

    const messages = parser.push('event: turn.started\n\nid: 5\nretry: 1000\nevent: error\ndata: {}\n\n');

    expect(messages).toEqual([{ event: 'error', data: '{}' }]);
  });

  it('discards an unterminated event at the end of the stream', () => {
    const parser = new NhAssistantSseParser();

    const messages = [...parser.push('event: message.delta\ndata: {"text":"cut'), ...parser.end()];

    expect(messages).toEqual([]);
  });
});
