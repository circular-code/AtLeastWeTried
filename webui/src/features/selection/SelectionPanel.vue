<script setup lang="ts">
import { computed } from 'vue';
import GaugeMeter from '../../shared/GaugeMeter.vue';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';

const gameStore = useGameStore();
const uiStore = useUiStore();

const selectionEntry = computed(() => gameStore.selectionEntry(uiStore.lastSelection));
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