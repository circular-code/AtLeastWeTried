<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { formatMetric } from '../../lib/formatting';
import { numberValue, objectValue } from '../../lib/validation';
import { readNavigationTarget, type WorldSceneSelection, WorldScene } from '../../renderer/WorldScene';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';
import ScoreStrip from './ScoreStrip.vue';

type EnergyTelemetrySnapshot = {
  collection: string;
  drain: string;
  delta: string;
  deltaTone: 'positive' | 'negative' | 'neutral';
};

const gameStore = useGameStore();
const uiStore = useUiStore();
const gateway = useGateway();

const host = ref<HTMLDivElement | null>(null);
const isFocusSelectionActive = ref(false);
const damageFlashToken = ref(0);
const damageFlashStrength = ref(0);
const displayedEnergyTelemetry = ref<EnergyTelemetrySnapshot>({
  collection: '0',
  drain: '0',
  delta: '0',
  deltaTone: 'neutral',
});
let worldScene: WorldScene | null = null;
let energyTelemetryIntervalId: number | null = null;

const snapshot = computed(() => gameStore.snapshot);
const ownerOverlay = computed(() => gameStore.ownerOverlay);
const selectedControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const ownedControllableIds = computed(() => new Set(gameStore.ownedControllables.map((entry) => entry.controllableId)));
const navigationTarget = computed(() => selectedControllableId.value ? readNavigationTarget(ownerOverlay.value, selectedControllableId.value) : null);
const tacticalTargetUnitId = computed(() => selectedControllableId.value
  ? (uiStore.tacticalTargetsByControllableId[selectedControllableId.value] ?? '')
  : '');
const trackedUnitColors = computed(() => uiStore.trackedUnitColors);
const customShipColors = computed(() => uiStore.customShipColors);
const activeOverlayEntry = computed(() => gameStore.overlayEntry(selectedControllableId.value));
const activeOverlayState = computed(() => {
  const controllableId = selectedControllableId.value;
  if (!controllableId) {
    return {};
  }

  return objectValue(ownerOverlay.value[controllableId]) ?? {};
});
const activeSubsystems = computed(() => readSubsystemEntries(activeOverlayState.value.subsystems ?? activeOverlayState.value.modules));
const ownedVitalTotals = computed(() => {
  const totals = new Map<string, number>();

  for (const controllable of gameStore.ownedControllables) {
    const overlayState = objectValue(ownerOverlay.value[controllable.controllableId]) ?? {};
    const hullState = objectValue(overlayState.hull);
    const shieldState = objectValue(overlayState.shield);
    const hullCurrent = numberValue(hullState?.current, 0);
    const shieldCurrent = numberValue(shieldState?.current, 0);

    totals.set(controllable.controllableId, Math.max(0, hullCurrent) + Math.max(0, shieldCurrent));
  }

  return totals;
});
const clusterLabel = computed(() => {
  if (!snapshot.value || snapshot.value.clusters.length === 0) {
    return 'No map';
  }

  if (activeOverlayEntry.value?.clusterLabel) {
    return activeOverlayEntry.value.clusterLabel;
  }

  if (snapshot.value.clusters.length === 1) {
    return snapshot.value.clusters[0]?.name ?? 'Unknown map';
  }

  return `${snapshot.value.clusters.length} maps loaded`;
});
const activeMeters = computed(() => {
  const meters = activeOverlayEntry.value?.meters ?? [];
  return {
    hull: meters.find((meter) => meter.label === 'Hull') ?? null,
    shield: meters.find((meter) => meter.label === 'Shield') ?? null,
    battery: meters.find((meter) => meter.label === 'Battery') ?? null,
  };
});
const energyTelemetry = computed<EnergyTelemetrySnapshot>(() => {
  const collection = readSubsystemResourceValue(activeSubsystems.value, 'Energy Cell', ['Collected', 'Charge per tick', 'Charge']);
  const drain = readSubsystemResourceValue(activeSubsystems.value, 'Energy Battery', ['Drain', 'Drain per tick']);
  const delta = collection !== null || drain !== null
    ? (collection ?? 0) - (drain ?? 0)
    : null;
  const formatNumericValue = (value: number | null) => (value === null ? '0' : value.toFixed(2).replace(/\.?0+$/, ''));

  return {
    collection: formatNumericValue(collection),
    drain: formatNumericValue(drain),
    delta: delta === null ? '0' : `${delta >= 0 ? '+' : ''}${formatNumericValue(delta)}`,
    deltaTone: delta === null ? 'neutral' : delta >= 0 ? 'positive' : 'negative',
  };
});

function syncDisplayedEnergyTelemetry() {
  displayedEnergyTelemetry.value = energyTelemetry.value;
}

type ShipSubsystemStat = {
  label: string;
  value: string;
};

type ShipSubsystemEntry = {
  name: string;
  stats: ShipSubsystemStat[];
};

function readSubsystemEntries(value: unknown): ShipSubsystemEntry[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => objectValue(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => ({
      name: humanizeSubsystemName(readText(entry.name, readText(entry.slot, 'Subsystem'))),
      stats: readSubsystemStats(entry.stats),
    }));
}

function readSubsystemStats(value: unknown): ShipSubsystemStat[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => objectValue(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => ({
      label: readText(entry.label, 'Stat'),
      value: readText(entry.value),
    }))
    .filter((entry) => entry.value.trim().length > 0);
}

function readSubsystemResourceValue(subsystemsToRead: ShipSubsystemEntry[], subsystemName: string, statLabels: string[]) {
  const subsystem = subsystemsToRead.find((entry) => entry.name === subsystemName);
  const stat = subsystem?.stats.find((entry) => statLabels.includes(entry.label));
  if (!stat) {
    return null;
  }

  return readLeadingMetric(stat.value);
}

function readLeadingMetric(value: string) {
  const match = value.match(/-?\d+(?:[.,]\d+)?/);
  if (!match) {
    return null;
  }

  const normalized = match[0].replace(',', '.');
  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

function readText(value: unknown, fallback = '') {
  return typeof value === 'string' ? value : fallback;
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

function handleWorldSelect(selection: WorldSceneSelection) {
  const clickedControllableId = selection.unitId ?? '';
  if (clickedControllableId && ownedControllableIds.value.has(clickedControllableId)) {
    uiStore.setSelectedControllable(clickedControllableId);
  }

  uiStore.setLastSelection(selection);
}

function handleWorldNavigate(selection: WorldSceneSelection) {
  uiStore.setLastSelection(selection);
  gateway.setNavigationTarget(
    selectedControllableId.value,
    selection.worldX,
    selection.worldY,
    uiStore.navigationThrustPercentage,
    selection.direct,
  );
}

function handleWorldFreeFire(selection: WorldSceneSelection) {
  uiStore.setLastSelection(selection);
  gateway.fireWeaponAt(
    selectedControllableId.value,
    selection.worldX,
    selection.worldY,
  );
}

function handleVisibleUnitsChanged(unitIds: string[]) {
  uiStore.setVisibleUnitIds(unitIds);
}

function handleFocusSelectionChanged(isActive: boolean) {
  isFocusSelectionActive.value = isActive;
}

function handleThrustWheel(delta: number) {
  const controllableId = selectedControllableId.value;
  if (!controllableId || !Number.isFinite(delta) || delta === 0) {
    return;
  }

  const nextThrust = Math.round(
    Math.min(
      1,
      Math.max(0, uiStore.navigationThrustPercentage - delta * 0.0005),
    ) * 20,
  ) / 20;

  if (nextThrust === uiStore.navigationThrustPercentage) {
    return;
  }

  uiStore.setNavigationThrustPercentage(nextThrust);
  gateway.setEngine(controllableId, nextThrust);
}

function toggleFocusActiveShip() {
  worldScene?.toggleFocusSelection();
}

function triggerDamageFlash(damageRatio: number) {
  damageFlashStrength.value = Math.min(1, Math.max(0.35, damageRatio));
  damageFlashToken.value += 1;
}

onMounted(() => {
  if (!host.value) {
    return;
  }

  worldScene = new WorldScene(host.value, {
    onSelection: handleWorldSelect,
    onNavigationTargetRequested: handleWorldNavigate,
    onFreeFireRequested: handleWorldFreeFire,
    onVisibleUnitsChanged: handleVisibleUnitsChanged,
    onFocusSelectionChanged: handleFocusSelectionChanged,
    onThrustWheelRequested: handleThrustWheel,
  });

  worldScene.setSnapshot(
    snapshot.value,
    ownerOverlay.value,
    selectedControllableId.value,
    navigationTarget.value,
    tacticalTargetUnitId.value,
  );
  worldScene.setTrackedUnits(trackedUnitColors.value);
  worldScene.setCustomShipColors(customShipColors.value);
  syncDisplayedEnergyTelemetry();
  energyTelemetryIntervalId = window.setInterval(() => {
    syncDisplayedEnergyTelemetry();
  }, 500);
});

watch(
  () => [snapshot.value, ownerOverlay.value, selectedControllableId.value, navigationTarget.value, tacticalTargetUnitId.value] as const,
  ([nextSnapshot, nextOwnerOverlay, nextSelectedControllableId, nextNavigationTarget, nextTacticalTargetUnitId]) => {
    worldScene?.setSnapshot(nextSnapshot, nextOwnerOverlay, nextSelectedControllableId, nextNavigationTarget, nextTacticalTargetUnitId);
  },
);

watch(
  trackedUnitColors,
  (nextTrackedUnitColors) => {
    worldScene?.setTrackedUnits(nextTrackedUnitColors);
  },
  { deep: true },
);

watch(
  customShipColors,
  (nextCustomShipColors) => {
    worldScene?.setCustomShipColors(nextCustomShipColors);
  },
  { deep: true },
);

let previousOwnedVitalTotals = new Map<string, number>();
watch(
  ownedVitalTotals,
  (nextTotals) => {
    let strongestHitRatio = 0;

    for (const [controllableId, nextTotal] of nextTotals.entries()) {
      const previousTotal = previousOwnedVitalTotals.get(controllableId);
      if (previousTotal === undefined || previousTotal <= 0 || nextTotal >= previousTotal) {
        continue;
      }

      strongestHitRatio = Math.max(strongestHitRatio, (previousTotal - nextTotal) / previousTotal);
    }

    previousOwnedVitalTotals = new Map(nextTotals);

    if (strongestHitRatio > 0.0001) {
      triggerDamageFlash(strongestHitRatio * 2.8);
    }
  },
  { immediate: true },
);

watch(
  () => uiStore.viewportJumpTargetId,
  (targetUnitId) => {
    if (!targetUnitId) {
      return;
    }

    worldScene?.jumpToUnit(targetUnitId);
    uiStore.clearViewportJump();
  },
);

onBeforeUnmount(() => {
  uiStore.setVisibleUnitIds([]);
  isFocusSelectionActive.value = false;
  if (energyTelemetryIntervalId !== null) {
    window.clearInterval(energyTelemetryIntervalId);
    energyTelemetryIntervalId = null;
  }
  worldScene?.dispose();
  worldScene = null;
});
</script>

<template>
  <section class="viewport-shell">
    <div ref="host" class="viewport-host"></div>
    <div
      v-if="damageFlashToken > 0"
      :key="damageFlashToken"
      class="damage-flash"
      :style="{ '--damage-flash-strength': damageFlashStrength.toFixed(3) }"
    ></div>
    <ScoreStrip />
    <header class="viewport-command-bar panel-glass">
      <div class="viewport-command-group viewport-command-group--bars">
        <div class="viewport-status-bar-block">
          <span class="viewport-command-label">Hull</span>
          <div class="viewport-meter-track">
            <div class="viewport-meter-fill is-hull" :style="{ '--meter-ratio': activeMeters.hull?.ratio ?? 0 }"></div>
          </div>
        </div>
        <div class="viewport-status-bar-block">
          <span class="viewport-command-label">Shield</span>
          <div class="viewport-meter-track">
            <div class="viewport-meter-fill is-shield" :style="{ '--meter-ratio': activeMeters.shield?.ratio ?? 0 }"></div>
          </div>
        </div>
        <div class="viewport-status-bar-block">
          <span class="viewport-command-label">Battery</span>
          <div class="viewport-meter-track">
            <div class="viewport-meter-fill is-energy" :style="{ '--meter-ratio': activeMeters.battery?.ratio ?? 0 }"></div>
          </div>
        </div>
      </div>

      <span class="dock-sep viewport-command-sep" aria-hidden="true"></span>

      <div class="viewport-command-group viewport-command-group--energy">
        <div class="viewport-energy-stat">
          <span class="viewport-command-label">Collect</span>
          <strong class="is-positive">{{ displayedEnergyTelemetry.collection }}</strong>
        </div>
        <div class="viewport-energy-stat">
          <span class="viewport-command-label">Drain</span>
          <strong class="is-negative">{{ displayedEnergyTelemetry.drain }}</strong>
        </div>
        <div class="viewport-energy-stat">
          <span class="viewport-command-label">Delta</span>
          <strong :class="`is-${displayedEnergyTelemetry.deltaTone}`">{{ displayedEnergyTelemetry.delta }}</strong>
        </div>
      </div>

      <span class="dock-sep viewport-command-sep" aria-hidden="true"></span>

      <div class="viewport-command-group viewport-command-group--actions">
        <button
          class="dock-btn viewport-command-btn"
          :class="{ active: isFocusSelectionActive }"
          :aria-pressed="isFocusSelectionActive"
          type="button"
          @click="toggleFocusActiveShip"
        >
          Lock onto ship
        </button>
        <span class="viewport-map-pill">{{ clusterLabel }}</span>
      </div>
    </header>
  </section>
</template>
