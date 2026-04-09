<script setup lang="ts">
import { computed } from 'vue';
import { clamp01 } from '../lib/formatting';
import type { OverlayMeter } from '../types/client';

const props = defineProps<{
  meter: OverlayMeter;
}>();

const toneColor = computed(() => {
  switch (props.meter.tone) {
    case 'hull':
      return '#ef9b72';
    case 'shield':
      return '#7abcf8';
    case 'energy':
    default:
      return '#86e6d8';
  }
});

const style = computed(() => ({
  '--gauge-ratio': `${clamp01(props.meter.ratio) > 0 ? Math.max(clamp01(props.meter.ratio), 0.03) : 0}`,
  '--gauge-fill': toneColor.value,
}));
</script>

<template>
  <div class="owner-overlay-gauge-shell" :class="[`tone-${meter.tone}`, { 'is-inactive': meter.inactive }]">
    <div class="owner-overlay-gauge" :style="style">
      <div class="owner-overlay-gauge-core">
        <strong>{{ Math.round(clamp01(meter.ratio) * 100) }}%</strong>
        <span>{{ meter.label }}</span>
      </div>
    </div>
    <small class="subtle-copy">{{ meter.valueText }}</small>
  </div>
</template>