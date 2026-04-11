export function createGatewayClient(url, handlers) {
    let socket = null;
    function setState(state) {
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
                const parsed = JSON.parse(event.data);
                handlers.onMessage?.(parsed);
                if (parsed.type === 'ping') {
                    const pong = { type: 'pong' };
                    send(pong);
                }
            }
            catch {
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
    function send(message) {
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
