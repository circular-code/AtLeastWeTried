<script setup lang="ts">
import { computed } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';
import GaugeMeter from '../../shared/GaugeMeter.vue';
import ShipCreator from './ShipCreator.vue';

const gameStore = useGameStore();
const uiStore = useUiStore();
const gateway = useGateway();

const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const entries = computed(() => gameStore.ownedControllables
  .map((entry) => gameStore.overlayEntry(entry.controllableId))
  .filter((entry): entry is NonNullable<ReturnType<typeof gameStore.overlayEntry>> => !!entry));

const statIcons: Record<string, string> = {
  Ammo: `<svg viewBox="0 0 16 16" focusable="false"><circle cx="8" cy="8" r="2.8" fill="none" stroke="currentColor" stroke-width="1.3"/><path d="M8 2v2.4M8 11.6V14M2 8h2.4M11.6 8H14" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>`,
  Speed: `<svg viewBox="0 0 16 16" focusable="false"><path d="M2.8 12.5A5.5 5.5 0 1 1 13.2 12.5" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/><path d="M8 9.8 10.5 7" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/><circle cx="8" cy="9.8" r="1.1" fill="currentColor"/></svg>`,
  Heading: `<svg viewBox="0 0 16 16" focusable="false"><path d="M8 2v12" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/><path d="M5.2 5 8 2.2l2.8 2.8" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
  Drive: `<svg viewBox="0 0 16 16" focusable="false"><path d="M6.5 4Q8 2 9.5 4l2 6H4.5z" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/><path d="M5.5 10.5 8 14.5 10.5 10.5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/><path d="M8 10.5v2" stroke="currentColor" stroke-width="1.1" stroke-linecap="round" opacity="0.55"/></svg>`,
};

function selectControllable(controllableId: string) {
  uiStore.setSelectedControllable(controllableId);
}
</script>

<template>
  <aside class="overlay-column overlay-column-left">
    <section class="owner-overlay-panel panel-glass">
      <button
        v-for="entry in entries"
        :key="entry.id"
        class="owner-overlay-item"
        :class="{ 'is-selected': entry.id === activeControllableId }"
        type="button"
        @click="selectControllable(entry.id)"
      >
        <div class="owner-overlay-summary">
          <div class="owner-overlay-head">
            <div class="owner-overlay-title">
              <h3>{{ entry.displayName }}</h3>
              <p>{{ entry.kind }}</p>
            </div>
            <div class="owner-overlay-badges">
              <span v-for="badge in entry.badges" :key="badge.label" class="overlay-badge" :class="`is-${badge.tone}`">{{ badge.label }}</span>
            </div>
          </div>
          <div class="owner-overlay-meta">
            <span>{{ entry.clusterLabel }}</span>
            <span>{{ entry.statusLabel }}</span>
          </div>
        </div>

        <div class="owner-overlay-gauges">
          <GaugeMeter v-for="meter in entry.meters" :key="meter.label" :meter="meter" />
        </div>

        <dl class="overlay-stat-grid owner-overlay-stats">
          <div v-for="stat in entry.stats" :key="stat.label" :title="stat.label">
            <dt>
              <span v-if="statIcons[stat.label]" class="stat-icon" v-html="statIcons[stat.label]" aria-hidden="true" />
              <template v-else>{{ stat.label }}</template>
            </dt>
            <dd>{{ stat.value }}</dd>
          </div>
        </dl>

        <div class="actions-compact">
          <button class="button-secondary button-compact" type="button" @click.stop="gateway.continueShip(entry.id)">Spawn</button>
          <button class="button-ghost button-compact" type="button" @click.stop="gateway.destroyShip(entry.id)">Destroy</button>
          <button class="button-danger button-compact" type="button" @click.stop="gateway.removeShip(entry.id)">Remove</button>
        </div>
      </button>

      <ShipCreator @create="gateway.createShip($event)" />
    </section>
  </aside>
</template>