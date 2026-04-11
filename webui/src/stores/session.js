import { defineStore } from 'pinia';
export const useSessionStore = defineStore('session', {
    state: () => ({
        gatewayUrl: import.meta.env.VITE_GATEWAY_URL ?? 'ws://127.0.0.1:5260/ws',
        connectionState: 'idle',
        connectionId: '',
        protocolVersion: '',
        observerOnly: false,
        playerSessions: [],
    }),
    getters: {
        selectedPlayerSession: (state) => state.playerSessions.find((player) => player.selected) ?? null,
        attachedPlayerSessions: (state) => state.playerSessions,
        attachedSessionIds: (state) => state.playerSessions.map((player) => player.playerSessionId),
        connectionStateLabel: (state) => {
            switch (state.connectionState) {
                case 'open':
                    return 'Connected';
                case 'connecting':
                    return 'Connecting…';
                case 'closed':
                    return 'Disconnected';
                case 'error':
                    return 'Connection error';
                default:
                    return 'Idle';
            }
        },
        showDisconnectAction: (state) => state.connectionState === 'open' || state.connectionState === 'connecting',
    },
    actions: {
        setConnectionState(state) {
            this.connectionState = state;
        },
        applySessionReady(message) {
            this.connectionId = message.connectionId;
            this.protocolVersion = message.protocolVersion;
            this.observerOnly = message.observerOnly;
            this.playerSessions = message.playerSessions.map((player) => ({ ...player }));
        },
        clearSession() {
            this.connectionId = '';
            this.protocolVersion = '';
            this.observerOnly = false;
            this.playerSessions = [];
        },
    },
});
