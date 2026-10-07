<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { saveSongNote } from '../api'
import { t } from '../i18n'

/**
 * A song's note, written in place and saved by itself: a moment after typing stops, when the field loses focus, when
 * the page is hidden (a phone locks, the app goes to the background) and when the note is closed. There is no save
 * button to forget on the phone.
 */
const props = defineProps<{
  songId?: string
  note: string
  /** Where the note goes, for a note that is not a song's (an instrument's); without it, the song's note. */
  save?: (note: string) => Promise<void>
  placeholder?: string
}>()

const emit = defineEmits<{
  /** What was saved, trimmed as the server keeps it. */
  saved: [note: string]
  error: [message: string]
}>()

const draft = ref(props.note)
/** The last text the server has, so an unchanged field is not sent again; null after a failure, to send it again. */
let stored: string | null = props.note.trim()
const state = ref<'idle' | 'saving' | 'saved'>('idle')
let timer: ReturnType<typeof setTimeout> | undefined
/** Saves go one after another, so a slow earlier one cannot land after a later one and undo it. */
let queue = Promise.resolve()

const field = ref<{ $el: HTMLTextAreaElement } | null>(null)

function save(): Promise<void> {
  clearTimeout(timer)
  const text = draft.value.trim()
  if (text === stored) {
    return queue
  }
  stored = text
  state.value = 'saving'
  queue = queue.then(async () => {
    try {
      await (props.save ? props.save(text) : saveSongNote(props.songId!, text))
      emit('saved', text)
      if (draft.value.trim() === text) {
        state.value = 'saved'
      }
    } catch (caught) {
      stored = null
      state.value = 'idle'
      emit('error', t('songNoteFailed', { message: caught instanceof Error ? caught.message : String(caught) }))
    }
  })
  return queue
}

function typed(): void {
  state.value = 'idle'
  clearTimeout(timer)
  timer = setTimeout(() => void save(), 1000)
}

function hidden(): void {
  if (document.visibilityState === 'hidden') {
    void save()
  }
}

onMounted(() => {
  document.addEventListener('visibilitychange', hidden)
  // Opened to write in: the keyboard comes up at once, an existing note is only read.
  if (!props.note) {
    field.value?.$el.focus()
  }
})

onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', hidden)
  void save()
})
</script>

<template>
  <div class="note">
    <Textarea
      ref="field"
      v-model="draft"
      rows="2"
      auto-resize
      fluid
      :maxlength="10000"
      :placeholder="placeholder ?? t('songNotePlaceholder')"
      :aria-label="t('songNote')"
      @input="typed"
      @blur="save"
    />
    <span class="muted text-xs state" aria-live="polite">{{
      state === 'saving' ? t('songNoteSaving') : state === 'saved' ? t('songNoteSaved') : ' '
    }}</span>
  </div>
</template>

<style scoped>
.note {
  display: flex;
  flex-direction: column;
  gap: 0.125rem;
  padding: 0.25rem 0;
}

.state {
  align-self: flex-end;
}
</style>
