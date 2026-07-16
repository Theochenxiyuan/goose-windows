import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import {
  DESKTOP_ACTIVATION_PROTOCOL_VERSION,
  DesktopActivationFrameDecoder,
  DesktopActivationProtocolError,
  type DesktopActivationRequest,
  type DesktopActivationResponse,
  encodeDesktopActivationFrame,
  parseDesktopActivationRequest,
} from './protocol';
import { DesktopActivationRouter } from './router';

export interface DesktopActivationEndpoint {
  protocolVersion: number;
  pid: number;
  pipeName: string;
  authToken: string;
}

interface DesktopActivationServerOptions {
  router: DesktopActivationRouter;
  onAccepted?: (request: DesktopActivationRequest) => void;
  onHandled?: (request: DesktopActivationRequest) => void;
  onError?: (code: string) => void;
}

function endpointPath(): string {
  const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local');
  return path.join(localAppData, 'Goose', 'launcher', 'desktop-activation.json');
}

async function publishEndpoint(endpoint: DesktopActivationEndpoint): Promise<void> {
  const file = endpointPath();
  await fs.mkdir(path.dirname(file), { recursive: true });
  const temporary = `${file}.${process.pid}.tmp`;
  await fs.writeFile(temporary, JSON.stringify(endpoint), { encoding: 'utf8', mode: 0o600 });
  await fs.rm(file, { force: true });
  await fs.rename(temporary, file);
}

async function removeEndpoint(authToken: string): Promise<void> {
  const file = endpointPath();
  try {
    const endpoint = JSON.parse(await fs.readFile(file, 'utf8')) as DesktopActivationEndpoint;
    if (endpoint.pid === process.pid && endpoint.authToken === authToken) {
      await fs.rm(file, { force: true });
    }
  } catch {
    return;
  }
}

function rejectedResponse(
  requestId: string,
  code: string,
  message: string
): DesktopActivationResponse {
  return {
    protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
    requestId,
    status: 'rejected',
    code,
    message,
  };
}

export class DesktopActivationServer {
  private readonly authToken = crypto.randomBytes(32).toString('hex');
  private readonly pipeName = `\\\\.\\pipe\\goose-desktop-${crypto.randomUUID()}`;
  private readonly responses = new Map<string, DesktopActivationResponse>();
  private readonly inFlightResponses = new Map<string, Promise<DesktopActivationResponse>>();
  private readonly sockets = new Set<net.Socket>();
  private server: net.Server | undefined;

  constructor(private readonly options: DesktopActivationServerOptions) {}

  async start(): Promise<void> {
    if (this.server) return;
    this.server = net.createServer((socket) => {
      this.sockets.add(socket);
      const decoder = new DesktopActivationFrameDecoder();
      let sequence = Promise.resolve();
      socket.on('data', (chunk) => {
        sequence = sequence
          .then(async () => {
            for (const payload of decoder.push(
              Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)
            )) {
              const response = await this.handlePayload(payload);
              socket.write(encodeDesktopActivationFrame(response));
            }
          })
          .catch((error: unknown) => {
            const code =
              error instanceof DesktopActivationProtocolError ? error.code : 'internal_error';
            this.options.onError?.(code);
            socket.write(
              encodeDesktopActivationFrame(rejectedResponse('', code, 'Request rejected.'))
            );
            socket.end();
          });
      });
      socket.on('close', () => this.sockets.delete(socket));
      socket.on('error', () => undefined);
    });

    await new Promise<void>((resolve, reject) => {
      this.server!.once('error', reject);
      this.server!.listen({ path: this.pipeName, readableAll: false, writableAll: false }, () => {
        this.server!.off('error', reject);
        resolve();
      });
    });
    try {
      await publishEndpoint({
        protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
        pid: process.pid,
        pipeName: this.pipeName,
        authToken: this.authToken,
      });
    } catch (error) {
      const server = this.server;
      this.server = undefined;
      await new Promise<void>((resolve) => {
        if (server) server.close(() => resolve());
        else resolve();
      });
      throw error;
    }
  }

  private async handlePayload(payload: Buffer): Promise<DesktopActivationResponse> {
    const request = parseDesktopActivationRequest(payload);
    const requestToken = Buffer.from(request.authToken);
    const expectedToken = Buffer.from(this.authToken);
    if (
      requestToken.length !== expectedToken.length ||
      !crypto.timingSafeEqual(requestToken, expectedToken)
    ) {
      return rejectedResponse(
        request.requestId,
        'unauthorized',
        'Activation client is not authorized.'
      );
    }

    const previous = this.responses.get(request.requestId);
    if (previous) return previous;

    const inFlight = this.inFlightResponses.get(request.requestId);
    if (inFlight) return inFlight;

    const handling = this.routeAndCache(request);
    this.inFlightResponses.set(request.requestId, handling);
    try {
      return await handling;
    } finally {
      this.inFlightResponses.delete(request.requestId);
    }
  }

  private async routeAndCache(
    request: DesktopActivationRequest
  ): Promise<DesktopActivationResponse> {
    let response: DesktopActivationResponse;
    try {
      response = await this.options.router.route(request);
    } catch (error) {
      const code = error instanceof DesktopActivationProtocolError ? error.code : 'internal_error';
      const message = error instanceof Error ? error.message : 'Activation failed.';
      response = rejectedResponse(request.requestId, code, message);
    }
    this.options.onHandled?.(request);
    this.responses.set(request.requestId, response);
    if (this.responses.size > 128) this.responses.delete(this.responses.keys().next().value!);
    if (response.status === 'accepted') this.options.onAccepted?.(request);
    return response;
  }

  async stop(): Promise<void> {
    const server = this.server;
    this.server = undefined;
    if (server) {
      for (const socket of this.sockets) socket.destroy();
      this.sockets.clear();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
    await removeEndpoint(this.authToken);
  }
}
