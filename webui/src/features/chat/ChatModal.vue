<script setup lang="ts">
import { ref } from 'vue';
import { formatActivityTime } from '../../lib/formatting';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';
import { useGateway } from '../../composables/useGateway';
import ModalBackdrop from '../../shared/ModalBackdrop.vue';

const gameStore = useGameStore();
const uiStore = useUiStore();
const gateway = useGateway();

const draft = ref('');

function close() {
  uiStore.isChatPopupOpen = false;
}

function send() {
  gateway.sendChat(draft.value);
  draft.value = '';
}
</script>

<template>
  <ModalBackdrop panel-class="chat-modal-panel" @close="close">
    <div class="modal-header">
      <h2 class="panel-title">Galaxy Chat</h2>
      <button class="button-ghost" type="button" @click="close">Close</button>
    </div>

    <div class="chat-modal-list panel-scroll">
      <article v-for="entry in gameStore.chatEntries" :key="entry.messageId" class="message-row">
        <header>
          <strong>{{ entry.senderDisplayName }}</strong>
          <span>{{ formatActivityTime(Date.parse(entry.sentAtUtc)) }}</span>
        </header>
        <p>{{ entry.message }}</p>
      </article>
    </div>

    <form class="chat-compose" @submit.prevent="send">
      <input v-model="draft" type="text" placeholder="Message" />
      <button class="button-primary" type="submit">Send</button>
    </form>
  </ModalBackdrop>
</template>