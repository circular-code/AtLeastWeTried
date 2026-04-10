<script setup lang="ts">
import { computed, watch } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';
import type { ScannerMode, TacticalMode } from '../../types/client';
import { readNavigationTarget } from '../../renderer/WorldScene';

const gateway = useGateway();
const gameStore = useGameStore();
const uiStore = useUiStore();
const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));

const thrust = computed({
  get: () => uiStore.navigationThrustPercentage,
  set: (value: number) => {
    uiStore.setNavigationThrustPercentage(value);
    const controllableId = activeControllableId.value;
    const navigationTarget = readNavigationTarget(gameStore.ownerOverlay, controllableId);
    if (navigationTarget) {
      gateway.setNavigationTarget(controllableId, navigationTarget.x, -navigationTarget.y, value);
    } else {
      gateway.setEngine(controllableId, value);
    }
  },
});
const tacticalMode = computed(() => uiStore.tacticalMode);
const scannerMode = computed<ScannerMode>(() => uiStore.scannerMode);
const scannerWidthMin = computed(() => gameStore.scannerWidthMinimumFor(activeControllableId.value));
const scannerWidthMax = computed(() => gameStore.scannerWidthMaximumFor(activeControllableId.value));
const scannerLengthMin = computed(() => gameStore.scannerLengthMinimumFor(activeControllableId.value));
const scannerLengthMax = computed(() => gameStore.scannerLengthMaximumFor(activeControllableId.value));
const activeOverlayState = computed<Record<string, unknown>>(() => {
  const overlay = gameStore.ownerOverlay[activeControllableId.value];
  return overlay && typeof overlay === 'object'
    ? overlay as Record<string, unknown>
    : {};
});
const activeSubsystems = computed(() => readSubsystemEntries(activeOverlayState.value.subsystems ?? activeOverlayState.value.modules));
const shotFabricator = computed(() => activeSubsystems.value.find((entry) => entry.name === 'Shot Fabricator' && entry.exists) ?? null);
const shotFabricatorRunning = computed(() => shotFabricator.value ? isShotFabricatorRunning(shotFabricator.value) : false);
const shotFabricatorMaximumRate = computed(() => shotFabricator.value ? readSubsystemStatMaximumMetric(shotFabricator.value, 'Rate') : null);
const isFocusSelectionActive = computed(() => ('isFocusSelectionActive' in uiStore ? !!uiStore.isFocusSelectionActive : false));
const scannerWidth = computed({
  get: () => uiStore.scannerWidth,
  set: (value: number) => uiStore.setScannerWidth(value),
});
const scannerLength = computed({
  get: () => uiStore.scannerLength,
  set: (value: number) => uiStore.setScannerLength(value),
});

watch(
  activeControllableId,
  (newId) => {
    if (!newId) {
      return;
    }

    const scannerModeFromOverlay = gameStore.scannerRequestedModeFor(newId);
    uiStore.setScannerMode(scannerModeFromOverlay);

    const scannerWidthFromOverlay = gameStore.scannerWidthFor(newId);
    uiStore.setScannerWidth(scannerWidthFromOverlay);

    const scannerLengthFromOverlay = gameStore.scannerLengthFor(newId);
    uiStore.setScannerLength(scannerLengthFromOverlay);

    const tacticalModeFromOverlay = gameStore.tacticalModeFor(newId);
    uiStore.setTacticalMode(tacticalModeFromOverlay);

    const thrustFromOverlay = gameStore.thrustPercentageFor(newId);
    uiStore.setNavigationThrustPercentage(thrustFromOverlay);
  },
);

watch(
  () => ({
    min: scannerWidthMin.value,
    max: scannerWidthMax.value,
    width: scannerWidth.value,
  }),
  ({ width, min, max }) => {
    const clampedWidth = Math.min(Math.max(width, min), max);
    if (clampedWidth !== width) {
      uiStore.setScannerWidth(clampedWidth);
    }
  },
  { immediate: true },
);

watch(
  () => ({
    min: scannerLengthMin.value,
    max: scannerLengthMax.value,
    length: scannerLength.value,
  }),
  ({ length, min, max }) => {
    const clampedLength = Math.min(Math.max(length, min), max);
    if (clampedLength !== length) {
      uiStore.setScannerLength(clampedLength);
    }
  },
  { immediate: true },
);

function setScanner(mode: ScannerMode) {
  uiStore.setScannerMode(mode);
  gateway.setScannerMode(activeControllableId.value, mode);
}

function setScannerWidth(value: number) {
  const controllableId = activeControllableId.value;
  if (!controllableId) {
    return;
  }

  const clampedWidth = Math.min(Math.max(value, scannerWidthMin.value), scannerWidthMax.value);
  uiStore.setScannerWidth(clampedWidth);
  gateway.setScannerWidth(controllableId, clampedWidth);
}

function setScannerLength(value: number) {
  const controllableId = activeControllableId.value;
  if (!controllableId) {
    return;
  }

  const clampedLength = Math.min(Math.max(value, scannerLengthMin.value), scannerLengthMax.value);
  uiStore.setScannerLength(clampedLength);
  gateway.setScannerLength(controllableId, clampedLength);
}

function setTactical(mode: TacticalMode) {
  uiStore.setTacticalMode(mode);
  gateway.setTacticalMode(activeControllableId.value, mode);
}

function toggleShotRegeneration() {
  const controllableId = activeControllableId.value;
  const subsystem = shotFabricator.value;
  if (!controllableId || !subsystem) {
    return;
  }

  if (shotFabricatorRunning.value) {
    gateway.setSubsystemMode(controllableId, subsystem.id, 'off');
    return;
  }

  const maximumRate = shotFabricatorMaximumRate.value;
  if (maximumRate !== null && maximumRate > 0) {
    gateway.setSubsystemMode(controllableId, subsystem.id, 'set', maximumRate);
  }

  gateway.setSubsystemMode(controllableId, subsystem.id, 'on');
}

function requestFocusSelectionToggle() {
  if (typeof uiStore.requestToggleFocusSelection === 'function') {
    uiStore.requestToggleFocusSelection();
    return;
  }

  const currentToken = 'focusSelectionRequestToken' in uiStore && typeof uiStore.focusSelectionRequestToken === 'number'
    ? uiStore.focusSelectionRequestToken
    : 0;
  uiStore.focusSelectionRequestToken = currentToken + 1;
}

type ShipSubsystemStat = {
  label: string;
  value: string;
};

type ShipSubsystemEntry = {
  id: string;
  name: string;
  exists: boolean;
  stats: ShipSubsystemStat[];
};

function readSubsystemEntries(value: unknown): ShipSubsystemEntry[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => readRecord(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => ({
      id: readText(entry.id, readText(entry.slot, readText(entry.name, 'subsystem'))),
      name: humanizeSubsystemName(readText(entry.name, readText(entry.slot, 'Subsystem'))),
      exists: readBoolean(entry.exists, (readNumeric(entry.tier) ?? 0) > 0),
      stats: readSubsystemStats(entry.stats),
    }));
}

function readSubsystemStats(value: unknown): ShipSubsystemStat[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => readRecord(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => ({
      label: readText(entry.label, 'Stat'),
      value: readText(entry.value),
    }))
    .filter((entry) => entry.value.trim().length > 0);
}

function isShotFabricatorRunning(subsystem: ShipSubsystemEntry) {
  const mode = subsystem.stats.find((entry) => entry.label === 'Mode')?.value.trim().toLowerCase();
  if (mode) {
    return mode === 'active' || mode === 'on';
  }

  return false;
}

function readSubsystemStatMaximumMetric(subsystem: ShipSubsystemEntry, statLabel: string) {
  const stat = subsystem.stats.find((entry) => entry.label === statLabel);
  if (!stat) {
    return null;
  }

  const metrics = readMetrics(stat.value);
  if (metrics.length >= 2) {
    return metrics[1] ?? null;
  }

  return metrics[0] ?? null;
}

function readMetrics(value: string) {
  return Array.from(value.matchAll(/-?\d+(?:[.,]\d+)?/g))
    .map((match) => Number(match[0].replace(',', '.')))
    .filter((metric) => Number.isFinite(metric));
}

function readNumeric(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string' && value.trim() !== '') {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  return null;
}

function readText(value: unknown, fallback = '') {
  return typeof value === 'string' ? value : fallback;
}

function readBoolean(value: unknown, fallback = false) {
  return typeof value === 'boolean' ? value : fallback;
}

function readRecord(value: unknown) {
  return typeof value === 'object' && value !== null
    ? value as Record<string, unknown>
    : undefined;
}

function humanizeSubsystemName(value: string) {
  const normalized = value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim();

  if (!normalized) {
    return 'Unknown subsystem';
  }

  return normalized.charAt(0).toUpperCase() + normalized.slice(1);
}
</script>

<template>
  <section v-if="activeControllableId" class="command-dock panel-glass">
    <div class="dock-group">
      <span class="dock-label">Thrust</span>
      <input v-model.number="thrust" class="dock-slider" type="range" min="0" max="1" step="0.05" />
      <span class="dock-value">{{ thrust.toFixed(2) }}</span>
      <button class="dock-btn" type="button" @click="gateway.fireWeapon(activeControllableId)">Fire</button>
    </div>

    <span class="dock-sep" aria-hidden="true"></span>

    <div class="dock-group-stack dock-group-stack--scanner">
      <div class="dock-group">
        <span class="dock-label">Scanner</span>
        <button class="dock-btn" :class="{ active: scannerMode === '360' }" type="button" @click="setScanner('360')">360°</button>
        <button class="dock-btn" :class="{ active: scannerMode === 'forward' }" type="button" @click="setScanner('forward')">Fwd</button>
        <button class="dock-btn" :class="{ active: scannerMode === 'sweep' }" type="button" @click="setScanner('sweep')">Sweep</button>
        <button class="dock-btn" :class="{ active: scannerMode === 'off' }" type="button" @click="setScanner('off')">Off</button>
      </div>
      <div class="dock-group dock-group--scanner-metrics">
        <div class="dock-group dock-group--scanner-metric">
        <span class="dock-label">W</span>
        <input
          :value="scannerWidth"
          class="dock-slider"
          type="range"
          :min="scannerWidthMin"
          :max="scannerWidthMax"
          step="1"
          @input="setScannerWidth(Number(($event.target as HTMLInputElement).value))"
        />
        <span class="dock-value">{{ scannerWidth.toFixed(0) }}&deg;</span>
        </div>
        <div class="dock-group dock-group--scanner-metric">
        <span class="dock-label">L</span>
        <input
          :value="scannerLength"
          class="dock-slider"
          type="range"
          :min="scannerLengthMin"
          :max="scannerLengthMax"
          step="1"
          @input="setScannerLength(Number(($event.target as HTMLInputElement).value))"
        />
        <span class="dock-value">{{ scannerLength.toFixed(0) }}</span>
        </div>
      </div>
    </div>

    <span class="dock-sep" aria-hidden="true"></span>

    <div class="dock-group">
      <span class="dock-label">Tactical</span>
      <button class="dock-btn" :class="{ active: tacticalMode === 'enemy' }" type="button" @click="setTactical('enemy')">Auto</button>
      <button class="dock-btn" :class="{ active: tacticalMode === 'target' }" type="button" @click="setTactical('target')">Target</button>
      <button class="dock-btn" :class="{ active: tacticalMode === 'off' }" type="button" @click="setTactical('off')">Off</button>
      <button class="dock-btn" type="button" @click="gateway.clearNavigationTarget(activeControllableId)">Clear Nav</button>
      <button v-if="shotFabricator" class="dock-btn" :class="{ active: shotFabricatorRunning }" type="button" @click="toggleShotRegeneration">
        {{ shotFabricatorRunning ? 'Stop Regen' : 'Regen Shots' }}
      </button>
    </div>

    <span class="dock-sep" aria-hidden="true"></span>

    <div class="dock-group dock-group--lock">
      <button
        class="dock-btn"
        :class="{ active: isFocusSelectionActive }"
        :aria-pressed="isFocusSelectionActive"
        type="button"
        @click="requestFocusSelectionToggle()"
      >
        Lock onto ship
      </button>
    </div>
  </section>
</template>
