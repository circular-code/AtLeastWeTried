<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { readCargoResourceSnapshot, readSubsystemEntries, readSubsystemMaximumMetric, readSubsystemMetric } from '../../lib/harvesting';
import { formatMetric } from '../../lib/formatting';
import type { ShipSubsystemEntry } from '../../lib/harvesting';
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
const shotFabricatorMaximumRate = computed(() => shotFabricator.value ? readSubsystemMaximumMetric(shotFabricator.value, 'Rate') : null);
const resourceMiner = computed(() => activeSubsystems.value.find((entry) => entry.name === 'Resource Miner' && entry.exists) ?? null);
const resourceMinerRate = computed(() => readSubsystemMetric(resourceMiner.value, 'Rate') ?? 0);
const resourceMinerMaximumRate = computed(() => readSubsystemMaximumMetric(resourceMiner.value, 'Rate') ?? 0);
const resourceMinerRunning = computed(() => resourceMinerRate.value > 0.0001);
const pendingMiningRateRequest = ref<{ desiredRate: number; createdAt: number; warningShown: boolean } | null>(null);
const activeMinedResources = computed(() => {
  const subsystems = activeSubsystems.value;
  const entries = [
    { key: 'metal' as const, label: 'Metal', yieldPerTick: readSubsystemMetric(resourceMiner.value, 'Metal') ?? 0 },
    { key: 'carbon' as const, label: 'Carbon', yieldPerTick: readSubsystemMetric(resourceMiner.value, 'Carbon') ?? 0 },
    { key: 'hydrogen' as const, label: 'Hydrogen', yieldPerTick: readSubsystemMetric(resourceMiner.value, 'Hydrogen') ?? 0 },
    { key: 'silicon' as const, label: 'Silicon', yieldPerTick: readSubsystemMetric(resourceMiner.value, 'Silicon') ?? 0 },
  ]
    .filter((entry) => entry.yieldPerTick > 0)
    .map((entry) => ({
      ...entry,
      cargo: readCargoResourceSnapshot(subsystems, entry.key),
    }));

  return entries;
});
const nebulaCollector = computed(() => activeSubsystems.value.find((entry) => entry.name === 'Nebula Collector' && entry.exists) ?? null);
const nebulaCollectorRate = computed(() => readSubsystemMetric(nebulaCollector.value, 'Rate') ?? 0);
const nebulaCollectorMaximumRate = computed(() => readSubsystemMaximumMetric(nebulaCollector.value, 'Rate') ?? 0);
const nebulaCollectorRunning = computed(() => nebulaCollectorRate.value > 0.0001);
const isFocusSelectionActive = computed(() => ('isFocusSelectionActive' in uiStore ? !!uiStore.isFocusSelectionActive : false));
const currentSpeed = computed(() => {
  const movement = activeOverlayState.value.movement;
  const record = movement && typeof movement === 'object'
    ? movement as Record<string, unknown>
    : undefined;
  const x = typeof record?.x === 'number' ? record.x : 0;
  const y = typeof record?.y === 'number' ? record.y : 0;
  return Math.hypot(x, y);
});
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

watch(
  () => ({
    actualRate: resourceMinerRate.value,
    speed: currentSpeed.value,
    controllableId: activeControllableId.value,
  }),
  ({ actualRate, speed, controllableId }) => {
    const pending = pendingMiningRateRequest.value;
    if (!pending) {
      return;
    }

    if (!controllableId || Date.now() - pending.createdAt > 4000) {
      pendingMiningRateRequest.value = null;
      return;
    }

    if (actualRate >= pending.desiredRate - 0.00005) {
      pendingMiningRateRequest.value = null;
      return;
    }

    if (!pending.warningShown && speed > 0.02 && pending.desiredRate > actualRate + 0.00005) {
      gameStore.recordActivity({
        tone: 'warn',
        summary: 'Mining rate reduced',
        detail: 'The backend cut the mining rate back because the ship is still moving too much. Slow down or hold position, then try again.',
        meta: `${gameStore.getControllableLabel(controllableId)} · speed ${speed.toFixed(2)}`,
      });
      pendingMiningRateRequest.value = {
        ...pending,
        warningShown: true,
      };
      return;
    }

    if (pending.warningShown && speed <= 0.02) {
      pendingMiningRateRequest.value = null;
    }
  },
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

function toggleResourceMiner() {
  const controllableId = activeControllableId.value;
  if (!controllableId || !resourceMiner.value) {
    return;
  }

  gateway.setSubsystemMode(controllableId, resourceMiner.value.id, resourceMinerRunning.value ? 'off' : 'on');
}

function setResourceMinerRate(value: number) {
  const controllableId = activeControllableId.value;
  const subsystem = resourceMiner.value;
  if (!controllableId || !subsystem) {
    return;
  }

  const clampedRate = Math.min(Math.max(value, 0), resourceMinerMaximumRate.value);
  pendingMiningRateRequest.value = {
    desiredRate: clampedRate,
    createdAt: Date.now(),
    warningShown: false,
  };
  gateway.setSubsystemMode(controllableId, subsystem.id, 'set', clampedRate);
}

function toggleNebulaCollector() {
  const controllableId = activeControllableId.value;
  if (!controllableId || !nebulaCollector.value) {
    return;
  }

  gateway.setSubsystemMode(controllableId, nebulaCollector.value.id, nebulaCollectorRunning.value ? 'off' : 'on');
}

function setNebulaCollectorRate(value: number) {
  const controllableId = activeControllableId.value;
  const subsystem = nebulaCollector.value;
  if (!controllableId || !subsystem) {
    return;
  }

  const clampedRate = Math.min(Math.max(value, 0), nebulaCollectorMaximumRate.value);
  gateway.setSubsystemMode(controllableId, subsystem.id, 'set', clampedRate);
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

function isShotFabricatorRunning(subsystem: ShipSubsystemEntry) {
  const mode = subsystem.stats.find((entry) => entry.label === 'Mode')?.value.trim().toLowerCase();
  if (mode) {
    return mode === 'active' || mode === 'on';
  }

  return false;
}

function miningButtonLabel() {
  return resourceMinerRunning.value ? 'Stop Mining' : 'Start Mining';
}

function formatCargoFill(current: number, maximum: number) {
  return `${formatMetric(current)} / ${formatMetric(maximum)}`;
}
</script>

<template>
  <section v-if="activeControllableId" class="command-dock panel-glass">
        <div class="dock-group-stack dock-group-stack--combat">
          <div class="dock-group">
            <span class="dock-label">Tactical</span>
            <button class="dock-btn" :class="{ active: tacticalMode === 'enemy' }" type="button" @click="setTactical('enemy')">Auto</button>
            <button class="dock-btn" :class="{ active: tacticalMode === 'target' }" type="button" @click="setTactical('target')">Target</button>
            <button class="dock-btn" :class="{ active: tacticalMode === 'off' }" type="button" @click="setTactical('off')">Off</button>
          </div>
          <span class="dock-sep dock-sep--horizontal" aria-hidden="true"></span>
          <div class="dock-group">
            <span class="dock-label">Thrust</span>
            <input v-model.number="thrust" class="dock-slider" type="range" min="0" max="1" step="0.05" />
            <span class="dock-value">{{ thrust.toFixed(2) }}</span>
            <!-- <button class="dock-btn" type="button" @click="gateway.fireWeapon(activeControllableId)">Fire</button> -->
          </div>
        </div>

    <span class="dock-sep" aria-hidden="true"></span>

    <div class="dock-group-stack dock-group-stack--scanner">
      <div class="dock-group">
        <span class="dock-label">Scanner</span>
        <button class="dock-btn" :class="{ active: scannerMode === '360' }" type="button" @click="setScanner('360')">360°</button>
        <button class="dock-btn" :class="{ active: scannerMode === 'forward' }" type="button" @click="setScanner('forward')">Fwd</button>
        <button class="dock-btn" :class="{ active: scannerMode === 'hold' }" type="button" @click="setScanner('hold')">Hold</button>
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

    <div
      v-if="resourceMiner || nebulaCollector"
      class="dock-group-stack dock-group-stack--harvest"
    >
      <div class="dock-group">
        <span class="dock-label">Harvest</span>
        <button
          v-if="resourceMiner"
          class="dock-btn"
          :class="{ active: resourceMinerRunning }"
          type="button"
          @click="toggleResourceMiner"
        >
          {{ miningButtonLabel() }}
        </button>
        <button
          v-if="nebulaCollector"
          class="dock-btn"
          :class="{ active: nebulaCollectorRunning }"
          type="button"
          @click="toggleNebulaCollector"
        >
          {{ nebulaCollectorRunning ? 'Stop Neb' : 'Nebula' }}
        </button>
      </div>
      <div v-if="resourceMiner" class="dock-group dock-group--harvest-metric">
        <span class="dock-label">Mine</span>
        <input
          :value="resourceMinerRate"
          class="dock-slider"
          type="range"
          min="0"
          :max="resourceMinerMaximumRate"
          step="0.0001"
          @input="setResourceMinerRate(Number(($event.target as HTMLInputElement).value))"
        />
        <span class="dock-value">{{ resourceMinerRate.toFixed(4) }}</span>
      </div>
      <div v-if="activeMinedResources.length > 0" class="dock-group dock-group--harvest-status">
        <span
          v-for="resource in activeMinedResources"
          :key="resource.key"
          class="dock-harvest-chip"
          :title="resource.cargo ? `${resource.label}: ${formatCargoFill(resource.cargo.current, resource.cargo.maximum)}` : resource.label"
        >
          <strong>{{ resource.label }}</strong>
          <span>{{ resource.yieldPerTick.toFixed(3) }}/t</span>
          <span v-if="resource.cargo">{{ formatCargoFill(resource.cargo.current, resource.cargo.maximum) }}</span>
        </span>
      </div>
      <div v-if="nebulaCollector" class="dock-group dock-group--harvest-metric">
        <span class="dock-label">Neb</span>
        <input
          :value="nebulaCollectorRate"
          class="dock-slider"
          type="range"
          min="0"
          :max="nebulaCollectorMaximumRate"
          step="0.001"
          @input="setNebulaCollectorRate(Number(($event.target as HTMLInputElement).value))"
        />
        <span class="dock-value">{{ nebulaCollectorRate.toFixed(3) }}</span>
      </div>
    </div>

    <span v-if="resourceMiner || nebulaCollector" class="dock-sep" aria-hidden="true"></span>

    <div class="dock-group-stack dock-group-stack--utility">
      <div class="dock-group">
        <button class="dock-btn" type="button" @click="gateway.clearNavigationTarget(activeControllableId)">Clear Nav</button>
        <button v-if="shotFabricator" class="dock-btn" :class="{ active: shotFabricatorRunning }" type="button" @click="toggleShotRegeneration">
          {{ shotFabricatorRunning ? 'Stop Regen' : 'Regen Shots' }}
        </button>
      </div>
      <div class="dock-group dock-group--lock">
        <button
          class="dock-btn dock-btn--wide"
          :class="{ active: isFocusSelectionActive }"
          :aria-pressed="isFocusSelectionActive"
          type="button"
          @click="requestFocusSelectionToggle()"
        >
          Lock onto ship
        </button>
      </div>
    </div>
  </section>
</template>
