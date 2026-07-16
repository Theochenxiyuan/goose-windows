import { describe, expect, it, vi } from 'vitest';
import {
  DESKTOP_ACTIVATION_PROTOCOL_VERSION,
  type DesktopActivationRequest,
  type DesktopActivationResponse,
} from './protocol';
import type { DesktopActivationRouter } from './router';
import { DesktopActivationServer } from './server';

describe('Desktop activation server', () => {
  it('rejects authentication failures before routing', async () => {
    const route = vi.fn();
    const server = new DesktopActivationServer({
      router: { route } as unknown as DesktopActivationRouter,
    });
    const internals = server as unknown as {
      handlePayload: (payload: Buffer) => Promise<DesktopActivationResponse>;
    };
    const payload = Buffer.from(
      JSON.stringify({
        protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
        requestId: 'unauthorized-request',
        action: 'ping',
        authToken: 'not-the-server-token',
        files: [],
        bringToFront: false,
      })
    );

    await expect(internals.handlePayload(payload)).resolves.toMatchObject({
      status: 'rejected',
      code: 'unauthorized',
    });
    expect(route).not.toHaveBeenCalled();
  });

  it('shares one in-flight response for concurrent duplicate request IDs', async () => {
    let finishRoute!: (response: DesktopActivationResponse) => void;
    const route = vi.fn(
      () =>
        new Promise<DesktopActivationResponse>((resolve) => {
          finishRoute = resolve;
        })
    );
    const server = new DesktopActivationServer({
      router: { route } as unknown as DesktopActivationRouter,
    });
    const internals = server as unknown as {
      authToken: string;
      handlePayload: (payload: Buffer) => Promise<DesktopActivationResponse>;
    };
    const request: DesktopActivationRequest = {
      protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
      requestId: 'same-request',
      action: 'ping',
      authToken: internals.authToken,
      files: [],
      bringToFront: false,
    };
    const payload = Buffer.from(JSON.stringify(request));

    const first = internals.handlePayload(payload);
    const second = internals.handlePayload(payload);
    expect(route).toHaveBeenCalledOnce();

    finishRoute({
      protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
      requestId: request.requestId,
      status: 'accepted',
    });
    await expect(Promise.all([first, second])).resolves.toEqual([
      expect.objectContaining({ status: 'accepted' }),
      expect.objectContaining({ status: 'accepted' }),
    ]);
    expect(route).toHaveBeenCalledOnce();
  });

  it('reuses a completed response for a later duplicate request ID', async () => {
    const route = vi.fn(
      async (): Promise<DesktopActivationResponse> => ({
        protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
        requestId: 'completed-request',
        status: 'accepted',
      })
    );
    const server = new DesktopActivationServer({
      router: { route } as unknown as DesktopActivationRouter,
    });
    const internals = server as unknown as {
      authToken: string;
      handlePayload: (payload: Buffer) => Promise<DesktopActivationResponse>;
    };
    const payload = Buffer.from(
      JSON.stringify({
        protocolVersion: DESKTOP_ACTIVATION_PROTOCOL_VERSION,
        requestId: 'completed-request',
        action: 'ping',
        authToken: internals.authToken,
        files: [],
        bringToFront: false,
      })
    );

    await internals.handlePayload(payload);
    await internals.handlePayload(payload);
    expect(route).toHaveBeenCalledOnce();
  });
});
