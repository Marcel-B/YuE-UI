<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { cancel, cancelQueued, deleteStems, deleteVersion, moveQueued, shutdownWorker, stopAll } from '../api'
import { formatDuration, formatTime, stageLabel, t, versionProgress } from '../i18n'
import QueueOverview from './QueueOverview.vue'
import SongTimeline from './SongTimeline.vue'
import { holderOf, waitReason } from '../queueModels'
import { showSong, songHref } from '../view'
import type { LogEntry, LyricsState, QueuedJob, SongState, StemSetState, VersionState, WorkerInfo } from '../types'

defineExpose({ run, stopAll, shutdownWorker })

const props = defineProps<{
  songs: SongState[]
  /** Jobs the server holds back until the memory is free, in the order they start. */
  jobs: QueuedJob[]
  worker: WorkerInfo
  lyricsDraft: LyricsState | null
  versions: VersionState[]
  /** Songs being split into stems; they share the voices' queue and memory. */
  stems: StemSetState[]
  /** Queue:BundleWindow; null for a server from before it was sent. */
  bundleWindowSeconds: number | null
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
      return [
        job.batch && job.batch > 1 ? t('jobSongs', { n: job.batch }) : '',
        quality,
        job.voiceLabel ? t('jobVoice', { voice: job.voiceLabel }) : '',
      ]
        .filter(Boolean)
        .join(' · ')
    case 'render':
      return [t('jobRender'), quality].join(' · ')
    case 'lyrics':
      return t(job.revision ? 'jobRevision' : 'jobLyrics')
  }
}

/** Ticks for the bundling window's countdown in the wait reasons. */
const now = ref(Date.now())
const timer = setInterval(() => (now.value = Date.now()), 15_000)
onBeforeUnmount(() => clearInterval(timer))

const voiceWork = computed(() => [...props.versions, ...props.stems])
const holder = computed(() => holderOf(props.worker, props.lyricsDraft, voiceWork.value))

const reasons = computed(() =>
  props.jobs.map((_, i) =>
    waitReason(props.jobs, i, holder.value, voiceWork.value, props.bundleWindowSeconds, now.value),
  ),
)

/**
 * Voice versions and stem separations in the works, oldest first, as VoiceConverter makes them one at a time. They
 * are not in the server's job queue (they keep their own), but they wait for the same memory.
 */
interface VoiceItem {
  key: string
  songId: string
  title: string
  detail: string
  stage: string
  stageLabel: string
  createdAt: string
  icon: string
  remove: () => Promise<void>
}

const voiceItems = computed<VoiceItem[]>(() =>
  [
    ...props.versions
      .filter((v) => !v.finished)
      .map((v) => ({
        key: `version:${v.id}`,
        songId: v.songId,
        title: v.title,
        detail: t('queueVersion', { voice: v.voiceLabel }),
        stage: v.stage,
        stageLabel: [t(`versionStage_${v.stage}`), versionProgress(v)].filter(Boolean).join(' '),
        createdAt: v.createdAt,
        icon: 'pi pi-user',
        remove: () => deleteVersion(v.songId, v.id),
      })),
    ...props.stems
      .filter((s) => !s.finished)
      .map((s) => ({
        key: `stems:${s.id}`,
        songId: s.songId,
        title: s.title,
        detail: t('queueStems', { model: s.model }),
        stage: s.stage,
        stageLabel: t(`stemsStage_${s.stage}`),
        createdAt: s.createdAt,
        icon: 'pi pi-sliders-v',
        remove: () => deleteStems(s.songId, s.id),
      })),
  ].sort((a, b) => a.createdAt.localeCompare(b.createdAt)),
)

/** A waiting one waits for whoever holds the memory, or for the one before it in the voices' queue. */
function voiceReason(item: VoiceItem): string {
  if (item.stage !== 'queued') {
    return ''
  }
  if (holder.value === 'yue') {
    return t('waitYue')
  }
  if (holder.value === 'lyrics') {
    return t('waitLyrics')
  }
  return holder.value === 'voice' ? t('waitTurn') : t('waitStarting')
}

function songNumber(songId: string): string {
  return t('songN', { n: songId.slice(songId.lastIndexOf('/') + 5) })
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

    <QueueOverview :songs="songs" :jobs="jobs" :worker="worker" :lyrics-draft="lyricsDraft" :versions="voiceWork" />

    <div v-if="voiceItems.length > 0" class="mb-4">
      <h3 class="m-0 text-sm font-medium text-muted-color">{{ t('queueVoice') }}</h3>
      <ul class="m-0 p-0 list-none flex flex-col gap-1">
        <li v-for="item in voiceItems" :key="item.key" class="flex gap-2 items-center">
          <i :class="[item.icon, 'text-muted-color']" />
          <div class="min-w-0 flex-1">
            <a
              v-if="listed.has(item.songId)"
              :href="songHref(item.songId)"
              class="block truncate font-bold text-color no-underline hover:underline"
              :title="t('showSong')"
              @click.prevent="showSong(item.songId)"
              >{{ item.title || t('untitled') }} · {{ songNumber(item.songId) }}</a
            >
            <strong v-else class="block truncate"
              >{{ item.title || t('untitled') }} · {{ songNumber(item.songId) }}</strong
            >
            <span class="block text-sm text-muted-color truncate">{{ item.detail }}</span>
            <span v-if="voiceReason(item)" class="block text-xs text-muted-color truncate"
              ><i class="pi pi-clock text-xs" /> {{ voiceReason(item) }}</span
            >
          </div>
          <Tag :severity="item.stage === 'queued' ? 'secondary' : undefined" class="shrink-0">
            <i v-if="item.stage !== 'queued'" class="pi pi-spin pi-spinner text-xs" />
            {{ item.stageLabel }}
          </Tag>
          <Button
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            :aria-label="t('queueCancel')"
            @click="run(item.remove)"
          />
        </li>
      </ul>
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
            <span class="block text-sm text-muted-color truncate">{{ jobDetail(job) }}</span>
            <span class="block text-xs text-muted-color truncate"
              ><i class="pi pi-clock text-xs" /> {{ reasons[i] }}</span
            >
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

    <p v-if="songs.length === 0 && jobs.length === 0 && voiceItems.length === 0" class="muted empty">
      {{ t('queueEmpty') }}
    </p>
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
          <span
            v-if="song.voice"
            class="muted truncate text-sm"
            :title="t('jobVoice', { voice: song.voice.voiceLabel })"
            ><i class="pi pi-user text-xs" /> {{ song.voice.voiceLabel }}</span
          >
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
