import { defineStore } from 'pinia';
import type { WorldSceneSelection } from '../renderer/WorldScene';
import type { ClientMessage, GatewayMessageDirection, ServerMessage } from '../types/generated';
import type { DebugLogEntry, ScannerMode, TacticalMode } from '../types/client';
import { formatDebugPayload } from '../lib/formatting';

const DEBUG_LOG_OPEN_STORAGE_KEY = 'flattiverse.debugLog.open';
const DEBUG_LOG_INGAME_STORAGE_KEY = 'flattiverse.debugLog.ingame';
const DEBUG_LOG_SETTINGS_STORAGE_KEY = 'flattiverse.debugLog.settings';
const GAMEPLAY_DOCK_SETTINGS_STORAGE_KEY = 'flattiverse.gameplayDock.settings';

type StoredDebugLogSettings = {
  limit?: number;
  search?: string;
  exclude?: string;
  captureSearch?: string;
  showClient?: boolean;
  showServer?: boolean;
};

type StoredGameplayDockSettings = {
  navigationThrustPercentage?: number;
  scannerMode?: ScannerMode;
  scannerWidth?: number;
  tacticalMode?: TacticalMode;
};

export const useUiStore = defineStore('ui', {
  state: () => {
    const storedSettings = readStoredDebugLogSettings();
    const storedDockSettings = readStoredGameplayDockSettings();

    return ({
    selectedControllableId: '',
    navigationThrustPercentage: readStoredNavigationThrustPercentage(storedDockSettings),
    scannerMode: readStoredScannerMode(storedDockSettings),
    scannerWidth: readStoredScannerWidth(storedDockSettings),
    tacticalMode: readStoredTacticalMode(storedDockSettings),
    lastSelection: null as WorldSceneSelection | null,
    visibleUnitIds: [] as string[],
    isManagerPopupOpen: false,
    isChatPopupOpen: false,
    isActivityHistoryOpen: false,
    isDebugLogOpen: false,
    isDebugLogIngame: false,
    debugLogEntries: [] as DebugLogEntry[],
    debugLogLimit: readStoredDebugLogLimit(storedSettings),
    debugLogSearch: storedSettings.search ?? '',
    debugLogExclude: storedSettings.exclude ?? '',
    debugLogCaptureSearch: storedSettings.captureSearch ?? '',
    showClientDebugMessages: storedSettings.showClient ?? true,
    showServerDebugMessages: storedSettings.showServer ?? true,
  });
  },
  getters: {
    normalizedDebugLogLimit: (state) => {
      if (!Number.isFinite(state.debugLogLimit)) {
        return 1;
      }

      return Math.max(1, Math.floor(state.debugLogLimit));
    },
    activeDebugCaptureSearch: (state) => state.debugLogCaptureSearch.trim(),
    filteredDebugLogEntries(state): DebugLogEntry[] {
      return state.debugLogEntries.filter((entry) => matchesDebugLogFilters(
        state,
        entry.direction,
        entry.messageType,
        entry.payload,
      ));
    },
    latestDebugEntry: (state) => state.debugLogEntries[0] ?? null,
    isDebugLogAtLimit(): boolean {
      if (this.activeDebugCaptureSearch && this.filteredDebugLogEntries.length === 0) {
        return false;
      }

      return this.debugLogEntries.length >= this.normalizedDebugLogLimit;
    },
  },
  actions: {
    setSelectedControllable(controllableId: string) {
      this.selectedControllableId = controllableId;
    },
    setNavigationThrustPercentage(value: number) {
      if (!Number.isFinite(value)) {
        this.navigationThrustPercentage = 0;
        this.persistGameplayDockPreferences();
        return;
      }

      this.navigationThrustPercentage = Math.min(Math.max(value, 0), 1);
      this.persistGameplayDockPreferences();
    },
    setScannerMode(mode: ScannerMode) {
      this.scannerMode = mode;
      this.persistGameplayDockPreferences();
    },
    setScannerWidth(value: number) {
      if (!Number.isFinite(value)) {
        this.scannerWidth = 90;
        this.persistGameplayDockPreferences();
        return;
      }

      this.scannerWidth = Math.max(1, Math.floor(value));
      this.persistGameplayDockPreferences();
    },
    setTacticalMode(mode: TacticalMode) {
      this.tacticalMode = mode;
      this.persistGameplayDockPreferences();
    },
    setLastSelection(selection: WorldSceneSelection | null) {
      this.lastSelection = selection;
    },
    setVisibleUnitIds(unitIds: string[]) {
      this.visibleUnitIds = unitIds;
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
      this.persistPreferences();
    },
    setDebugLogSearch(value: string) {
      this.debugLogSearch = value;
      this.refreshDebugCaptureBuffer();
      this.persistPreferences();
    },
    setDebugLogExclude(value: string) {
      this.debugLogExclude = value;
      this.refreshDebugCaptureBuffer();
      this.persistPreferences();
    },
    setShowClientDebugMessages(value: boolean) {
      this.showClientDebugMessages = value;
      this.refreshDebugCaptureBuffer();
      this.persistPreferences();
    },
    setShowServerDebugMessages(value: boolean) {
      this.showServerDebugMessages = value;
      this.refreshDebugCaptureBuffer();
      this.persistPreferences();
    },
    recordDebugMessage(direction: GatewayMessageDirection, message: ClientMessage | ServerMessage) {
      const payload = formatDebugPayload(message);
      const captureQuery = this.debugLogCaptureSearch.trim().toLowerCase();
      if (captureQuery && !matchesDebugLogFilters(this, direction, message.type, payload, captureQuery)) {
        return;
      }

      if (this.debugLogEntries.length >= this.normalizedDebugLogLimit) {
        return;
      }

      this.debugLogEntries = [{
        id: `debug-${Date.now()}-${Math.random().toString(16).slice(2)}`,
        direction,
        messageType: message.type,
        payload,
        createdAt: Date.now(),
      }, ...this.debugLogEntries];
    },
    clearDebugLog() {
      this.debugLogEntries = [];
      this.debugLogCaptureSearch = this.debugLogSearch.trim();
      this.persistPreferences();
    },
    refreshDebugCaptureBuffer() {
      if (!this.activeDebugCaptureSearch) {
        return;
      }

      this.debugLogEntries = [];
      this.debugLogCaptureSearch = this.debugLogSearch.trim();
    },
    restorePreferences() {
      this.isDebugLogOpen = readBoolean(DEBUG_LOG_OPEN_STORAGE_KEY);
      this.isDebugLogIngame = readBoolean(DEBUG_LOG_INGAME_STORAGE_KEY);
    },
    persistPreferences() {
      persistBoolean(DEBUG_LOG_OPEN_STORAGE_KEY, this.isDebugLogOpen);
      persistBoolean(DEBUG_LOG_INGAME_STORAGE_KEY, this.isDebugLogIngame);
      persistDebugLogSettings({
        limit: this.debugLogLimit,
        search: this.debugLogSearch,
        exclude: this.debugLogExclude,
        captureSearch: this.debugLogCaptureSearch,
        showClient: this.showClientDebugMessages,
        showServer: this.showServerDebugMessages,
      });
    },
    persistGameplayDockPreferences() {
      persistGameplayDockSettings({
        navigationThrustPercentage: this.navigationThrustPercentage,
        scannerMode: this.scannerMode,
        scannerWidth: this.scannerWidth,
        tacticalMode: this.tacticalMode,
      });
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

function readStoredDebugLogSettings(): StoredDebugLogSettings {
  try {
    const storedValue = globalThis.localStorage?.getItem(DEBUG_LOG_SETTINGS_STORAGE_KEY);
    if (!storedValue) {
      return {};
    }

    const parsed = JSON.parse(storedValue);
    if (!parsed || typeof parsed !== 'object') {
      return {};
    }

    return parsed as StoredDebugLogSettings;
  } catch {
    return {};
  }
}

function persistDebugLogSettings(settings: StoredDebugLogSettings) {
  try {
    globalThis.localStorage?.setItem(DEBUG_LOG_SETTINGS_STORAGE_KEY, JSON.stringify(settings));
  } catch {
    // Ignore browser storage failures.
  }
}

function readStoredDebugLogLimit(settings: StoredDebugLogSettings) {
  if (!Number.isFinite(settings.limit)) {
    return 200;
  }

  return Math.max(1, Math.floor(settings.limit ?? 200));
}

function readStoredGameplayDockSettings(): StoredGameplayDockSettings {
  try {
    const storedValue = globalThis.localStorage?.getItem(GAMEPLAY_DOCK_SETTINGS_STORAGE_KEY);
    if (!storedValue) {
      return {};
    }

    const parsed = JSON.parse(storedValue);
    if (!parsed || typeof parsed !== 'object') {
      return {};
    }

    return parsed as StoredGameplayDockSettings;
  } catch {
    return {};
  }
}

function persistGameplayDockSettings(settings: StoredGameplayDockSettings) {
  try {
    globalThis.localStorage?.setItem(GAMEPLAY_DOCK_SETTINGS_STORAGE_KEY, JSON.stringify(settings));
  } catch {
    // Ignore browser storage failures.
  }
}

function readStoredNavigationThrustPercentage(settings: StoredGameplayDockSettings) {
  if (!Number.isFinite(settings.navigationThrustPercentage)) {
    return 0.25;
  }

  return Math.min(Math.max(settings.navigationThrustPercentage ?? 0.25, 0), 1);
}

function readStoredScannerWidth(settings: StoredGameplayDockSettings) {
  if (!Number.isFinite(settings.scannerWidth)) {
    return 90;
  }

  return Math.max(1, Math.floor(settings.scannerWidth ?? 90));
}

function readStoredScannerMode(settings: StoredGameplayDockSettings): ScannerMode {
  switch (settings.scannerMode) {
    case '360':
    case 'forward':
    case 'sweep':
    case 'off':
      return settings.scannerMode;
    default:
      return 'off';
  }
}

function readStoredTacticalMode(settings: StoredGameplayDockSettings): TacticalMode {
  switch (settings.tacticalMode) {
    case 'enemy':
    case 'target':
    case 'off':
      return settings.tacticalMode;
    default:
      return 'off';
  }
}

function buildDebugSearchableText(messageType: string, payload: string) {
  return `${messageType}\n${payload}`.toLowerCase();
}

function matchesDebugLogFilters(
  state: {
    debugLogSearch: string;
    debugLogExclude: string;
    showClientDebugMessages: boolean;
    showServerDebugMessages: boolean;
  },
  direction: GatewayMessageDirection,
  messageType: string,
  payload: string,
  includeQuery = state.debugLogSearch.trim().toLowerCase(),
) {
  if (direction === 'client' && !state.showClientDebugMessages) {
    return false;
  }

  if (direction === 'server' && !state.showServerDebugMessages) {
    return false;
  }

  const searchable = buildDebugSearchableText(messageType, payload);
  const excludeQuery = state.debugLogExclude.trim().toLowerCase();

  if (excludeQuery && searchable.includes(excludeQuery)) {
    return false;
  }

  if (!includeQuery) {
    return true;
  }

  return searchable.includes(includeQuery);
}
