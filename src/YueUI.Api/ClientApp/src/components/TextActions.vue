<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { t } from '../i18n'
import { copyText } from '../clipboard'

/**
 * Copy and clear for a text field. Clearing asks nothing, since a confirmation is one more tap on the phone
 * for every clear; instead the cleared text is kept and the clear button offers it back until the field
 * is typed into again.
 */
const text = defineModel<string>({ required: true })
withDefaults(defineProps<{ clearable?: boolean }>(), { clearable: true })

const cleared = ref<string | null>(null)
const canRestore = computed(() => cleared.value !== null && text.value === '')
const copied = ref<boolean | null>(null)
let copiedTimer: ReturnType<typeof setTimeout> | undefined

async function copy(): Promise<void> {
  copied.value = await copyText(text.value)
  clearTimeout(copiedTimer)
  copiedTimer = setTimeout(() => (copied.value = null), 2000)
}

function clear(): void {
  cleared.value = text.value
  text.value = ''
}

function restore(): void {
  if (cleared.value !== null) text.value = cleared.value
  cleared.value = null
}

onBeforeUnmount(() => clearTimeout(copiedTimer))
</script>

<template>
  <div v-if="text !== '' || canRestore" class="flex items-center justify-end gap-1">
    <small v-if="copied !== null" :class="copied ? 'muted' : 'danger'" role="status">
      {{ copied ? t('textCopied') : t('textCopyFailed') }}
    </small>
    <Button
      v-if="text !== ''"
      type="button"
      :icon="copied ? 'pi pi-check' : 'pi pi-copy'"
      size="small"
      text
      rounded
      :aria-label="t('textCopy')"
      v-tooltip.bottom="t('textCopy')"
      @click="copy"
    />
    <template v-if="clearable">
      <Button
        v-if="canRestore"
        type="button"
        icon="pi pi-undo"
        :label="t('textRestore')"
        size="small"
        text
        @click="restore"
      />
      <Button
        v-else
        type="button"
        icon="pi pi-eraser"
        size="small"
        text
        rounded
        severity="secondary"
        :aria-label="t('textClear')"
        v-tooltip.bottom="t('textClear')"
        @click="clear"
      />
    </template>
  </div>
</template>
