<script setup lang="ts">
import { ref } from 'vue';
import type { ShipCreateRequest } from '../../types/client';

const emit = defineEmits<{
  create: [request: ShipCreateRequest];
}>();

const shipName = ref(createDefaultShipName());

function createShip(shipClass: ShipCreateRequest['shipClass']) {
  emit('create', {
    name: shipName.value,
    shipClass,
  });
  shipName.value = createDefaultShipName();
}

function createDefaultShipName() {
  return `Aurora Wing ${Math.floor(100000 + Math.random() * 900000)}`;
}
</script>

<template>
  <section class="panel ship-creator stack">
    <label class="field-label">
      <span>Ship name</span>
      <input v-model="shipName" type="text" placeholder="Aurora Wing" @keydown.enter.prevent="createShip('modern')" />
    </label>
    <div class="actions-compact">
      <button class="button-primary button-compact" type="button" @click="createShip('modern')">Modern</button>
      <button class="button-secondary button-compact" type="button" @click="createShip('classic')">Classic</button>
    </div>
  </section>
</template>
