<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref } from 'vue'
import { Game } from '../../game/game'

const host = ref<HTMLElement | null>(null)
let game: Game | null = null

onMounted(() => {
  if (!host.value) {
    return
  }

  game = new Game(host.value)
  game.mount()
})

onBeforeUnmount(() => {
  game?.destroy()
  game = null
})
</script>

<template>
  <section ref="host" class="game-canvas" aria-label="Game canvas layer" />
</template>
