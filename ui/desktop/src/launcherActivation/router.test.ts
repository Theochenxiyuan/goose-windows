import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import type { DesktopActivationRequest } from './protocol';
import { DESKTOP_ACTIVATION_PROTOCOL_VERSION } from './protocol';
import { DesktopActivationRouter, type LauncherChatOptions } from './router';

describe('Desktop activation routing', () => {
  it('creates a new auto-submitting chat with the requested cwd and files', async () => {
    const cwd = await fs.mkdtemp(path.join(os.tmpdir(), 'goose-launcher-测试-'));
    const file = path.join(cwd, '示例 file.txt');
    await fs.writeFile(file, 'test');
    let options: LauncherChatOptions | undefined;
    const window = {
      isDestroyed: () => false,
      isMinimized: () => false,
      restore: vi.fn(),
      show: vi.fn(),
      focus: vi.fn(),
      moveTop: vi.fn(),
    };
    const router = new DesktopActivationRouter(async (value) => {
      options = value;
      return window;
    });
    const request: DesktopActivationRequest = {
      protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
      requestId: 'request-1',
      action: 'run',
      authToken: 'unused',
      cwd,
      prompt: '检查文件',
      files: [file],
      bringToFront: true,
    };

    await expect(router.route(request)).resolves.toMatchObject({ status: 'accepted' });
    expect(options).toMatchObject({
      dir: cwd,
      initialMessageNoAutoSubmit: false,
    });
    expect(options?.initialMessage).toContain(file);
    expect(window.show).toHaveBeenCalledOnce();
    expect(window.focus).toHaveBeenCalledOnce();
    await fs.rm(cwd, { recursive: true });
  });

  it('rejects a file outside cwd', async () => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'goose-launcher-paths-'));
    const cwd = path.join(root, 'cwd');
    await fs.mkdir(cwd);
    const file = path.join(root, 'outside.txt');
    await fs.writeFile(file, 'test');
    const router = new DesktopActivationRouter(async () => undefined);

    await expect(
      router.route({
        protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
        requestId: 'request-2',
        action: 'run',
        authToken: 'unused',
        cwd,
        prompt: 'test',
        files: [file],
        bringToFront: true,
      })
    ).rejects.toMatchObject({ code: 'invalid_path' });
    await fs.rm(root, { recursive: true });
  });
});
