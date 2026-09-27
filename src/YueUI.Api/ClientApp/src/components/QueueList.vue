<script setup lang="ts">
import { computed, nextTick, ref, useTemplateRef, watch } from 'vue'
import { cancel, cancelQueued, moveQueued, shutdownWorker, stopAll } from '../api'
import { formatDuration, formatTime, stageLabel, t } from '../i18n'
import SongTimeline from './SongTimeline.vue'
import { showSong, songHref } from '../view'
import type { LogEntry, QueuedJob, SongState, WorkerInfo } from '../types'

defineExpose({ run, stopAll, shutdownWorker })

const props = defineProps<{
  songs: SongState[]
  /** Jobs the server holds back until the memory is free, in the order they start. */
  jobs: QueuedJob[]
  worker: WorkerInfo
  log: LogEntry[]
  /** Songs the library lists; those have a place on the songs page to jump to. */
  listed: Set<string>
}>()

const emit = defineEmits<{
  hideFinished: []
  error: [message: string]
}>()

const hasFinished = computed(() => props.songs.some((s) => s.finished))

const jobIcons: Record<QueuedJob['kind'], string> = {
  song: 'pi pi-sparkles',
  render: 'pi pi-refresh',
  lyrics: 'pi pi-pen-to-square',
}

function jobTitle(job: QueuedJob): string {
  if (job.title) {
    return job.title
  }
  return job.kind === 'render' ? (job.songId ?? '') : job.kind === 'lyrics' ? t('jobLyrics') : t('untitled')
}

function jobDetail(job: QueuedJob): string {
  const quality = job.quality ? t(job.quality === 'full' ? 'qualityFull' : 'qualityDraft') : ''
  switch (job.kind) {
    case 'song':
      return [job.batch && job.batch > 1 ? t('jobSongs', { n: job.batch }) : '', quality].filter(Boolean).join(' · ')
    case 'render':
      return [t('jobRender'), quality].join(' · ')
    case 'lyrics':
      return t(job.revision ? 'jobRevision' : 'jobLyrics')
  }
}

/** From joining the queue to the end, or null for a song without its stages (a server from before they were kept). */
function totalTime(song: SongState): string | null {
  const first = song.stages?.[0]
  const last = song.stages?.[song.stages.length - 1]
  if (!first || !last || first === last) {
    return null
  }
  return formatDuration((new Date(last.startedAt).getTime() - new Date(first.startedAt).getTime()) / 1000)
}

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

    <div v-if="jobs.length > 0" class="mb-4">
      <h3 class="m-0 text-sm font-medium text-muted-color" :title="t('queueWaitingHint')">{{ t('queueWaiting') }}</h3>
      <ul class="m-0 p-0 list-none flex flex-col gap-1">
        <li v-for="(job, i) in jobs" :key="job.id" class="flex gap-2 items-center">
          <i :class="[jobIcons[job.kind], 'text-muted-color']" />
          <div class="min-w-0 flex-1">
            <a
              v-if="job.songId && listed.has(job.songId)"
              :href="songHref(job.songId)"
              class="block truncate font-bold text-color no-underline hover:underline"
              :title="t('showSong')"
              @click.prevent="showSong(job.songId)"
              >{{ jobTitle(job) }}</a
            >
            <strong v-else class="block truncate">{{ jobTitle(job) }}</strong>
            <span class="text-sm text-muted-color">{{ jobDetail(job) }}</span>
          </div>
          <Button
            icon="pi pi-arrow-up"
            text
            rounded
            size="small"
            :disabled="i === 0"
            :aria-label="t('moveUp')"
            @click="run(() => moveQueued(job.id, -1))"
          />
          <Button
            icon="pi pi-arrow-down"
            text
            rounded
            size="small"
            :disabled="i === jobs.length - 1"
            :aria-label="t('moveDown')"
            @click="run(() => moveQueued(job.id, 1))"
          />
          <Button
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            :aria-label="t('removeFromQueue')"
            @click="run(() => cancelQueued(job.id))"
          />
        </li>
      </ul>
    </div>

    <p v-if="songs.length === 0 && jobs.length === 0" class="muted empty">{{ t('queueEmpty') }}</p>
    <ul v-else-if="songs.length > 0">
      <li v-for="song in songs" :key="song.id" :class="['song', song.stage]">
        <div class="flex gap-3 items-center">
          <a
            v-if="listed.has(song.id)"
            :href="songHref(song.id)"
            class="name font-bold text-color no-underline hover:underline"
            :title="t('showSong')"
            @click.prevent="showSong(song.id)"
            >{{ song.title || song.run }}</a
          >
          <strong v-else class="name">{{ song.title || song.run }}</strong>
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
        <div v-if="song.finished" class="flex flex-wrap gap-x-3 items-center">
          <span class="text-sm font-medium">{{ stageLabel(song.stage) }}</span>
          <span v-if="totalTime(song)" class="text-sm text-muted-color whitespace-nowrap">{{
            t('stageTotal', { time: totalTime(song)! })
          }}</span>
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
      <!-- Only while open: the worker's stderr can bring several lines a second, and each one shifts all the lines. -->
      <div v-if="logOpen" ref="logBox" class="text-sm">
        <p v-if="log.length === 0" class="muted">{{ t('logEmpty') }}</p>
        <div v-for="(entry, i) in log" :key="i" :class="['entry', entry.level]">
          <span class="muted">{{ formatTime(entry.time) }}</span> {{ entry.message }}
        </div>
      </div>
    </details>
  </section>
</template>

<style scoped></style>
