import { describe, expect, it } from 'vitest';
import {
  DESKTOP_ACTIVATION_PROTOCOL_VERSION,
  DesktopActivationFrameDecoder,
  DesktopActivationProtocolError,
  encodeDesktopActivationFrame,
  parseDesktopActivationRequest,
} from './protocol';

function request(overrides: Record<string, unknown> = {}) {
  return {
    protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
    requestId: '7fe8ba7e-95fd-4c9b-8933-f40881273729',
    action: 'run',
    authToken: 'a'.repeat(64),
    cwd: 'C:\\测试 工作区',
    prompt: '检查这些文件',
    files: ['C:\\测试 工作区\\示例.txt'],
    bringToFront: true,
    ...overrides,
  };
}

describe('Desktop activation protocol', () => {
  it('decodes fragmented Unicode frames', () => {
    const frame = encodeDesktopActivationFrame(request());
    const decoder = new DesktopActivationFrameDecoder();
    const payloads = [
      ...decoder.push(frame.subarray(0, 2)),
      ...decoder.push(frame.subarray(2, 11)),
      ...decoder.push(frame.subarray(11)),
    ];

    expect(payloads).toHaveLength(1);
    expect(parseDesktopActivationRequest(payloads[0])).toMatchObject({
      cwd: 'C:\\测试 工作区',
      prompt: '检查这些文件',
    });
  });

  it('rejects incompatible versions', () => {
    const payload = Buffer.from(JSON.stringify(request({ protocolVersion: 2 })));
    expect(() => parseDesktopActivationRequest(payload)).toThrowError(
      expect.objectContaining<Partial<DesktopActivationProtocolError>>({
        code: 'unsupported_version',
      })
    );
  });

  it('rejects run without a prompt', () => {
    const payload = Buffer.from(JSON.stringify(request({ prompt: '' })));
    expect(() => parseDesktopActivationRequest(payload)).toThrowError(
      expect.objectContaining<Partial<DesktopActivationProtocolError>>({ code: 'invalid_request' })
    );
  });

  it('decodes multiple frames from one chunk', () => {
    const decoder = new DesktopActivationFrameDecoder();
    const frames = decoder.push(
      Buffer.concat([
        encodeDesktopActivationFrame(request()),
        encodeDesktopActivationFrame(request()),
      ])
    );
    expect(frames).toHaveLength(2);
  });
});
