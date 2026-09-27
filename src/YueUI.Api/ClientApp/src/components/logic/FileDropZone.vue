<script setup lang="ts">
import { computed, ref } from 'vue'
import { t } from '../../logic/i18n'

const props = defineProps<{
  file: File | null
  /** Expected file extension, e.g. ".abc", or several; other files are accepted but flagged. */
  extension: string | string[]
  accept: string
  dropHint: string
  wrongTypeHint: string
}>()
const emit = defineEmits<{ select: [file: File]; clear: [] }>()

const input = ref<HTMLInputElement | null>(null)
const dragDepth = ref(0)
const dragging = computed(() => dragDepth.value > 0)
const expectedType = computed(() => {
  const name = props.file?.name.toLowerCase()
  return !name || [props.extension].flat().some((extension) => name.endsWith(extension))
})

function openDialog(): void {
  input.value?.click()
}

function onInput(event: Event): void {
  const target = event.target as HTMLInputElement
  const file = target.files?.[0]
  if (file) {
    emit('select', file)
  }
  // Allows choosing the same file again after editing it on disk.
  target.value = ''
}

function onDrop(event: DragEvent): void {
  dragDepth.value = 0
  const file = event.dataTransfer?.files[0]
  if (file) {
    emit('select', file)
  }
}

function formatSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }
  return bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
</script>

<template>
  <div
    class="drop-zone"
    :class="{ dragging, filled: file }"
    role="button"
    tabindex="0"
    :aria-label="t('chooseFile')"
    @click="openDialog"
    @keydown.enter.self.prevent="openDialog"
    @keydown.space.self.prevent="openDialog"
    @dragenter.prevent="dragDepth++"
    @dragover.prevent
    @dragleave.prevent="dragDepth = Math.max(0, dragDepth - 1)"
    @drop.prevent="onDrop"
  >
    <input ref="input" type="file" :accept="accept" hidden @change="onInput" />

    <template v-if="dragging">
      <p class="headline">{{ t('dropWhileDragging') }}</p>
    </template>
    <template v-else-if="file">
      <p class="headline file-name">{{ file.name }}</p>
      <p class="muted">{{ formatSize(file.size) }}</p>
      <span class="flex gap-2">
        <Button :label="t('otherFile')" link size="small" @click.stop="openDialog" />
        <Button :label="t('removeFile')" link size="small" severity="danger" @click.stop="emit('clear')" />
      </span>
    </template>
    <template v-else>
      <i class="pi pi-upload icon" aria-hidden="true" />
      <p class="headline">{{ dropHint }}</p>
      <p class="muted">{{ t('dropOr') }}</p>
      <Button
        :label="t('chooseFile')"
        icon="pi pi-file"
        severity="secondary"
        outlined
        size="small"
        class="mt-1"
        @click.stop="openDialog"
      />
    </template>
  </div>
  <p v-if="!expectedType" class="hint warning">{{ wrongTypeHint }}</p>
</template>

<style scoped>
.drop-zone {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.35rem;
  padding: 1.5rem 1rem;
  border: 2px dashed var(--border-strong);
  border-radius: var(--radius);
  background: var(--surface-sunken);
  text-align: center;
  cursor: pointer;
  transition:
    border-color 0.15s,
    background 0.15s;
}

.drop-zone:hover,
.drop-zone:focus-visible {
  border-color: var(--accent);
  outline: none;
}

.drop-zone.dragging {
  border-color: var(--accent);
  background: var(--accent-soft);
}

.drop-zone.filled {
  border-style: solid;
  padding: 1.25rem 1rem;
}

.headline {
  margin: 0;
  font-weight: 600;
}

.file-name {
  overflow-wrap: anywhere;
}

.muted {
  margin: 0;
}

.icon {
  color: var(--accent);
  font-size: 2rem;
}
</style>
