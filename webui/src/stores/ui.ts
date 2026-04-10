import { defineStore } from 'pinia';
import type { WorldSceneSelection } from '../renderer/WorldScene';
import type { ClientMessage, GatewayMessageDirection, ServerMessage } from '../types/generated';
import type { ClearTacticalTargetCommandMessage, SetTacticalModeCommandMessage, SetTacticalTargetCommandMessage, UpgradeSubsystemCommandMessage } from '../transport/commands';
import type { DebugLogEntry, ScannerMode, TacticalMode } from '../types/client';
import { formatDebugPayload } from '../lib/formatting';

const DEBUG_LOG_OPEN_STORAGE_KEY = 'flattiverse.debugLog.open';
const DEBUG_LOG_INGAME_STORAGE_KEY = 'flattiverse.debugLog.ingame';
const DEBUG_LOG_SETTINGS_STORAGE_KEY = 'flattiverse.debugLog.settings';
const CUSTOM_SHIP_COLORS_STORAGE_KEY = 'flattiverse.customShipColors';
const TRACKED_UNIT_COLOR_PALETTE = [
  '#6ef2ff',
  '#ff8a5b',
  '#ffe66d',
  '#83ffb3',
  '#ff78c5',
  '#9fa8ff',
  '#f9a8ff',
  '#7fffd4',
];

type StoredDebugLogSettings = {
  limit?: number;
  search?: string;
  exclude?: string;
  captureSearch?: string;
  showClient?: boolean;
  showServer?: boolean;
};

type DebugGatewayMessage = ClientMessage | ServerMessage | SetTacticalModeCommandMessage | SetTacticalTargetCommandMessage | ClearTacticalTargetCommandMessage | UpgradeSubsystemCommandMessage;

export const useUiStore = defineStore('ui', {
  state: () => {
    const storedSettings = readStoredDebugLogSettings();
    const storedCustomShipColors = readStoredCustomShipColors();

    return ({
    selectedControllableId: '',
    navigationThrustPercentage: 1,
    viewportJumpTargetId: '',
    focusSelectionRequestToken: 0,
    isFocusSelectionActive: false,

    scannerMode: 'off' as ScannerMode,
    scannerWidth: 90,
    scannerLength: 200,
    tacticalMode: 'off' as TacticalMode,
    tacticalTargetsByControllableId: {} as Record<string, string>,
    lastSelection: null as WorldSceneSelection | null,
    visibleUnitIds: [] as string[],
    trackedUnitColors: {} as Record<string, string>,
    customShipColors: storedCustomShipColors,
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
    requestViewportJump(unitId: string) {
      this.viewportJumpTargetId = unitId;
    },
    clearViewportJump() {
      this.viewportJumpTargetId = '';
    },
    requestToggleFocusSelection() {
      this.focusSelectionRequestToken += 1;
    },
    setFocusSelectionActive(value: boolean) {
      this.isFocusSelectionActive = value;
    },
    setNavigationThrustPercentage(value: number) {
      if (!Number.isFinite(value)) {
        this.navigationThrustPercentage = 0;
        return;
      }

      this.navigationThrustPercentage = Math.min(Math.max(value, 0), 1);
    },
    setScannerMode(mode: ScannerMode) {
      this.scannerMode = mode;
    },
    setScannerWidth(value: number) {
      if (!Number.isFinite(value)) {
        this.scannerWidth = 90;
        return;
      }

      this.scannerWidth = Math.max(1, Math.floor(value));
    },
    setScannerLength(value: number) {
      if (!Number.isFinite(value)) {
        this.scannerLength = 200;
        return;
      }

      this.scannerLength = Math.max(1, Math.floor(value));
    },
    setTacticalMode(mode: TacticalMode) {
      this.tacticalMode = mode;
    },
    setTacticalTarget(controllableId: string, targetId: string) {
      const normalizedControllableId = controllableId.trim();
      const normalizedTargetId = targetId.trim();
      if (!normalizedControllableId || !normalizedTargetId) {
        return;
      }

      this.tacticalTargetsByControllableId = {
        ...this.tacticalTargetsByControllableId,
        [normalizedControllableId]: normalizedTargetId,
      };
    },
    clearTacticalTarget(controllableId: string) {
      const normalizedControllableId = controllableId.trim();
      if (!normalizedControllableId || !this.tacticalTargetsByControllableId[normalizedControllableId]) {
        return;
      }

      const nextTacticalTargets = { ...this.tacticalTargetsByControllableId };
      delete nextTacticalTargets[normalizedControllableId];
      this.tacticalTargetsByControllableId = nextTacticalTargets;
    },
    clearTacticalTargets() {
      this.tacticalTargetsByControllableId = {};
    },
    setLastSelection(selection: WorldSceneSelection | null) {
      this.lastSelection = selection;
    },
    setVisibleUnitIds(unitIds: string[]) {
      this.visibleUnitIds = unitIds;
    },
    toggleTrackedUnit(unitId: string) {
      if (!unitId) {
        return;
      }

      if (this.trackedUnitColors[unitId]) {
        const nextTrackedUnitColors = { ...this.trackedUnitColors };
        delete nextTrackedUnitColors[unitId];
        this.trackedUnitColors = nextTrackedUnitColors;
        return;
      }

      this.trackedUnitColors = {
        ...this.trackedUnitColors,
        [unitId]: pickTrackedUnitColor(Object.values(this.trackedUnitColors)),
      };
    },
    setCustomShipColor(controllableId: string, color: string) {
      const normalizedControllableId = controllableId.trim();
      const normalizedColor = normalizeHexColor(color);
      if (!normalizedControllableId || !normalizedColor) {
        return;
      }

      this.customShipColors = {
        ...this.customShipColors,
        [normalizedControllableId]: normalizedColor,
      };
      persistCustomShipColors(this.customShipColors);
    },
    clearCustomShipColor(controllableId: string) {
      const normalizedControllableId = controllableId.trim();
      if (!normalizedControllableId || !this.customShipColors[normalizedControllableId]) {
        return;
      }

      const nextCustomShipColors = { ...this.customShipColors };
      delete nextCustomShipColors[normalizedControllableId];
      this.customShipColors = nextCustomShipColors;
      persistCustomShipColors(this.customShipColors);
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
    recordDebugMessage(direction: GatewayMessageDirection, message: DebugGatewayMessage) {
      const payload = formatDebugPayload(message);
      const captureQuery = this.debugLogCaptureSearch.trim().toLowerCase();
      if (captureQuery && !matchesDebugLogFilters(this, direction, message.type, payload, captureQuery)) {
        return;
      }

      if (this.debugLogEntries.length >= this.normalizedDebugLogLimit) {
        return;
      }

      this.debugLogEntries.unshift({
        id: `debug-${Date.now()}-${Math.random().toString(16).slice(2)}`,
        direction,
        messageType: message.type,
        payload,
        createdAt: Date.now(),
      });
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

function readStoredCustomShipColors() {
  try {
    const storedValue = globalThis.localStorage?.getItem(CUSTOM_SHIP_COLORS_STORAGE_KEY);
    if (!storedValue) {
      return {} as Record<string, string>;
    }

    const parsed = JSON.parse(storedValue);
    if (!parsed || typeof parsed !== 'object') {
      return {} as Record<string, string>;
    }

    return Object.fromEntries(
      Object.entries(parsed as Record<string, unknown>)
        .map(([controllableId, color]) => [controllableId, normalizeHexColor(typeof color === 'string' ? color : '')])
        .filter((entry): entry is [string, string] => typeof entry[1] === 'string'),
    );
  } catch {
    return {} as Record<string, string>;
  }
}

function persistCustomShipColors(customShipColors: Record<string, string>) {
  try {
    globalThis.localStorage?.setItem(CUSTOM_SHIP_COLORS_STORAGE_KEY, JSON.stringify(customShipColors));
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

function pickTrackedUnitColor(usedColors: string[]) {
  const availableColors = TRACKED_UNIT_COLOR_PALETTE.filter((color) => !usedColors.includes(color));
  if (availableColors.length > 0) {
    return availableColors[Math.floor(Math.random() * availableColors.length)] ?? TRACKED_UNIT_COLOR_PALETTE[0];
  }

  return TRACKED_UNIT_COLOR_PALETTE[Math.floor(Math.random() * TRACKED_UNIT_COLOR_PALETTE.length)] ?? '#6ef2ff';
}

function normalizeHexColor(value: string) {
  const normalized = value.trim();
  return /^#[0-9a-fA-F]{6}$/.test(normalized) ? normalized.toLowerCase() : undefined;
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
