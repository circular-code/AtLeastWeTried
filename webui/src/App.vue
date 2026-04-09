<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, watch } from 'vue';
import ActivityHistoryModal from './features/activity/ActivityHistoryModal.vue';
import ActivityToasts from './features/activity/ActivityToasts.vue';
import ChatModal from './features/chat/ChatModal.vue';
import CommandDock from './features/commands/CommandDock.vue';
import DebugLogPanel from './features/debug/DebugLogPanel.vue';
import OverlayPanel from './features/overlay/OverlayPanel.vue';
import SelectionPanel from './features/selection/SelectionPanel.vue';
import SessionManagerModal from './features/session/SessionManagerModal.vue';
import StatusBar from './features/status/StatusBar.vue';
import WorldViewport from './features/viewport/WorldViewport.vue';
import { useGateway } from './composables/useGateway';
import { useGameStore } from './stores/game';
import { useUiStore } from './stores/ui';
import { isEditableTarget } from './lib/validation';

const gateway = useGateway();
const gameStore = useGameStore();
const uiStore = useUiStore();

const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));

const hasSelectionDetails = computed(() => !!gameStore.selectionEntry(uiStore.lastSelection));
const hasMultipleOwnedShips = computed(() => gameStore.ownedControllables.length > 1);

function handleWindowKeydown(event: KeyboardEvent) {
  if (isEditableTarget(event.target)) {
    return;
  }

  if (event.key.toLowerCase() === 's') {
    event.preventDefault();
    gateway.clearNavigationTarget(activeControllableId.value);
    return;
  }

  if (event.key !== 'Escape') {
    return;
  }

  uiStore.closeAllPopups();
}

watch(
  () => gameStore.ownedControllables,
  (entries) => {
    if (entries.length === 0) {
      if (uiStore.selectedControllableId) {
        uiStore.setSelectedControllable('');
      }
      return;
    }

    if (entries.some((entry) => entry.controllableId === uiStore.selectedControllableId)) {
      return;
    }

    uiStore.setSelectedControllable(entries[0]?.controllableId ?? '');
  },
  { immediate: true, deep: true },
);

onMounted(() => {
  gateway.initialize();
  window.addEventListener('keydown', handleWindowKeydown);
});

onBeforeUnmount(() => {
  window.removeEventListener('keydown', handleWindowKeydown);
});
</script>

<template>
  <main class="app-shell">
    <section class="scene-stage">
      <WorldViewport />

      <div
        :class="[
          'overlay-shell',
          {
            'is-debug-log-ingame-active': uiStore.isDebugLogOpen && uiStore.isDebugLogIngame,
            'has-multiple-owned-ships': hasMultipleOwnedShips,
          },
        ]"
      >
        <div class="overlay-top" :class="{ 'overlay-top--has-selection': hasSelectionDetails }">
          <OverlayPanel />
          <SelectionPanel v-if="hasSelectionDetails" />
        </div>

        <ActivityToasts />
        <CommandDock />
        <StatusBar />
      </div>
    </section>

    <SessionManagerModal v-if="uiStore.isManagerPopupOpen" />
    <ChatModal v-if="uiStore.isChatPopupOpen" />
    <ActivityHistoryModal v-if="uiStore.isActivityHistoryOpen" />
    <DebugLogPanel v-if="uiStore.isDebugLogOpen" />
  </main>
</template>
