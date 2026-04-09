<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { formatActivityTime } from '../../lib/formatting';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';

const gameStore = useGameStore();
const uiStore = useUiStore();
const clock = ref(Date.now());
let timer: number | undefined;

const toastLifetimeMs = 8000;
const recentLimit = 3;

const floatingEntries = computed(() => gameStore.activityEntries
  .filter((entry) => clock.value - entry.createdAt < toastLifetimeMs)
  .slice(0, recentLimit));

const olderActivityCount = computed(() => Math.max(0, gameStore.activityEntries.length - floatingEntries.value.length));

onMounted(() => {
  timer = window.setInterval(() => {
    clock.value = Date.now();
  }, 1000);
});

onBeforeUnmount(() => {
  if (timer !== undefined) {
    window.clearInterval(timer);
  }
});
</script>

<template>
  <div v-if="floatingEntries.length > 0 || olderActivityCount > 0" class="activity-tray">
    <div class="activity-list">
      <article v-for="entry in floatingEntries" :key="entry.id" class="activity-item activity-item-toast" :class="`tone-${entry.tone}`">
        <header class="activity-item-head">
          <strong>{{ entry.summary }}</strong>
          <span>{{ formatActivityTime(entry.createdAt) }}</span>
        </header>
        <p v-if="entry.detail">{{ entry.detail }}</p>
        <small v-if="entry.meta">{{ entry.meta }}</small>
      </article>
    </div>
  </div>
</template>