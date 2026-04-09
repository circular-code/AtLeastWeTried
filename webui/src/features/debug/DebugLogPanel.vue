<script setup lang="ts">
import { computed } from 'vue';
import { downloadTextFile, formatDebugDisplayPayload, formatDebugSnapshotFileName, formatDebugTime } from '../../lib/formatting';
import { useUiStore } from '../../stores/ui';

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
  <div :class="['modal-backdrop', 'debug-log-backdrop', { 'is-ingame': uiStore.isDebugLogIngame }]" @click.self="handleBackdropClose">
    <section :class="['modal-panel', 'panel', 'panel-glass', 'debug-log-panel', { 'is-ingame': uiStore.isDebugLogIngame }]" aria-label="Gateway debug log">
      <div class="modal-header">
        <div>
          <p class="eyebrow">Transport Trace</p>
          <h1>Gateway Debug Log</h1>
        </div>
        <div class="actions actions-tight">
          <label :class="['secondary', 'button-compact', 'debug-log-toggle', 'is-ingame', { 'is-active': uiStore.isDebugLogIngame }]">
            <input :checked="uiStore.isDebugLogIngame" type="checkbox" @change="uiStore.setDebugLogIngame(($event.target as HTMLInputElement).checked)" />
            <span>Ingame</span>
          </label>
          <button class="secondary" type="button" :disabled="uiStore.filteredDebugLogEntries.length === 0" @click="downloadSnapshot">Download Snapshot</button>
          <button class="secondary" type="button" :disabled="uiStore.debugLogEntries.length === 0" @click="uiStore.clearDebugLog()">Clear</button>
          <button class="secondary" type="button" @click="close">Close</button>
        </div>
      </div>

      <div class="debug-log-toolbar">
        <label class="field debug-log-limit">
          <span>Limit</span>
          <input v-model.number="debugLogLimitModel" type="number" min="1" step="1" />
        </label>
        <label class="field debug-log-search">
          <span>Search</span>
          <input :value="uiStore.debugLogSearch" type="text" placeholder="Filter by type or message content" @input="uiStore.setDebugLogSearch(($event.target as HTMLInputElement).value)" />
        </label>
        <label class="field debug-log-search debug-log-exclude">
          <span>Exclude</span>
          <input :value="uiStore.debugLogExclude" type="text" placeholder="Hide matches like delta or ping" @input="uiStore.setDebugLogExclude(($event.target as HTMLInputElement).value)" />
        </label>
        <div class="debug-log-filters">
          <label :class="['debug-log-toggle', 'is-client', { 'is-active': uiStore.showClientDebugMessages }]">
            <input :checked="uiStore.showClientDebugMessages" type="checkbox" @change="uiStore.setShowClientDebugMessages(($event.target as HTMLInputElement).checked)" />
            <span>Client</span>
          </label>
          <label :class="['debug-log-toggle', 'is-server', { 'is-active': uiStore.showServerDebugMessages }]">
            <input :checked="uiStore.showServerDebugMessages" type="checkbox" @change="uiStore.setShowServerDebugMessages(($event.target as HTMLInputElement).checked)" />
            <span>Server</span>
          </label>
        </div>
      </div>

      <p v-if="uiStore.isDebugLogAtLimit" class="debug-log-notice">
        Debug log is full at {{ uiStore.normalizedDebugLogLimit }} messages. Clear it or raise the limit to capture more traffic.
      </p>
      <p v-if="uiStore.activeDebugCaptureSearch" class="debug-log-notice">
        Capture filter active. Only new messages matching "{{ uiStore.activeDebugCaptureSearch }}" will be added until you clear again with a different search.
      </p>

      <ul class="debug-log-list">
        <li v-if="uiStore.debugLogEntries.length === 0" class="debug-log-empty text-muted">No gateway traffic captured yet.</li>
        <li v-else-if="uiStore.filteredDebugLogEntries.length === 0" class="debug-log-empty text-muted">No messages match the current filters.</li>
        <li v-for="entry in uiStore.filteredDebugLogEntries" :key="entry.id" :class="['debug-log-entry', `is-${entry.direction}`]">
          <div class="debug-log-head">
            <div class="debug-log-meta">
              <span :class="['debug-log-direction', `is-${entry.direction}`]">{{ entry.direction }}</span>
              <strong>{{ entry.messageType }}</strong>
            </div>
            <span>{{ formatDebugTime(entry.createdAt) }}</span>
          </div>
          <pre>{{ formatDebugDisplayPayload(entry.payload) }}</pre>
        </li>
      </ul>
    </section>
  </div>
</template>
