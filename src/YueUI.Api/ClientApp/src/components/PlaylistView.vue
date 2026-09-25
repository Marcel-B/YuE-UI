<script setup lang="ts">
import { computed } from 'vue'
import { formatDuration, t } from '../i18n'
import { current, play, playing, trackOf, type Track } from '../player'
import { playlistIds, savePlaylist } from '../playlist'
import type { RunInfo, SongInfo } from '../types'

const props = defineProps<{ runs: RunInfo[] }>()

const emit = defineEmits<{ error: [message: string] }>()

interface Entry {
  song: SongInfo
  track: Track
}

/** The playlist's songs as the library knows them; one deleted meanwhile is left out until the next reload. */
const entries = computed<Entry[]>(() => {
  const songs = new Map(props.runs.flatMap((run) => run.songs.map((song) => [song.id, { run, song }] as const)))
  return playlistIds.value.flatMap((id) => {
    const found = songs.get(id)
    return found ? [{ song: found.song, track: trackOf(found.run, found.song) }] : []
  })
})

const playable = computed(() => entries.value.filter((e) => e.song.hasAudio).map((e) => e.track))
const duration = computed(() => formatDuration(entries.value.reduce((sum, e) => sum + (e.song.seconds ?? 0), 0)))

function playEntry(entry: Entry): void {
  play(entry.track, playable.value)
}

async function save(songIds: string[]): Promise<void> {
  try {
    await savePlaylist(songIds)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

function move(index: number, by: number): void {
  const ids = entries.value.map((e) => e.song.id)
  const [moved] = ids.splice(index, 1)
  ids.splice(index + by, 0, moved!)
  void save(ids)
}

function remove(entry: Entry): void {
  void save(playlistIds.value.filter((id) => id !== entry.song.id))
}
</script>

<template>
  <section>
    <p v-if="entries.length === 0" class="muted">{{ t('playlistEmpty') }}</p>
    <template v-else>
      <div class="flex items-center justify-between gap-2 mb-3">
        <span class="muted text-sm">{{ t('playlistSummary', { count: entries.length, duration }) }}</span>
        <Button
          icon="pi pi-play"
          :label="t('playAll')"
          size="small"
          :disabled="playable.length === 0"
          @click="play(playable[0]!, playable)"
        />
      </div>
      <ol class="entries">
        <li
          v-for="(entry, index) in entries"
          :key="entry.song.id"
          :class="['entry', { 'bg-emphasis': current?.id === entry.song.id }]"
        >
          <Button
            :icon="current?.id === entry.song.id && playing ? 'pi pi-pause' : 'pi pi-play'"
            rounded
            text
            :disabled="!entry.song.hasAudio"
            :aria-label="current?.id === entry.song.id && playing ? t('pause') : t('play')"
            @click="playEntry(entry)"
          />
          <div class="min-w-0 flex-1">
            <div class="truncate font-semibold">{{ entry.track.title }}</div>
            <div class="muted text-sm">
              {{ entry.track.detail
              }}<template v-if="entry.song.seconds"> · {{ formatDuration(entry.song.seconds) }}</template>
            </div>
          </div>
          <Button
            icon="pi pi-arrow-up"
            text
            rounded
            size="small"
            severity="secondary"
            :disabled="index === 0"
            :aria-label="t('moveUp')"
            @click="move(index, -1)"
          />
          <Button
            icon="pi pi-arrow-down"
            text
            rounded
            size="small"
            severity="secondary"
            :disabled="index === entries.length - 1"
            :aria-label="t('moveDown')"
            @click="move(index, 1)"
          />
          <Button
            icon="pi pi-minus-circle"
            text
            rounded
            size="small"
            severity="danger"
            v-tooltip="t('removeFromPlaylist')"
            :aria-label="t('removeFromPlaylist')"
            @click="remove(entry)"
          />
        </li>
      </ol>
    </template>
  </section>
</template>

<style scoped>
.entries {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.entry {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.35rem 0.25rem;
  border-radius: var(--radius-small);
}
</style>
