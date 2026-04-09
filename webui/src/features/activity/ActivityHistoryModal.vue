<script setup lang="ts">
import { formatActivityTime } from '../../lib/formatting';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';
import ModalBackdrop from '../../shared/ModalBackdrop.vue';

const gameStore = useGameStore();
const uiStore = useUiStore();

function close() {
  uiStore.isActivityHistoryOpen = false;
}
</script>

<template>
  <ModalBackdrop panel-class="activity-history-panel" @close="close">
    <div class="modal-header">
      <h2 class="panel-title">Activity</h2>
      <button class="button-ghost" type="button" @click="close">Close</button>
    </div>

    <div class="activity-history-list panel-scroll">
      <article v-for="entry in gameStore.activityEntries" :key="entry.id" class="activity-item activity-item-history" :class="`tone-${entry.tone}`">
        <header>
          <strong>{{ entry.summary }}</strong>
          <span>{{ formatActivityTime(entry.createdAt) }}</span>
        </header>
        <p v-if="entry.detail">{{ entry.detail }}</p>
        <small v-if="entry.meta">{{ entry.meta }}</small>
      </article>
    </div>
  </ModalBackdrop>
</template>