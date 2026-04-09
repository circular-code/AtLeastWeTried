<script setup lang="ts">
import { computed, ref } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';
import type { ScannerMode, TacticalMode } from '../../types/client';

const gateway = useGateway();
const gameStore = useGameStore();
const uiStore = useUiStore();

const thrust = ref(0.25);
const tacticalMode = ref<TacticalMode>('off');

const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const scannerMode = computed<ScannerMode>(() => gameStore.scannerModeFor(activeControllableId.value));

function setScanner(mode: ScannerMode) {
  gateway.setScannerMode(activeControllableId.value, mode);
}

function setTactical(mode: TacticalMode) {
  tacticalMode.value = mode;
  gateway.setTacticalMode(activeControllableId.value, mode);
}
</script>

<template>
  <section v-if="activeControllableId" class="command-dock panel-glass">
    <div class="dock-group">
      <span class="dock-label">Thrust</span>
      <input v-model.number="thrust" class="dock-slider" type="range" min="0" max="1" step="0.05" />
      <span class="dock-value">{{ thrust.toFixed(2) }}</span>
      <button class="dock-btn" type="button" @click="gateway.setEngine(activeControllableId, thrust)">Apply</button>
      <button class="dock-btn" type="button" @click="gateway.fireWeapon(activeControllableId)">Fire</button>
    </div>

    <span class="dock-sep" aria-hidden="true"></span>

    <div class="dock-group">
      <span class="dock-label">Scanner</span>
      <button class="dock-btn" :class="{ active: scannerMode === '360' }" type="button" @click="setScanner('360')">360°</button>
      <button class="dock-btn" :class="{ active: scannerMode === 'forward' }" type="button" @click="setScanner('forward')">Fwd</button>
      <button class="dock-btn" :class="{ active: scannerMode === 'off' }" type="button" @click="setScanner('off')">Off</button>
    </div>

    <span class="dock-sep" aria-hidden="true"></span>

    <div class="dock-group">
      <span class="dock-label">Tactical</span>
      <button class="dock-btn" :class="{ active: tacticalMode === 'enemy' }" type="button" @click="setTactical('enemy')">Enemy</button>
      <button class="dock-btn" :class="{ active: tacticalMode === 'target' }" type="button" @click="setTactical('target')">Target</button>
      <button class="dock-btn" :class="{ active: tacticalMode === 'off' }" type="button" @click="setTactical('off')">Off</button>
      <button class="dock-btn" type="button" @click="gateway.clearNavigationTarget(activeControllableId)">Clear Nav</button>
    </div>
  </section>
</template>