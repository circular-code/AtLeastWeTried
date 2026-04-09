import type { ClientMessage, PongMessage, ServerMessage } from '../types/generated';

export type ClientConnectionState = 'idle' | 'connecting' | 'open' | 'closed' | 'error';

type Handlers = {
  onStateChange?: (state: ClientConnectionState) => void;
  onMessage?: (message: ServerMessage) => void;
  onSend?: (message: ClientMessage) => void;
};

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
    setState('connecting');

    socket.addEventListener('open', () => {
      setState('open');
    });

    socket.addEventListener('message', (event) => {
      try {
        const parsed = JSON.parse(event.data) as ServerMessage;
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

  function send(message: ClientMessage) {
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      return false;
    }

    handlers.onSend?.(message);
    socket.send(JSON.stringify(message));
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