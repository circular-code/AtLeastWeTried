<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { numberValue, objectValue } from '../../lib/validation';
import { formatTeamAccent, readNavigationTarget, type WorldSceneSelection, WorldScene } from '../../renderer/WorldScene';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';

const gameStore = useGameStore();
const uiStore = useUiStore();
const gateway = useGateway();

const host = ref<HTMLDivElement | null>(null);
const isFocusSelectionActive = ref(false);
const damageFlashToken = ref(0);
const damageFlashStrength = ref(0);
let worldScene: WorldScene | null = null;

const snapshot = computed(() => gameStore.snapshot);
const ownerOverlay = computed(() => gameStore.ownerOverlay);
const selectedControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const navigationTarget = computed(() => selectedControllableId.value ? readNavigationTarget(ownerOverlay.value, selectedControllableId.value) : null);
const trackedUnitColors = computed(() => uiStore.trackedUnitColors);
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
    return 'No cluster data';
  }

  if (snapshot.value.clusters.length === 1) {
    return snapshot.value.clusters[0]?.name ?? 'Unknown cluster';
  }

  return `${snapshot.value.clusters.length} clusters loaded`;
});

const highlightedControllable = computed(() => {
  if (!snapshot.value || !selectedControllableId.value) {
    return null;
  }

  return snapshot.value.controllables.find((controllable) => controllable.controllableId === selectedControllableId.value) ?? null;
});

const accent = computed(() => formatTeamAccent(highlightedControllable.value?.teamName, snapshot.value?.teams ?? []));

function focusSelection() {
  worldScene?.toggleFocusSelection();
}

function handleWorldSelect(selection: WorldSceneSelection) {
  uiStore.setLastSelection(selection);
}

function handleWorldNavigate(selection: WorldSceneSelection) {
  uiStore.setLastSelection(selection);
  gateway.setNavigationTarget(
    selectedControllableId.value,
    selection.worldX,
    selection.worldY,
    uiStore.navigationThrustPercentage,
  );
}

function handleVisibleUnitsChanged(unitIds: string[]) {
  uiStore.setVisibleUnitIds(unitIds);
}

function handleFocusSelectionChanged(isActive: boolean) {
  isFocusSelectionActive.value = isActive;
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
    onVisibleUnitsChanged: handleVisibleUnitsChanged,
    onFocusSelectionChanged: handleFocusSelectionChanged,
  });

  worldScene.setSnapshot(snapshot.value, ownerOverlay.value, selectedControllableId.value, navigationTarget.value);
  worldScene.setTrackedUnits(trackedUnitColors.value);
});

watch(
  () => [snapshot.value, ownerOverlay.value, selectedControllableId.value, navigationTarget.value] as const,
  ([nextSnapshot, nextOwnerOverlay, nextSelectedControllableId, nextNavigationTarget]) => {
    worldScene?.setSnapshot(nextSnapshot, nextOwnerOverlay, nextSelectedControllableId, nextNavigationTarget);
  },
);

watch(
  trackedUnitColors,
  (nextTrackedUnitColors) => {
    worldScene?.setTrackedUnits(nextTrackedUnitColors);
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
    <div class="viewport-hud">
      <div class="viewport-chip">{{ clusterLabel }}</div>
      <button
        class="viewport-chip viewport-button"
        :class="{ 'viewport-highlight': isFocusSelectionActive }"
        :style="isFocusSelectionActive ? { '--accent': accent } : undefined"
        :aria-pressed="isFocusSelectionActive"
        type="button"
        @click="focusSelection"
      >
        Focus Selection
      </button>
      <div class="viewport-chip viewport-highlight" :style="{ '--accent': accent }">
        {{ highlightedControllable?.displayName ?? 'No active controllable' }}
      </div>
    </div>
  </section>
</template>
