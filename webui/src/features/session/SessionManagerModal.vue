<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { maskApiKey } from '../../lib/savedConnections';
import { useSessionStore } from '../../stores/session';
import { useUiStore } from '../../stores/ui';
import ModalBackdrop from '../../shared/ModalBackdrop.vue';

const gateway = useGateway();
const sessionStore = useSessionStore();
const uiStore = useUiStore();

const apiKeyDraft = ref('');
const teamNameDraft = ref('');
const rememberKey = ref(true);
const selectedPlayerSessionId = ref('');
const savedConnections = computed(() => gateway.savedConnections.value);

watch(
  () => sessionStore.attachedPlayerSessions,
  (nextSessions) => {
    if (nextSessions.length === 0) {
      selectedPlayerSessionId.value = '';
      return;
    }

    if (nextSessions.some((player) => player.playerSessionId === selectedPlayerSessionId.value)) {
      return;
    }

    selectedPlayerSessionId.value = nextSessions.find((player) => player.selected)?.playerSessionId ?? nextSessions[0]?.playerSessionId ?? '';
  },
  { immediate: true, deep: true },
);

const canSelectPlayer = computed(() => !!selectedPlayerSessionId.value);

function close() {
  uiStore.isManagerPopupOpen = false;
}

function submitConnection() {
  gateway.addConnection(apiKeyDraft.value, teamNameDraft.value, rememberKey.value);
  apiKeyDraft.value = '';
}
</script>

<template>
  <ModalBackdrop panel-class="modal-panel-wide" @close="close">
    <div class="modal-header">
      <h2 class="panel-title">Sessions</h2>
      <button class="button-ghost" type="button" @click="close">Close</button>
    </div>

    <div class="modal-grid">
      <section class="modal-column stack">
        <div class="panel stack">
          <h3 class="panel-title">Attached Sessions</h3>
          <label class="field-label">
            <span>Active player</span>
            <select v-model="selectedPlayerSessionId">
              <option v-for="player in sessionStore.attachedPlayerSessions" :key="player.playerSessionId" :value="player.playerSessionId">
                {{ player.displayName }}<template v-if="player.teamName"> · {{ player.teamName }}</template>
              </option>
            </select>
          </label>
          <div class="actions-tight">
            <button class="button-primary" type="button" :disabled="!canSelectPlayer" @click="gateway.selectPlayerSession(selectedPlayerSessionId)">Select</button>
          </div>
          <div class="stack panel-scroll compact-list">
            <div v-for="player in sessionStore.attachedPlayerSessions" :key="player.playerSessionId" class="list-row">
              <div>
                <strong>{{ player.displayName }}</strong>
                <p class="panel-copy">{{ player.teamName ?? 'No team' }} · {{ player.connected ? 'connected' : 'detached' }}</p>
              </div>
              <button class="button-secondary" type="button" @click="gateway.detachPlayerSession(player.playerSessionId)">Detach</button>
            </div>
          </div>
        </div>

        <div class="panel stack">
          <h3 class="panel-title">Saved Keys</h3>
          <div class="stack panel-scroll compact-list">
            <div v-for="savedConnection in savedConnections" :key="savedConnection.id" class="list-row">
              <div>
                <strong>{{ savedConnection.label }}</strong>
                <p class="panel-copy">{{ maskApiKey(savedConnection.apiKey) }}<template v-if="savedConnection.teamName"> · {{ savedConnection.teamName }}</template></p>
              </div>
              <div class="actions-tight">
                <button class="button-secondary" type="button" @click="gateway.attachSavedConnection(savedConnection)">Attach</button>
                <button class="button-danger" type="button" @click="gateway.forgetSavedConnection(savedConnection.id)">Forget</button>
              </div>
            </div>
          </div>
        </div>
      </section>

      <section class="modal-column stack">
        <div class="panel stack">
          <h3 class="panel-title">Attach Player Key</h3>
          <label class="field-label">
            <span>API key</span>
            <input v-model="apiKeyDraft" type="password" placeholder="64-character player key" />
          </label>
          <label class="field-label">
            <span>Team name</span>
            <input v-model="teamNameDraft" type="text" placeholder="Optional team name" />
          </label>
          <label class="checkbox-row">
            <input v-model="rememberKey" type="checkbox" />
            <span>Remember this key on this device</span>
          </label>
          <div class="actions-tight">
            <button class="button-primary" type="button" @click="submitConnection">Attach Key</button>
          </div>
        </div>

        <div class="panel stack">
          <h3 class="panel-title">Connection</h3>
          <dl class="overlay-stat-grid">
            <div>
              <dt>Gateway</dt>
              <dd>{{ sessionStore.gatewayUrl }}</dd>
            </div>
            <div>
              <dt>Status</dt>
              <dd>{{ sessionStore.connectionStateLabel }}</dd>
            </div>
            <div>
              <dt>Pending</dt>
              <dd>{{ gateway.pendingAttachmentsCount }}</dd>
            </div>
          </dl>
          <div class="actions-tight">
            <button v-if="sessionStore.showDisconnectAction" class="button-secondary" type="button" @click="gateway.disconnect()">Disconnect</button>
            <button v-else class="button-primary" type="button" @click="gateway.connect()">Connect</button>
          </div>
        </div>
      </section>
    </div>
  </ModalBackdrop>
</template>