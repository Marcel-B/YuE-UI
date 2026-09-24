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
    <Button
      v-if="more"
      type="button"
      class="underline"
      size="small"
      text
      :aria-expanded="open"
      :aria-controls="`${id}-more`"
      @click="open = !open"
      :label="t('moreInfo')"
    />
  </small>
  <div v-if="more && open" :id="`${id}-more`" class="text-xs">{{ more }}</div>
</template>

<style scoped></style>
