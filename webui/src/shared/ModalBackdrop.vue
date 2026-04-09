<script setup lang="ts">
const props = defineProps<{
  disableBackdropClose?: boolean;
  panelClass?: string;
}>();

const emit = defineEmits<{
  close: [];
}>();

function handleBackdropClick() {
  if (props.disableBackdropClose) {
    return;
  }

  emit('close');
}
</script>

<template>
  <div class="modal-backdrop" @click.self="handleBackdropClick">
    <section class="modal-panel panel-glass" :class="panelClass">
      <slot />
    </section>
  </div>
</template>