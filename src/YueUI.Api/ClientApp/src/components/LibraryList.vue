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
const severityByQuality: Record<string, string> = {
  draft: 'warning',
  full: 'success',
}
</script>

<template>
  <section>
    <p v-if="error" class="danger">{{ t('libraryError', { message: error }) }}</p>
    <p v-else-if="!loading && runs.length === 0" class="muted">{{ t('libraryEmpty') }}</p>
    <DataView :value="visible">
      <template #list="slotProps">
        <div v-for="(run, index) in slotProps.items" :key="index">
          <Fieldset :legend="run.title || t('untitled')">
            <div class="flex justify-between items-center">
              <div>
                <span v-if="run.createdAt" class="muted text-sm">{{ formatDateTime(run.createdAt) }}</span>
              </div>
              <Button
                icon="pi pi-upload"
                v-tooltip="t('useAsTemplate')"
                text
                class="secondary"
                @click="emit('template', run)"
              />
            </div>
            <p class="style muted">{{ run.style }}</p>
            <div class="flex top-0">
              <div class="extras flex-1">
                <details v-if="run.lyrics" class="lyrics">
                  <summary>{{ t('showLyrics') }}</summary>
                  <pre>{{ run.lyrics }}</pre>
                </details>
              </div>
              <Button
                as="a"
                text
                v-if="run.songs.length > 1"
                v-tooltip="t('zipRun')"
                icon="pi pi-box"
                :href="runZipUrl(run.id)"
              />
            </div>

            <ul class="songs">
              <li v-for="song in run.songs" :key="song.id" class="song">
                <div class="flex gap-3 items-center">
                  <div><strong>{{ t('songN', { n: song.index }) }}</strong></div>
                  <div v-if="song.seconds" class="muted">{{ formatDuration(song.seconds) }}</div>
                  <Tag v-if="song.quality" :severity="severityByQuality[song.quality]">
                    {{ song.quality === 'draft' ? t('qualityDraft') : t('qualityFull') }}
                  </Tag>
                  <div v-if="song.seed !== null" class="muted seed">#{{ song.seed }}</div>
       
                </div>
                <div class="flex justify-between">

                    <Button
                      v-if="song.canRender && song.quality !== 'full'"
                      :label="busyIds.has(song.id) ? t('rendering') : t('renderFull')"
                      :loading="busyIds.has(song.id) ? true : false"
                      text
                      size="small"
                      :disabled="busyIds.has(song.id)"
                      @click="renderFull(song)"
                    />
                    <div class="flex justify-end">

                    <Button as="a" text v-if="song.hasAudio" size="small" :href="audioUrl(song.id, true)">{{
                      t('download')
                    }}</Button>
                    <Button as="a" text v-if="song.hasScore" size="small" :href="scoreUrl(song.id)">{{
                      t('score')
                    }}</Button>
                    <Button
                      v-if="song.hasAudio || song.hasScore"
                      as="a"
                      text
                      size="small"
                      rounded
                      icon="pi pi-box"
                      v-tooltip="t('zipTitle')"
                      :href="songZipUrl(song.id)"
                      :title="t('zipTitle')"
                    />
                </div>
                  </div>
                <!-- preload="none": a page of five-minute FLACs would otherwise start loading on a phone. -->
                <audio v-if="song.hasAudio" controls preload="none" :src="audioUrl(song.id)" />
                <span v-else class="muted">{{ t('noAudio') }}</span>
              </li>
            </ul>
          </Fieldset>
        </div>
      </template>
      <template #footer>
        <Button v-if="runs.length > shown" @click="shown += pageSize">
          {{ t('showMore') }}
        </Button>
      </template>
    </DataView>
  </section>
</template>

<style scoped>
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
