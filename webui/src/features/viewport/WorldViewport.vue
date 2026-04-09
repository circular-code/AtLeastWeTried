<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { formatTeamAccent, readNavigationTarget, type WorldSceneSelection, WorldScene } from '../../renderer/WorldScene';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';

const gameStore = useGameStore();
const uiStore = useUiStore();
const gateway = useGateway();

const host = ref<HTMLDivElement | null>(null);
let worldScene: WorldScene | null = null;

const snapshot = computed(() => gameStore.snapshot);
const ownerOverlay = computed(() => gameStore.ownerOverlay);
const selectedControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const navigationTarget = computed(() => selectedControllableId.value ? readNavigationTarget(ownerOverlay.value, selectedControllableId.value) : null);

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
  worldScene?.focusOnSelection();
}

function handleWorldSelect(selection: WorldSceneSelection) {
  uiStore.setLastSelection(selection);
}

function handleWorldNavigate(selection: WorldSceneSelection) {
  uiStore.setLastSelection(selection);
  gateway.setNavigationTarget(selectedControllableId.value, selection.worldX, selection.worldY);
}

onMounted(() => {
  if (!host.value) {
    return;
  }

  worldScene = new WorldScene(host.value, {
    onSelection: handleWorldSelect,
    onNavigationTargetRequested: handleWorldNavigate,
  });

  worldScene.setSnapshot(snapshot.value, ownerOverlay.value, selectedControllableId.value, navigationTarget.value);
});

watch(
  () => [snapshot.value, ownerOverlay.value, selectedControllableId.value, navigationTarget.value] as const,
  ([nextSnapshot, nextOwnerOverlay, nextSelectedControllableId, nextNavigationTarget]) => {
    worldScene?.setSnapshot(nextSnapshot, nextOwnerOverlay, nextSelectedControllableId, nextNavigationTarget);
  },
  { deep: true },
);

onBeforeUnmount(() => {
  worldScene?.dispose();
  worldScene = null;
});
</script>

<template>
  <section class="viewport-shell">
    <div ref="host" class="viewport-host"></div>
    <div class="viewport-hud">
      <div class="viewport-chip">{{ clusterLabel }}</div>
      <button class="viewport-chip viewport-button" type="button" @click="focusSelection">Focus Selection</button>
      <div class="viewport-chip viewport-highlight" :style="{ '--accent': accent }">
        {{ highlightedControllable?.displayName ?? 'No active controllable' }}
      </div>
    </div>
  </section>
</template>