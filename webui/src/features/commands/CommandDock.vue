<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';
import type { ScannerMode, TacticalMode } from '../../types/client';

const gateway = useGateway();
const gameStore = useGameStore();
const uiStore = useUiStore();
const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));

const thrust = computed({
  get: () => uiStore.navigationThrustPercentage,
  set: (value: number) => {
    uiStore.setNavigationThrustPercentage(value);
    gateway.setEngine(activeControllableId.value, value);
  },
});
const tacticalMode = ref<TacticalMode>('off');
const scannerMode = computed<ScannerMode>(() => gameStore.scannerModeFor(activeControllableId.value));
const scannerWidthMin = computed(() => gameStore.scannerWidthMinimumFor(activeControllableId.value));
const scannerWidthMax = computed(() => gameStore.scannerWidthMaximumFor(activeControllableId.value));
const scannerWidth = ref(90);

watch(
  () => ({
    controllableId: activeControllableId.value,
    width: gameStore.scannerWidthFor(activeControllableId.value),
    min: scannerWidthMin.value,
    max: scannerWidthMax.value,
  }),
  ({ width, min, max }) => {
    scannerWidth.value = Math.min(Math.max(width, min), max);
  },
  { immediate: true },
);

function setScanner(mode: ScannerMode) {
  gateway.setScannerMode(activeControllableId.value, mode);
}

function setScannerWidth(value: number) {
  const controllableId = activeControllableId.value;
  if (!controllableId) {
    return;
  }

  const clampedWidth = Math.min(Math.max(value, scannerWidthMin.value), scannerWidthMax.value);
  scannerWidth.value = clampedWidth;
  gateway.setScannerWidth(controllableId, clampedWidth);
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
      <button class="dock-btn" type="button" @click="gateway.fireWeapon(activeControllableId)">Fire</button>
    </div>

    <span class="dock-sep" aria-hidden="true"></span>

    <div class="dock-group-stack">
      <div class="dock-group">
        <span class="dock-label">Scanner</span>
        <button class="dock-btn" :class="{ active: scannerMode === '360' }" type="button" @click="setScanner('360')">360°</button>
        <button class="dock-btn" :class="{ active: scannerMode === 'forward' }" type="button" @click="setScanner('forward')">Fwd</button>
        <button class="dock-btn" :class="{ active: scannerMode === 'sweep' }" type="button" @click="setScanner('sweep')">Sweep</button>
        <button class="dock-btn" :class="{ active: scannerMode === 'off' }" type="button" @click="setScanner('off')">Off</button>
      </div>
      <div class="dock-group">
        <span class="dock-label">Width</span>
        <input
          :value="scannerWidth"
          class="dock-slider"
          type="range"
          :min="scannerWidthMin"
          :max="scannerWidthMax"
          step="1"
          @input="setScannerWidth(Number(($event.target as HTMLInputElement).value))"
        />
        <span class="dock-value">{{ scannerWidth.toFixed(0) }}&deg;</span>
      </div>
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