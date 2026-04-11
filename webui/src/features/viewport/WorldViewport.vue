<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { numberValue, objectValue } from '../../lib/validation';
import { readNavigationTarget, type WorldSceneSelection, WorldScene } from '../../renderer/WorldScene';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';
import ScoreStrip from './ScoreStrip.vue';

const gameStore = useGameStore();
const uiStore = useUiStore();
const gateway = useGateway();

const host = ref<HTMLDivElement | null>(null);
const damageFlashToken = ref(0);
const damageFlashStrength = ref(0);
let worldScene: WorldScene | null = null;

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

function setFocusSelectionActive(value: boolean) {
  if (typeof uiStore.setFocusSelectionActive === 'function') {
    uiStore.setFocusSelectionActive(value);
    return;
  }

  uiStore.isFocusSelectionActive = value;
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
    uiStore.navigationMaxSpeedFraction,
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
  setFocusSelectionActive(isActive);
}

function handleThrustWheel(delta: number) {
  const controllableId = selectedControllableId.value;
  if (!controllableId || !Number.isFinite(delta) || delta === 0) {
    return;
  }

  const nextSpeed = Math.round(
    Math.min(
      1,
      Math.max(0, uiStore.navigationMaxSpeedFraction - delta * 0.0005),
    ) * 20,
  ) / 20;

  if (nextSpeed === uiStore.navigationMaxSpeedFraction) {
    return;
  }

  uiStore.setNavigationMaxSpeedFraction(nextSpeed);

  const navigationTarget = readNavigationTarget(gameStore.ownerOverlay, controllableId);
  if (navigationTarget) {
    gateway.setNavigationTarget(controllableId, navigationTarget.x, -navigationTarget.y, nextSpeed);
  }
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

watch(
  () => uiStore.focusSelectionRequestToken,
  (requestToken, previousToken) => {
    if (requestToken === previousToken || requestToken === 0) {
      return;
    }

    worldScene?.toggleFocusSelection();
  },
);

onBeforeUnmount(() => {
  uiStore.setVisibleUnitIds([]);
  setFocusSelectionActive(false);
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
  </section>
</template>
