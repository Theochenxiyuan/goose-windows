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
      return { window, activationAccepted: Promise.resolve() };
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
      sessionSelection: {
        provider: 'chatgpt_codex',
        model: 'gpt-5.4',
        thinkingEffort: 'high',
      },
    };

    await expect(router.route(request)).resolves.toMatchObject({ status: 'accepted' });
    expect(options).toMatchObject({
      dir: cwd,
      initialMessageNoAutoSubmit: false,
      launcherRequestId: 'request-1',
      launcherSessionSelection: {
        provider: 'chatgpt_codex',
        model: 'gpt-5.4',
        thinkingEffort: 'high',
      },
    });
    expect(options?.initialMessage).toContain(file);
    expect(window.show).toHaveBeenCalledOnce();
    expect(window.focus).toHaveBeenCalledOnce();
    await fs.rm(cwd, { recursive: true });
  });

  it('does not accept a run until the renderer confirms submission started', async () => {
    const cwd = await fs.mkdtemp(path.join(os.tmpdir(), 'goose-launcher-ack-'));
    let confirmSubmission!: () => void;
    const activationAccepted = new Promise<void>((resolve) => {
      confirmSubmission = resolve;
    });
    const routeSettled = vi.fn();
    const router = new DesktopActivationRouter(async () => ({
      window: {
        isDestroyed: () => false,
        isMinimized: () => false,
        restore: vi.fn(),
        show: vi.fn(),
        focus: vi.fn(),
        moveTop: vi.fn(),
      },
      activationAccepted,
    }));

    const routing = router
      .route({
        protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
        requestId: 'request-ack',
        action: 'run',
        authToken: 'unused',
        cwd,
        prompt: 'test',
        files: [],
        bringToFront: true,
      })
      .then(routeSettled);

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(routeSettled).not.toHaveBeenCalled();
    confirmSubmission();
    await routing;
    expect(routeSettled).toHaveBeenCalledWith(
      expect.objectContaining({ requestId: 'request-ack', status: 'accepted' })
    );
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
