import fs from 'node:fs/promises';
import path from 'node:path';
import {
  DESKTOP_ACTIVATION_PROTOCOL_VERSION,
  DesktopActivationProtocolError,
  type DesktopActivationRequest,
  type DesktopActivationResponse,
  type LauncherSessionSelection,
  desktopActivationCapabilities,
} from './protocol';

export interface LauncherChatWindow {
  isDestroyed(): boolean;
  isMinimized(): boolean;
  restore(): void;
  show(): void;
  focus(): void;
  moveTop(): void;
}

export interface LauncherChatOptions {
  initialMessage?: string;
  initialMessageNoAutoSubmit?: boolean;
  dir?: string;
  launcherSessionSelection?: LauncherSessionSelection;
}

export type CreateLauncherChat = (
  options: LauncherChatOptions
) => Promise<LauncherChatWindow | undefined>;

function samePath(left: string, right: string): boolean {
  return process.platform === 'win32'
    ? left.localeCompare(right, undefined, { sensitivity: 'accent' }) === 0
    : left === right;
}

async function validatePaths(request: DesktopActivationRequest): Promise<string> {
  const cwd = path.resolve(request.cwd!);
  if (!path.isAbsolute(request.cwd!)) {
    throw new DesktopActivationProtocolError('invalid_path', 'cwd must be an absolute path.');
  }
  const cwdStats = await fs.stat(cwd).catch(() => null);
  if (!cwdStats?.isDirectory()) {
    throw new DesktopActivationProtocolError('invalid_path', 'cwd is not an accessible directory.');
  }

  for (const file of request.files) {
    if (!path.isAbsolute(file)) {
      throw new DesktopActivationProtocolError(
        'invalid_path',
        'Every file must use an absolute path.'
      );
    }
    const fullPath = path.resolve(file);
    const fileStats = await fs.stat(fullPath).catch(() => null);
    if (!fileStats?.isFile() || !samePath(path.dirname(fullPath), cwd)) {
      throw new DesktopActivationProtocolError(
        'invalid_path',
        'Every file must exist directly inside cwd.'
      );
    }
  }
  return cwd;
}

export function buildLauncherInitialMessage(prompt: string, files: string[]): string {
  if (files.length === 0) return prompt;
  return `${prompt.trimEnd()}\n\nUser-selected files (exact paths; treat these as explicit inputs to the task):\n${files
    .map((file, index) => `${index + 1}. ${path.resolve(file)}`)
    .join('\n')}`;
}

export class DesktopActivationRouter {
  constructor(private readonly createChat: CreateLauncherChat) {}

  async route(request: DesktopActivationRequest): Promise<DesktopActivationResponse> {
    if (request.action === 'ping' || request.action === 'capabilities') {
      return {
        protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
        requestId: request.requestId,
        status: 'accepted',
        capabilities: desktopActivationCapabilities(),
      };
    }

    let cwd: string | undefined;
    if (request.cwd) {
      cwd = await validatePaths(request);
    }

    const window = await this.createChat({
      dir: cwd,
      initialMessage:
        request.action === 'run'
          ? buildLauncherInitialMessage(request.prompt!, request.files)
          : undefined,
      initialMessageNoAutoSubmit: false,
      launcherSessionSelection: request.sessionSelection,
    });
    if (!window || window.isDestroyed()) {
      throw new DesktopActivationProtocolError(
        'window_creation_failed',
        'Goose could not create a window.'
      );
    }
    if (request.bringToFront) {
      if (window.isMinimized()) window.restore();
      window.show();
      window.focus();
      window.moveTop();
    }

    return {
      protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
      requestId: request.requestId,
      status: 'accepted',
    };
  }
}
