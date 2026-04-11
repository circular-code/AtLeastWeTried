<script setup lang="ts">
import { computed } from 'vue';
import ModalBackdrop from '../../shared/ModalBackdrop.vue';
import { useUiStore } from '../../stores/ui';

type KeybindEntry = {
  input: string;
  action: string;
  detail?: string;
};

type KeybindSection = {
  title: string;
  entries: KeybindEntry[];
};

const uiStore = useUiStore();

const sections = computed<KeybindSection[]>(() => [
  {
    title: 'Mouse',
    entries: [
      {
        input: 'Left Click',
        action: 'Select a ship or unit',
        detail: 'Owned ships become the active controllable when selected.',
      },
      {
        input: 'Drag Left Click',
        action: 'Pan the map',
        detail: 'If focus-follow is active, dragging breaks the follow camera.',
      },
      {
        input: 'Right Click',
        action: 'Navigate to the clicked point',
        detail: 'If you click near a unit, the target includes that unit context.',
      },
      {
        input: 'Shift + Right Click',
        action: 'Direct / free navigate',
        detail: 'Sends a direct navigation target instead of the standard route.',
      },
      {
        input: 'Ctrl + Shift + Left Click',
        action: 'Shoot / free fire at the clicked point',
        detail: 'Fires at world coordinates, optionally centered on the nearest unit.',
      },
      {
        input: 'Mouse Wheel',
        action: 'Zoom the viewport',
      },
      {
        input: 'Shift + Mouse Wheel',
        action: 'Adjust navigation thrust / max speed',
      },
    ],
  },
  {
    title: 'Keyboard',
    entries: [
      {
        input: 'Q',
        action: 'Tactical auto',
      },
      {
        input: 'W',
        action: 'Tactical target',
      },
      {
        input: 'E',
        action: 'Tactical off',
      },
      {
        input: 'A',
        action: 'Toggle shot regeneration',
        detail: 'Available when the active ship has a Shot Fabricator.',
      },
      {
        input: 'S',
        action: 'Clear navigation target',
      },
      {
        input: 'D',
        action: 'Lock onto ship',
      },
      {
        input: 'T',
        action: 'Set selected unit as tactical target',
        detail: 'Switches to target mode if not already active, then sets the selected unit as the target.',
      },
      {
        input: 'F',
        action: 'Scanner off',
      },
      {
        input: '1',
        action: 'Scanner 360',
      },
      {
        input: '2',
        action: 'Scanner forward',
      },
      {
        input: '3',
        action: 'Scanner hold',
      },
      {
        input: '4',
        action: 'Scanner sweep',
      },
      {
        input: 'Space',
        action: 'Center camera on the followed selection',
      },
      {
        input: 'Esc',
        action: 'Close open popups and modals',
      },
    ],
  },
]);

function close() {
  uiStore.isKeybindsPopupOpen = false;
}
</script>

<template>
  <ModalBackdrop panel-class="keybinds-modal-panel" @close="close">
    <div class="modal-header">
      <h2 class="panel-title">Controls</h2>
      <button class="button-ghost" type="button" @click="close">Close</button>
    </div>

    <div class="keybinds-modal-body">
      <section v-for="section in sections" :key="section.title" class="panel stack">
        <h3 class="panel-title">{{ section.title }}</h3>

        <div class="keybinds-list">
          <article v-for="entry in section.entries" :key="`${section.title}-${entry.input}`" class="keybinds-row">
            <div class="keybinds-copy">
              <strong>{{ entry.action }}</strong>
              <p v-if="entry.detail" class="panel-copy">{{ entry.detail }}</p>
            </div>

            <kbd class="keybinds-input">{{ entry.input }}</kbd>
          </article>
        </div>
      </section>
    </div>
  </ModalBackdrop>
</template>
