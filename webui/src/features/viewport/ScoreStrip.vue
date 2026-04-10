<script setup lang="ts">
import { computed } from 'vue';
import { useGameStore } from '../../stores/game';

const gameStore = useGameStore();

const teamScores = computed(() => gameStore.teamScores);
</script>

<template>
  <header v-if="teamScores.length > 0" class="score-strip panel-glass" aria-label="Team scores">
    <div v-for="team in teamScores" :key="team.id" class="score-strip-team">
      <span class="score-strip-swatch" :style="{ '--team-color': team.colorHex || '#808080' }" aria-hidden="true"></span>
      <div class="score-strip-copy">
        <strong>{{ team.name }}</strong>
      </div>
      <span class="score-strip-value">{{ team.score }}</span>
    </div>
  </header>
</template>
