import { defineStore } from 'pinia';
import type { WorldSceneSelection } from '../renderer/WorldScene';
import type { ClientMessage, GatewayMessageDirection, ServerMessage } from '../types/generated';
import type { DebugLogEntry } from '../types/client';
import { formatDebugPayload } from '../lib/formatting';

const DEBUG_LOG_OPEN_STORAGE_KEY = 'flattiverse.debugLog.open';
const DEBUG_LOG_INGAME_STORAGE_KEY = 'flattiverse.debugLog.ingame';

export const useUiStore = defineStore('ui', {
  state: () => ({
    selectedControllableId: '',
    lastSelection: null as WorldSceneSelection | null,
    isManagerPopupOpen: false,
    isChatPopupOpen: false,
    isActivityHistoryOpen: false,
    isDebugLogOpen: false,
    isDebugLogIngame: false,
    debugLogEntries: [] as DebugLogEntry[],
    debugLogLimit: 200,
    debugLogSearch: '',
    debugLogExclude: '',
    showClientDebugMessages: true,
    showServerDebugMessages: true,
  }),
  getters: {
    normalizedDebugLogLimit: (state) => {
      if (!Number.isFinite(state.debugLogLimit)) {
        return 1;
      }

      return Math.max(1, Math.floor(state.debugLogLimit));
    },
    filteredDebugLogEntries(state): DebugLogEntry[] {
      const includeQuery = state.debugLogSearch.trim().toLowerCase();
      const excludeQuery = state.debugLogExclude.trim().toLowerCase();

      return state.debugLogEntries.filter((entry) => {
        if (entry.direction === 'client' && !state.showClientDebugMessages) {
          return false;
        }

        if (entry.direction === 'server' && !state.showServerDebugMessages) {
          return false;
        }

        const searchable = `${entry.messageType}\n${entry.payload}`.toLowerCase();

        if (excludeQuery && searchable.includes(excludeQuery)) {
          return false;
        }

        if (!includeQuery) {
          return true;
        }

        return searchable.includes(includeQuery);
      });
    },
    latestDebugEntry: (state) => state.debugLogEntries.at(-1) ?? null,
    isDebugLogAtLimit(): boolean {
      return this.debugLogEntries.length >= this.normalizedDebugLogLimit;
    },
  },
  actions: {
    setSelectedControllable(controllableId: string) {
      this.selectedControllableId = controllableId;
    },
    setLastSelection(selection: WorldSceneSelection | null) {
      this.lastSelection = selection;
    },
    closeAllPopups() {
      this.isManagerPopupOpen = false;
      this.isChatPopupOpen = false;
      this.isActivityHistoryOpen = false;
      this.isDebugLogOpen = false;
    },
    setDebugLogOpen(value: boolean) {
      this.isDebugLogOpen = value;
      persistBoolean(DEBUG_LOG_OPEN_STORAGE_KEY, value);
    },
    setDebugLogIngame(value: boolean) {
      this.isDebugLogIngame = value;
      persistBoolean(DEBUG_LOG_INGAME_STORAGE_KEY, value);
    },
    setDebugLogLimit(value: number) {
      this.debugLogLimit = Number.isFinite(value) ? Math.max(1, Math.floor(value)) : 1;
      if (this.debugLogEntries.length > this.debugLogLimit) {
        this.debugLogEntries = this.debugLogEntries.slice(0, this.debugLogLimit);
      }
    },
    recordDebugMessage(direction: GatewayMessageDirection, message: ClientMessage | ServerMessage) {
      if (this.debugLogEntries.length >= this.normalizedDebugLogLimit) {
        return;
      }

      this.debugLogEntries = [
        ...this.debugLogEntries,
        {
          id: `debug-${Date.now()}-${Math.random().toString(16).slice(2)}`,
          direction,
          messageType: message.type,
          payload: formatDebugPayload(message),
          createdAt: Date.now(),
        },
      ];
    },
    clearDebugLog() {
      this.debugLogEntries = [];
    },
    restorePreferences() {
      this.isDebugLogOpen = readBoolean(DEBUG_LOG_OPEN_STORAGE_KEY);
      this.isDebugLogIngame = readBoolean(DEBUG_LOG_INGAME_STORAGE_KEY);
    },
    persistPreferences() {
      persistBoolean(DEBUG_LOG_OPEN_STORAGE_KEY, this.isDebugLogOpen);
      persistBoolean(DEBUG_LOG_INGAME_STORAGE_KEY, this.isDebugLogIngame);
    },
  },
});

function readBoolean(key: string) {
  try {
    return globalThis.localStorage?.getItem(key) === 'true';
  } catch {
    return false;
  }
}

function persistBoolean(key: string, value: boolean) {
  try {
    globalThis.localStorage?.setItem(key, value ? 'true' : 'false');
  } catch {
    // Ignore browser storage failures.
  }
}