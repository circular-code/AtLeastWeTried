<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { formatTeamAccent, type WorldSceneSelection, WorldScene } from '../lib/worldScene';
import type { GalaxySnapshotDto } from '../types/generated';

type NavigationTarget = {
  x: number;
  y: number;
} | null;

const props = defineProps<{
  snapshot: GalaxySnapshotDto | null;
  ownerOverlay: Record<string, unknown>;
  selectedControllableId: string;
  navigationTarget: NavigationTarget;
}>();

const emit = defineEmits<{
  worldSelect: [selection: WorldSceneSelection];
  worldNavigate: [selection: WorldSceneSelection];
}>();

const host = ref<HTMLDivElement | null>(null);
let worldScene: WorldScene | null = null;

const clusterLabel = computed(() => {
  if (!props.snapshot || props.snapshot.clusters.length === 0) {
    return 'No cluster data';
  }

  if (props.snapshot.clusters.length === 1) {
    return props.snapshot.clusters[0]?.name ?? 'Unknown cluster';
  }

  return `${props.snapshot.clusters.length} clusters loaded`;
});

const highlightedControllable = computed(() => {
  if (!props.snapshot || !props.selectedControllableId) {
    return null;
  }

  return props.snapshot.controllables.find((controllable) => controllable.controllableId === props.selectedControllableId) ?? null;
});

const accent = computed(() => formatTeamAccent(highlightedControllable.value?.teamName, props.snapshot?.teams ?? []));

function focusSelection() {
  worldScene?.focusOnSelection();
}

onMounted(() => {
  if (!host.value) {
    return;
  }

  worldScene = new WorldScene(host.value, {
    onSelection(selection) {
      emit('worldSelect', selection);
    },
    onNavigationTargetRequested(selection) {
      emit('worldNavigate', selection);
    },
  });

  worldScene.setSnapshot(props.snapshot, props.ownerOverlay, props.selectedControllableId, props.navigationTarget);
});

watch(
  () => [props.snapshot, props.ownerOverlay, props.selectedControllableId, props.navigationTarget] as const,
  ([snapshot, ownerOverlay, selectedControllableId, navigationTarget]) => {
    worldScene?.setSnapshot(snapshot, ownerOverlay, selectedControllableId, navigationTarget);
  },
  { deep: true }
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