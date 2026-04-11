import { encode, decode } from '@msgpack/msgpack';
import type { ClientMessage, PongMessage, ServerMessage } from '../types/generated';
import type { ClearTacticalTargetCommandMessage, SetTacticalModeCommandMessage, SetTacticalTargetCommandMessage, UpgradeSubsystemCommandMessage } from './commands';

export type ClientConnectionState = 'idle' | 'connecting' | 'open' | 'closed' | 'error';

type Handlers = {
  onStateChange?: (state: ClientConnectionState) => void;
  onMessage?: (message: ServerMessage) => void;
  onSend?: (message: OutgoingMessage) => void;
};

export type OutgoingMessage = ClientMessage | SetTacticalModeCommandMessage | SetTacticalTargetCommandMessage | ClearTacticalTargetCommandMessage | UpgradeSubsystemCommandMessage;

function toCamelCaseKey(key: string): string {
  return key.charAt(0).toLowerCase() + key.slice(1);
}

function transformKeys(obj: unknown): unknown {
  if (Array.isArray(obj)) {
    return obj.map(transformKeys);
  }
  if (obj !== null && typeof obj === 'object') {
    const result: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(obj)) {
      result[toCamelCaseKey(key)] = transformKeys(value);
    }
    return result;
  }
  return obj;
}

export function createGatewayClient(url: string, handlers: Handlers) {
  let socket: WebSocket | null = null;

  function setState(state: ClientConnectionState) {
    handlers.onStateChange?.(state);
  }

  function connect() {
    if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) {
      return;
    }

    socket = new WebSocket(url);
    socket.binaryType = 'arraybuffer';
    setState('connecting');

    socket.addEventListener('open', () => {
      setState('open');
    });

    socket.addEventListener('message', (event) => {
      try {
        const raw = decode(new Uint8Array(event.data as ArrayBuffer));
        const parsed = transformKeys(raw) as ServerMessage;
        handlers.onMessage?.(parsed);

        if (parsed.type === 'ping') {
          const pong: PongMessage = { type: 'pong' };
          send(pong);
        }
      } catch {
        setState('error');
      }
    });

    socket.addEventListener('close', () => {
      setState('closed');
    });

    socket.addEventListener('error', () => {
      setState('error');
    });
  }

  function send(message: OutgoingMessage) {
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      return false;
    }

    handlers.onSend?.(message);
    socket.send(encode(message));
    return true;
  }

  function disconnect() {
    if (!socket) {
      return;
    }

    const currentSocket = socket;
    socket = null;
    currentSocket.close();
  }

  return {
    connect,
    disconnect,
    send,
    isOpen() {
      return socket?.readyState === WebSocket.OPEN;
    },
  };
}
