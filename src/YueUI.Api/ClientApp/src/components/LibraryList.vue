<script setup lang="ts">
import { computed, ref } from 'vue'
import { audioUrl, render, runZipUrl, scoreUrl, songZipUrl } from '../api'
import { formatDateTime, formatDuration, t } from '../i18n'
import type { RunInfo, SongInfo } from '../types'

const props = defineProps<{
  runs: RunInfo[]
  loading: boolean
  error: string | null
  /** Songs the worker is working on; they cannot be rendered again meanwhile. */
  busyIds: Set<string>
}>()

const emit = defineEmits<{
  refresh: []
  template: [run: RunInfo]
  error: [message: string]
}>()

const pageSize = 8
const shown = ref(pageSize)
const visible = computed(() => props.runs.slice(0, shown.value))

async function renderFull(song: SongInfo): Promise<void> {
  try {
    await render(song.id, 'full')
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}
</script>

<template>
  <section class="library">
    <div class="heading">
      <h2>{{ t('library') }}</h2>
      <button type="button" class="link" :disabled="loading" @click="emit('refresh')">{{ t('refresh') }}</button>
    </div>

    <p v-if="error" class="danger">{{ t('libraryError', { message: error }) }}</p>
    <p v-else-if="!loading && runs.length === 0" class="muted">{{ t('libraryEmpty') }}</p>

    <article v-for="run in visible" :key="run.id" class="card run">
      <header>
        <div class="titles">
          <h3>{{ run.title || t('untitled') }}</h3>
          <span v-if="run.createdAt" class="muted date">{{ formatDateTime(run.createdAt) }}</span>
        </div>
        <button type="button" class="button secondary small" @click="emit('template', run)">{{ t('useAsTemplate') }}</button>
      </header>
      <p class="style muted">{{ run.style }}</p>
      <div class="extras">
        <details v-if="run.lyrics" class="lyrics">
          <summary>{{ t('showLyrics') }}</summary>
          <pre>{{ run.lyrics }}</pre>
        </details>
        <a v-if="run.songs.length > 1" class="link zip-run" :href="runZipUrl(run.id)">{{ t('zipRun') }}</a>
      </div>

      <ul class="songs">
        <li v-for="song in run.songs" :key="song.id" class="song">
          <div class="line">
            <strong>{{ t('songN', { n: song.index }) }}</strong>
            <span v-if="song.seconds" class="muted">{{ formatDuration(song.seconds) }}</span>
            <span v-if="song.quality" :class="['badge', song.quality]">
              {{ song.quality === 'draft' ? t('qualityDraft') : t('qualityFull') }}
            </span>
            <span v-if="song.seed !== null" class="muted seed">#{{ song.seed }}</span>
            <span class="links">
              <button
                v-if="song.canRender && song.quality !== 'full'"
                type="button"
                class="link"
                :disabled="busyIds.has(song.id)"
                @click="renderFull(song)"
              >
                {{ busyIds.has(song.id) ? t('rendering') : t('renderFull') }}
              </button>
              <a v-if="song.hasAudio" class="link" :href="audioUrl(song.id, true)">{{ t('download') }}</a>
              <a v-if="song.hasScore" class="link" :href="scoreUrl(song.id)">{{ t('score') }}</a>
              <a v-if="song.hasAudio || song.hasScore" class="link" :href="songZipUrl(song.id)" :title="t('zipTitle')">{{ t('zip') }}</a>
            </span>
          </div>
          <!-- preload="none": a page of five-minute FLACs would otherwise start loading on a phone. -->
          <audio v-if="song.hasAudio" controls preload="none" :src="audioUrl(song.id)" />
          <span v-else class="muted">{{ t('noAudio') }}</span>
        </li>
      </ul>
    </article>

    <button v-if="runs.length > shown" type="button" class="button secondary more" @click="shown += pageSize">
      {{ t('showMore') }}
    </button>
  </section>
</template>

<style scoped>
.library {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.heading {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
}

.heading h2 {
  margin: 0;
}

.run header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 0.75rem;
}

.titles {
  min-width: 0;
}

h3 {
  margin: 0;
  font-size: 1.05rem;
  overflow-wrap: anywhere;
}

.date {
  font-size: 0.85rem;
}

.style {
  display: -webkit-box;
  margin: 0.5rem 0 0;
  overflow: hidden;
  font-size: 0.9rem;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  line-clamp: 2;
}

.extras {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  gap: 0.4rem 1rem;
  margin-top: 0.4rem;
}

.lyrics {
  flex: 1 1 100%;
  font-size: 0.9rem;
}

/* Beside the closed "Lyrics" summary; an open one takes the whole row and the link moves below it. */
.lyrics:not([open]) {
  flex: 0 1 auto;
}

.zip-run {
  line-height: 1.5;
}

.lyrics summary {
  cursor: pointer;
  color: var(--accent);
}

.lyrics pre {
  max-height: 20rem;
  margin: 0.5rem 0 0;
  padding: 0.5rem 0.75rem;
  overflow: auto;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
  font-family: var(--font-mono);
  font-size: 0.8rem;
  white-space: pre-wrap;
}

.songs {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
  margin: 0.9rem 0 0;
  padding: 0.9rem 0 0;
  border-top: 1px solid var(--border);
  list-style: none;
}

.song {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0.5rem;
}

.seed {
  font-family: var(--font-mono);
  font-size: 0.8rem;
}

.links {
  display: flex;
  gap: 0.9rem;
  margin-left: auto;
}

.badge.full {
  background: var(--accent-soft);
  color: var(--accent);
}

audio {
  width: 100%;
  height: 2.5rem;
}

.more {
  align-self: center;
}
</style>
