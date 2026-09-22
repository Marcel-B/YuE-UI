<script setup lang="ts">
import { computed, nextTick, ref, useTemplateRef, watch } from 'vue'
import { cancel, shutdownWorker, stopAll } from '../api'
import { formatTime, stageLabel, t } from '../i18n'
import type { LogEntry, SongState, WorkerInfo } from '../types'

const props = defineProps<{
  songs: SongState[]
  worker: WorkerInfo
  log: LogEntry[]
}>()

const emit = defineEmits<{
  hideFinished: []
  error: [message: string]
}>()

const hasFinished = computed(() => props.songs.some((s) => s.finished))

async function run(action: () => Promise<void>): Promise<void> {
  try {
    await action()
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

/** The log opens scrolled to its end, and stays there while new lines come in. */
const logOpen = ref(false)
const logBox = useTemplateRef<HTMLElement>('logBox')
watch(
  () => [props.log.length, logOpen.value],
  async () => {
    await nextTick()
    const box = logBox.value
    if (box && logOpen.value) {
      box.scrollTop = box.scrollHeight
    }
  },
)
</script>

<template>
  <section class="card">
    <div class="heading">
      <h2>{{ t('queue') }}</h2>
      <div class="tools">
        <button v-if="hasFinished" type="button" class="link" @click="emit('hideFinished')">{{ t('clearFinished') }}</button>
        <button v-if="worker.busy" type="button" class="button secondary small" @click="run(stopAll)">{{ t('stopAll') }}</button>
        <button
          v-else-if="worker.status !== 'stopped'"
          type="button"
          class="button secondary small"
          :title="t('shutdownHint')"
          @click="run(shutdownWorker)"
        >
          {{ t('shutdown') }}
        </button>
      </div>
    </div>

    <p v-if="songs.length === 0" class="muted empty">{{ t('queueEmpty') }}</p>
    <ul v-else class="songs">
      <li v-for="song in songs" :key="song.id" :class="['song', song.stage]">
        <div class="line">
          <strong class="name">{{ song.title || song.run }}</strong>
          <span class="muted">{{ t('songN', { n: song.index }) }}</span>
          <button v-if="!song.finished" type="button" class="link cancel" @click="run(() => cancel(song.id))">{{ t('cancel') }}</button>
        </div>
        <div class="line">
          <span class="stage">{{ stageLabel(song.stage) }}</span>
          <span v-if="song.engine && !song.finished" class="badge">{{ song.engine }}</span>
          <span class="muted detail">{{ song.message ?? song.detail }}</span>
        </div>
        <progress
          v-if="!song.finished"
          :value="song.stage === 'queued' ? undefined : song.fraction"
          max="1"
          :aria-label="stageLabel(song.stage)"
        />
      </li>
    </ul>

    <details class="log" @toggle="logOpen = ($event.target as HTMLDetailsElement).open">
      <summary>{{ t('log') }}</summary>
      <div ref="logBox" class="entries">
        <p v-if="log.length === 0" class="muted">{{ t('logEmpty') }}</p>
        <div v-for="(entry, i) in log" :key="i" :class="['entry', entry.level]">
          <span class="time">{{ formatTime(entry.time) }}</span> {{ entry.message }}
        </div>
      </div>
    </details>
  </section>
</template>

<style scoped>
.heading {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  margin-bottom: 0.75rem;
}

.heading h2 {
  margin: 0;
}

.tools {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.empty {
  margin: 0;
}

.songs {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.song {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0.5rem;
  min-width: 0;
}

.name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cancel {
  margin-left: auto;
}

.stage {
  font-size: 0.9rem;
  font-weight: 600;
}

.song.ready .stage {
  color: var(--success);
}

.song.failed .stage,
.song.cancelled .stage {
  color: var(--danger);
}

.detail {
  overflow: hidden;
  font-size: 0.85rem;
  text-overflow: ellipsis;
}

progress {
  width: 100%;
  height: 0.5rem;
  accent-color: var(--accent);
}

.log {
  margin-top: 1rem;
}

.log summary {
  cursor: pointer;
  font-weight: 600;
}

.entries {
  max-height: 16rem;
  margin-top: 0.5rem;
  padding: 0.5rem 0.75rem;
  overflow: auto;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
  font-family: var(--font-mono);
  font-size: 0.75rem;
  line-height: 1.45;
}

.entries p {
  margin: 0;
}

.entry {
  white-space: pre-wrap;
  word-break: break-word;
}

.entry.error {
  color: var(--danger);
}

.entry.stderr {
  color: var(--text-muted);
}

.time {
  color: var(--text-muted);
}
</style>
