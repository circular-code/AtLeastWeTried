<script setup lang="ts">
import { computed } from 'vue';
import { useGateway } from '../../composables/useGateway';
import GaugeMeter from '../../shared/GaugeMeter.vue';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';

const gateway = useGateway();
const gameStore = useGameStore();
const uiStore = useUiStore();

const selectionEntry = computed(() => gameStore.selectionEntry(uiStore.lastSelection));
const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const scannerMode = computed(() => gameStore.scannerModeFor(activeControllableId.value));
const scannerTargetId = computed(() => gameStore.scannerTargetFor(activeControllableId.value) ?? '');
const trackedUnitColors = computed(() => uiStore.trackedUnitColors);
const tacticalMode = computed(() => uiStore.tacticalMode);
const canInitiateTargetedScan = computed(() => {
  if (!selectionEntry.value || !activeControllableId.value) {
    return false;
  }

  return selectionEntry.value.id !== activeControllableId.value;
});
const canSetAsTarget = computed(() => {
  if (!selectionEntry.value || !activeControllableId.value) {
    return false;
  }

  return tacticalMode.value === 'target' && selectionEntry.value.id !== activeControllableId.value;
});

function initiateTargetedScan() {
  if (!selectionEntry.value || !activeControllableId.value) {
    return;
  }

  gateway.initiateTargetedScan(activeControllableId.value, selectionEntry.value.id);
}

function toggleTrackedSelection() {
  if (!selectionEntry.value) {
    return;
  }

  uiStore.toggleTrackedUnit(selectionEntry.value.id);
}

function isUnitTracked(unitId: string) {
  return !!trackedUnitColors.value[unitId];
}

function trackedUnitButtonStyle(unitId: string) {
  const color = trackedUnitColors.value[unitId];
  return color ? { '--track-color': color } : undefined;
}

function setAsTarget() {
  if (!selectionEntry.value || !activeControllableId.value) {
    return;
  }

  gateway.setTacticalTarget(activeControllableId.value, selectionEntry.value.id);
}
</script>

<template>
  <aside class="overlay-column overlay-column-right">
    <section v-if="selectionEntry" class="panel selection-unit-card stack">
      <div class="stack">
        <div class="actions-tight overlay-badge-row">
          <span v-for="badge in selectionEntry.badges" :key="badge.label" class="overlay-badge" :class="`is-${badge.tone}`">{{ badge.label }}</span>
        </div>
        <div>
          <h2 class="panel-title">{{ selectionEntry.displayName }}</h2>
          <p class="panel-copy">{{ selectionEntry.kind }} · {{ selectionEntry.clusterLabel }}</p>
        </div>
      </div>

      <dl class="overlay-stat-grid selection-unit-grid">
        <div v-for="stat in selectionEntry.stats" :key="stat.label">
          <dt>{{ stat.label }}</dt>
          <dd>{{ stat.value }}</dd>
        </div>
      </dl>

      <div class="selection-unit-gauges">
        <GaugeMeter v-for="meter in selectionEntry.meters" :key="meter.label" :meter="meter" />
      </div>

      <div class="selection-scan-actions">
        <div class="actions-tight">
          <button class="button-secondary button-compact" type="button" :disabled="!canInitiateTargetedScan" @click="initiateTargetedScan">
            Initiate Scan
          </button>
          <button
            class="button-secondary button-compact"
            :class="{ 'is-active': isUnitTracked(selectionEntry.id) }"
            :style="trackedUnitButtonStyle(selectionEntry.id)"
            type="button"
            @click="toggleTrackedSelection"
          >
            {{ isUnitTracked(selectionEntry.id) ? 'Tracked' : 'Track' }}
          </button>
          <button
            v-if="tacticalMode === 'target'"
            class="button-secondary button-compact"
            type="button"
            :disabled="!canSetAsTarget"
            @click="setAsTarget"
          >
            Set as Target
          </button>
        </div>
        <span v-if="scannerMode === 'targeted' && scannerTargetId === selectionEntry.id" class="selection-scan-status">Target scan active</span>
      </div>

      <section v-for="group in selectionEntry.detailGroups" :key="group.title" class="selection-detail-group" :class="`tone-${group.tone}`">
        <h3>{{ group.title }}</h3>
        <dl class="overlay-stat-grid">
          <div v-for="stat in group.stats" :key="stat.label">
            <dt>{{ stat.label }}</dt>
            <dd>{{ stat.value }}</dd>
          </div>
        </dl>
      </section>
    </section>
  </aside>
</template>
