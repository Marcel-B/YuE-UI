<script setup lang="ts">
import { computed, nextTick, ref, useTemplateRef, watch } from 'vue'
import { cancel, shutdownWorker, stopAll } from '../api'
import { formatTime, stageLabel, t } from '../i18n'
import SongTimeline from './SongTimeline.vue'
import type { LogEntry, SongState, WorkerInfo } from '../types'

defineExpose({ run, stopAll, shutdownWorker })

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

/** Finished songs fold to one line; their steps (and how long each took) open on request. */
const expanded = ref(new Set<string>())
function toggleSteps(id: string): void {
  const next = new Set(expanded.value)
  if (!next.delete(id)) {
    next.add(id)
  }
  expanded.value = next
}

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
  <section>
    <div class="flex justify-between">
      <Button :label="t('clearFinished')" v-if="hasFinished" class="underline" text @click="emit('hideFinished')" />
      <Button :label="t('stopAll')" v-if="worker.busy" class="danger" @click="run(stopAll)" />
      <Button
        v-else-if="worker.status !== 'stopped'"
        :label="t('shutdown')"
        :title="t('shutdownHint')"
        @click="run(shutdownWorker)"
      />
    </div>

    <p v-if="songs.length === 0" class="muted empty">{{ t('queueEmpty') }}</p>
    <ul v-else>
      <li v-for="song in songs" :key="song.id" :class="['song', song.stage]">
        <div class="flex gap-3 items-center">
          <strong class="name">{{ song.title || song.run }}</strong>
          <span class="muted">{{ t('songN', { n: song.index }) }}</span>
          <Button
            v-if="!song.finished"
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            @click="run(() => cancel(song.id))"
          />
        </div>
        <div v-if="song.finished" class="flex gap-3 items-center">
          <span class="text-sm font-medium">{{ stageLabel(song.stage) }}</span>
          <span class="muted detail">{{ song.message ?? song.detail }}</span>
          <Button
            v-if="song.stages?.length"
            :label="expanded.has(song.id) ? t('hideSteps') : t('showSteps')"
            text
            size="small"
            @click="toggleSteps(song.id)"
          />
        </div>
        <SongTimeline v-if="!song.finished || expanded.has(song.id)" :song="song" />
      </li>
    </ul>

    <details class="log" @toggle="logOpen = ($event.target as HTMLDetailsElement).open">
      <summary>{{ t('log') }}</summary>
      <div ref="logBox" class="text-sm">
        <p v-if="log.length === 0" class="muted">{{ t('logEmpty') }}</p>
        <div v-for="(entry, i) in log" :key="i" :class="['entry', entry.level]">
          <span class="muted">{{ formatTime(entry.time) }}</span> {{ entry.message }}
        </div>
      </div>
    </details>
  </section>
</template>

<style scoped></style>
