<script setup lang="ts">
import { ref, watch } from 'vue';
import type { ShipCreateRequest } from '../../types/client';
import { useSessionStore } from '../../stores/session';

const session = useSessionStore();

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
  const prefix = session.selectedPlayerSession?.displayName ?? 'Ship';
  return `${prefix} ${Math.floor(100000 + Math.random() * 900000)}`;
}

watch(
  () => session.selectedPlayerSession?.displayName,
  (displayName) => {
    if (!displayName) return;
    const suffix = shipName.value.match(/\d{6}$/)?.[0] ?? Math.floor(100000 + Math.random() * 900000).toString();
    shipName.value = `${displayName} ${suffix}`;
  }
);
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
