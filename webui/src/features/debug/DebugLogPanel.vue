<script setup lang="ts">
import { computed } from 'vue';
import { downloadTextFile, formatDebugDisplayPayload, formatDebugSnapshotFileName, formatDebugTime } from '../../lib/formatting';
import { useUiStore } from '../../stores/ui';
import ModalBackdrop from '../../shared/ModalBackdrop.vue';

const uiStore = useUiStore();

const debugLogLimitModel = computed({
  get: () => uiStore.debugLogLimit,
  set: (value: number) => uiStore.setDebugLogLimit(value),
});

function close() {
  uiStore.setDebugLogOpen(false);
}

function handleBackdropClose() {
  if (uiStore.isDebugLogIngame) {
    return;
  }

  close();
}

function downloadSnapshot() {
  if (uiStore.filteredDebugLogEntries.length === 0) {
    return;
  }

  const createdAt = Date.now();
  const fileName = `gateway-debug-log-${formatDebugSnapshotFileName(createdAt)}.txt`;
  const filters = [
    `limit=${uiStore.normalizedDebugLogLimit}`,
    `search=${uiStore.debugLogSearch || '(none)'}`,
    `exclude=${uiStore.debugLogExclude || '(none)'}`,
    `client=${uiStore.showClientDebugMessages}`,
    `server=${uiStore.showServerDebugMessages}`,
  ].join('\n');
  const content = [
    'Gateway Debug Log Snapshot',
    `created=${new Date(createdAt).toISOString()}`,
    filters,
    '',
    ...uiStore.filteredDebugLogEntries.map((entry) => [
      `[${new Date(entry.createdAt).toISOString()}] ${entry.direction.toUpperCase()} ${entry.messageType}`,
      entry.payload,
      '',
    ].join('\n')),
  ].join('\n');

  downloadTextFile(fileName, content);
}
</script>

<template>
  <ModalBackdrop :disable-backdrop-close="uiStore.isDebugLogIngame" panel-class="debug-log-panel" @close="handleBackdropClose">
    <div class="modal-header">
      <h2 class="panel-title">Debug Log</h2>
      <div class="actions-tight">
        <label class="checkbox-row">
          <input :checked="uiStore.isDebugLogIngame" type="checkbox" @change="uiStore.setDebugLogIngame(($event.target as HTMLInputElement).checked)" />
          <span>Ingame</span>
        </label>
        <button class="button-ghost" type="button" @click="close">Close</button>
      </div>
    </div>

    <div class="debug-log-toolbar stack">
      <div class="actions-tight">
        <label class="field-label debug-field">
          <span>Limit</span>
          <input v-model.number="debugLogLimitModel" type="number" min="1" step="1" />
        </label>
        <label class="field-label debug-field">
          <span>Search</span>
          <input v-model="uiStore.debugLogSearch" type="text" placeholder="include message text" />
        </label>
        <label class="field-label debug-field">
          <span>Exclude</span>
          <input v-model="uiStore.debugLogExclude" type="text" placeholder="exclude message text" />
        </label>
      </div>

      <div class="actions-tight">
        <label class="checkbox-row">
          <input v-model="uiStore.showClientDebugMessages" type="checkbox" />
          <span>Client</span>
        </label>
        <label class="checkbox-row">
          <input v-model="uiStore.showServerDebugMessages" type="checkbox" />
          <span>Server</span>
        </label>
        <button class="button-secondary" type="button" @click="downloadSnapshot">Download Snapshot</button>
        <button class="button-danger" type="button" @click="uiStore.clearDebugLog()">Clear</button>
      </div>
    </div>

    <div class="debug-log-list panel-scroll">
      <article v-for="entry in uiStore.filteredDebugLogEntries" :key="entry.id" class="debug-log-entry" :class="`is-${entry.direction}`">
        <header>
          <strong>{{ entry.messageType }}</strong>
          <span>{{ entry.direction }} · {{ formatDebugTime(entry.createdAt) }}</span>
        </header>
        <pre>{{ formatDebugDisplayPayload(entry.payload) }}</pre>
      </article>
    </div>
  </ModalBackdrop>
</template>