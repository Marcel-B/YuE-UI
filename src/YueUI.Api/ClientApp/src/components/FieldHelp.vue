<script setup lang="ts">
import { ref } from 'vue'
import { t } from '../i18n'

/** A field's documentation: a one-line hint (the field's aria-describedby target) and, on demand, the longer text with examples. */
defineProps<{ id: string; hint: string; more?: string }>()

const open = ref(false)
</script>

<template>
  <small :id="id" class="muted help">
    {{ hint }}
    <button
      v-if="more"
      type="button"
      class="link"
      :aria-expanded="open"
      :aria-controls="`${id}-more`"
      @click="open = !open"
    >
      {{ t('moreInfo') }}
    </button>
  </small>
  <div v-if="more && open" :id="`${id}-more`" class="more">{{ more }}</div>
</template>

<style scoped>
.help .link {
  margin-left: 0.2rem;
  font-size: inherit;
}

/* The texts carry their examples and lists as line breaks. */
.more {
  padding: 0.6rem 0.75rem;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
  font-size: 0.85rem;
  white-space: pre-line;
  overflow-wrap: anywhere;
}
</style>
