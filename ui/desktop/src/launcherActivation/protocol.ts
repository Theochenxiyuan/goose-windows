export const DESKTOP_ACTIVATION_PROTOCOL_VERSION = 2;
export const MAX_ACTIVATION_PAYLOAD_BYTES = 256 * 1024;
export const MAX_ACTIVATION_PROMPT_LENGTH = 64 * 1024;
export const MAX_ACTIVATION_FILES = 8;
export const MAX_ACTIVATION_PATH_LENGTH = 32_767;

export type DesktopActivationAction = 'ping' | 'capabilities' | 'run' | 'open';
export type LauncherThinkingEffort = 'off' | 'low' | 'medium' | 'high' | 'max';

export interface LauncherSessionSelection {
  provider: string;
  model: string;
  thinkingEffort?: LauncherThinkingEffort;
}

export interface DesktopActivationRequest {
  protocolVersion: number;
  requestId: string;
  action: DesktopActivationAction;
  authToken: string;
  cwd?: string;
  prompt?: string;
  files: string[];
  bringToFront: boolean;
  sessionSelection?: LauncherSessionSelection;
}

export interface DesktopActivationCapabilities {
  actions: DesktopActivationAction[];
  maxPayloadBytes: number;
  maxPromptLength: number;
  maxFiles: number;
  sessionSelection: boolean;
}

export interface DesktopActivationResponse {
  protocolVersion: number;
  requestId: string;
  status: 'accepted' | 'rejected';
  code?: string;
  message?: string;
  capabilities?: DesktopActivationCapabilities;
}

export class DesktopActivationProtocolError extends Error {
  constructor(
    readonly code: string,
    message: string
  ) {
    super(message);
  }
}

function requireRecord(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new DesktopActivationProtocolError(
      'invalid_request',
      'Activation request must be an object.'
    );
  }
  return value as Record<string, unknown>;
}

function requireString(
  record: Record<string, unknown>,
  key: string,
  maximumLength: number
): string {
  const value = record[key];
  if (typeof value !== 'string' || value.length === 0 || value.length > maximumLength) {
    throw new DesktopActivationProtocolError(
      'invalid_request',
      `${key} must be a non-empty string of at most ${maximumLength} characters.`
    );
  }
  return value;
}

function optionalString(
  record: Record<string, unknown>,
  key: string,
  maximumLength: number
): string | undefined {
  const value = record[key];
  if (value === undefined) return undefined;
  if (typeof value !== 'string' || value.length > maximumLength) {
    throw new DesktopActivationProtocolError(
      'invalid_request',
      `${key} must be a string of at most ${maximumLength} characters.`
    );
  }
  return value;
}

function optionalSessionSelection(
  record: Record<string, unknown>
): LauncherSessionSelection | undefined {
  const value = record.sessionSelection;
  if (value === undefined) return undefined;
  const selection = requireRecord(value);
  const provider = requireString(selection, 'provider', 256);
  const model = requireString(selection, 'model', 512);
  const thinkingEffort = optionalString(selection, 'thinkingEffort', 16);
  if (
    thinkingEffort !== undefined &&
    !['off', 'low', 'medium', 'high', 'max'].includes(thinkingEffort)
  ) {
    throw new DesktopActivationProtocolError('invalid_request', 'thinkingEffort is not supported.');
  }
  return {
    provider,
    model,
    thinkingEffort: thinkingEffort as LauncherThinkingEffort | undefined,
  };
}

export function parseDesktopActivationRequest(payload: Buffer): DesktopActivationRequest {
  if (payload.length === 0 || payload.length > MAX_ACTIVATION_PAYLOAD_BYTES) {
    throw new DesktopActivationProtocolError(
      'invalid_length',
      'Activation payload length is invalid.'
    );
  }

  let value: unknown;
  try {
    value = JSON.parse(payload.toString('utf8'));
  } catch {
    throw new DesktopActivationProtocolError(
      'invalid_json',
      'Activation payload is not valid JSON.'
    );
  }

  const record = requireRecord(value);
  if (record.protocolVersion !== DESKTOP_ACTIVATION_PROTOCOL_VERSION) {
    throw new DesktopActivationProtocolError(
      'unsupported_version',
      `Desktop activation protocol ${String(record.protocolVersion)} is not supported.`
    );
  }

  const requestId = requireString(record, 'requestId', 128);
  if (!/^[A-Za-z0-9._-]+$/.test(requestId)) {
    throw new DesktopActivationProtocolError(
      'invalid_request',
      'requestId contains invalid characters.'
    );
  }

  const action = requireString(record, 'action', 32);
  if (!['ping', 'capabilities', 'run', 'open'].includes(action)) {
    throw new DesktopActivationProtocolError(
      'unsupported_action',
      'Activation action is not supported.'
    );
  }

  const authToken = requireString(record, 'authToken', 128);
  const cwd = optionalString(record, 'cwd', MAX_ACTIVATION_PATH_LENGTH);
  const prompt = optionalString(record, 'prompt', MAX_ACTIVATION_PROMPT_LENGTH);
  const filesValue = record.files ?? [];
  if (!Array.isArray(filesValue) || filesValue.length > MAX_ACTIVATION_FILES) {
    throw new DesktopActivationProtocolError(
      'invalid_request',
      `files must contain at most ${MAX_ACTIVATION_FILES} paths.`
    );
  }
  const files = filesValue.map((file) => {
    if (typeof file !== 'string' || file.length === 0 || file.length > MAX_ACTIVATION_PATH_LENGTH) {
      throw new DesktopActivationProtocolError(
        'invalid_request',
        'files contains an invalid path.'
      );
    }
    return file;
  });

  if (typeof record.bringToFront !== 'boolean') {
    throw new DesktopActivationProtocolError('invalid_request', 'bringToFront must be a boolean.');
  }
  const sessionSelection = optionalSessionSelection(record);
  if (action === 'run' && (!cwd || !prompt?.trim())) {
    throw new DesktopActivationProtocolError('invalid_request', 'run requires cwd and prompt.');
  }
  if (action !== 'run' && files.length > 0) {
    throw new DesktopActivationProtocolError('invalid_request', 'Only run accepts files.');
  }
  if (action !== 'run' && sessionSelection) {
    throw new DesktopActivationProtocolError(
      'invalid_request',
      'Only run accepts sessionSelection.'
    );
  }

  return {
    protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
    requestId,
    action: action as DesktopActivationAction,
    authToken,
    cwd,
    prompt,
    files,
    bringToFront: record.bringToFront,
    sessionSelection,
  };
}

export function encodeDesktopActivationFrame(value: unknown): Buffer {
  const payload = Buffer.from(JSON.stringify(value), 'utf8');
  if (payload.length === 0 || payload.length > MAX_ACTIVATION_PAYLOAD_BYTES) {
    throw new DesktopActivationProtocolError(
      'invalid_length',
      'Activation payload length is invalid.'
    );
  }
  const frame = Buffer.allocUnsafe(4 + payload.length);
  frame.writeUInt32LE(payload.length, 0);
  payload.copy(frame, 4);
  return frame;
}

export class DesktopActivationFrameDecoder {
  private buffered: Buffer<ArrayBufferLike> = Buffer.alloc(0);

  push(chunk: Buffer): Buffer[] {
    this.buffered = this.buffered.length === 0 ? chunk : Buffer.concat([this.buffered, chunk]);
    const frames: Buffer[] = [];

    while (this.buffered.length >= 4) {
      const length = this.buffered.readUInt32LE(0);
      if (length === 0 || length > MAX_ACTIVATION_PAYLOAD_BYTES) {
        throw new DesktopActivationProtocolError(
          'invalid_length',
          'Activation frame length is invalid.'
        );
      }
      if (this.buffered.length < length + 4) break;
      frames.push(this.buffered.subarray(4, length + 4));
      this.buffered = this.buffered.subarray(length + 4);
    }

    if (this.buffered.length > MAX_ACTIVATION_PAYLOAD_BYTES + 4) {
      throw new DesktopActivationProtocolError('invalid_length', 'Activation frame is too large.');
    }
    return frames;
  }
}

export function desktopActivationCapabilities(): DesktopActivationCapabilities {
  return {
    actions: ['ping', 'capabilities', 'run', 'open'],
    maxPayloadBytes: MAX_ACTIVATION_PAYLOAD_BYTES,
    maxPromptLength: MAX_ACTIVATION_PROMPT_LENGTH,
    maxFiles: MAX_ACTIVATION_FILES,
    sessionSelection: true,
  };
}
