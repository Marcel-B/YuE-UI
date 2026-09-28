<script setup lang="ts">
import { computed } from 'vue'
import { t, type MessageKey } from '../i18n'
import { holderOf, modelOf, type Model, type VoiceWork } from '../queueModels'
import type { LyricsState, QueuedJob, SongState, SpeechTake, WorkerInfo } from '../types'

/**
 * One line over the queue: the models that take turns in the 24 GB, which one holds the memory now and how much waits
 * for each. The jobs' own wait reasons are in QueueList, under each job.
 */
const props = defineProps<{
  songs: SongState[]
  jobs: QueuedJob[]
  worker: WorkerInfo
  lyricsDraft: LyricsState | null
  /** Voice versions and stem separations. */
  versions: VoiceWork[]
  /** The speech lab's takes in the works. */
  takes: SpeechTake[]
}>()

const models: { model: Model; icon: string; label: MessageKey }[] = [
  { model: 'yue', icon: 'pi pi-sparkles', label: 'modelYue' },
  { model: 'lyrics', icon: 'pi pi-pen-to-square', label: 'modelLyrics' },
  { model: 'voice', icon: 'pi pi-user', label: 'modelVoice' },
  { model: 'speech', icon: 'pi pi-comments', label: 'modelSpeech' },
]

/** The speech lab is an experiment: its tile only takes room on the phone while it has takes in the works. */
const shown = computed(() => models.filter((m) => m.model !== 'speech' || props.takes.some((take) => !take.finished)))

const holder = computed(() => holderOf(props.worker, props.lyricsDraft, props.versions, props.takes))

function running(model: Model): number {
  switch (model) {
    case 'yue':
      return props.songs.filter((s) => !s.finished).length
    case 'lyrics':
      return props.lyricsDraft?.stage === 'writing' ? 1 : 0
    case 'voice':
      return props.versions.filter((v) => !v.finished && v.stage !== 'queued').length
    case 'speech':
      return props.takes.filter((take) => !take.finished && take.stage !== 'queued').length
  }
}

function waiting(model: Model): number {
  switch (model) {
    case 'voice':
      return props.versions.filter((v) => v.stage === 'queued').length
    case 'speech':
      return props.takes.filter((take) => take.stage === 'queued').length
    default:
      return props.jobs.filter((j) => modelOf(j) === model).length
  }
}

function state(model: Model): string {
  const parts: string[] = []
  const busy = running(model)
  if (holder.value === model) {
    parts.push(busy > 1 ? t('modelBusyN', { n: busy }) : t('modelBusy'))
  } else if (model === 'yue' && props.worker.status === 'ready') {
    // Loaded but idle: the next song starts without loading, a draft or version first shuts it down.
    parts.push(t('modelLoaded'))
  }
  const queued = waiting(model)
  if (queued > 0) {
    parts.push(t('modelWaiting', { n: queued }))
  }
  return parts.join(' · ') || t('modelFree')
}
</script>

<template>
  <!-- Four tiles in a row leave a phone only their first letters, so the speech lab's makes two rows there. -->
  <div
    :class="['gap-1 mb-3', shown.length > 3 ? 'grid grid-cols-2 sm:flex' : 'flex']"
    role="list"
    :aria-label="t('memory')"
    :title="t('memoryHint')"
  >
    <div
      v-for="m in shown"
      :key="m.model"
      role="listitem"
      :class="[
        'model flex-1 min-w-0 flex items-center gap-2 px-2 py-1 rounded-md',
        holder === m.model ? 'active' : waiting(m.model) > 0 ? 'queued' : 'idle',
      ]"
    >
      <i :class="[m.icon, 'text-sm']" />
      <div class="min-w-0 leading-tight">
        <div class="text-sm font-medium truncate">{{ t(m.label) }}</div>
        <div class="text-xs truncate state">{{ state(m.model) }}</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.model {
  border: 1px solid var(--p-content-border-color);
}
.model.idle {
  color: var(--p-text-muted-color);
}
.model.queued {
  border-style: dashed;
}
.model.active {
  border-color: var(--p-primary-color);
  background: color-mix(in srgb, var(--p-primary-color) 14%, transparent);
}
.model.active i {
  color: var(--p-primary-color);
  animation: pulse 1.6s ease-in-out infinite;
}
.state {
  color: var(--p-text-muted-color);
}
@keyframes pulse {
  50% {
    opacity: 0.35;
  }
}
@media (prefers-reduced-motion: reduce) {
  .model.active i {
    animation: none;
  }
}
</style>
