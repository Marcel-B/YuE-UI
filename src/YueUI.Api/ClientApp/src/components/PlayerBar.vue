<script setup lang="ts">
import { onBeforeUnmount, useTemplateRef, watch } from 'vue'
import { t } from '../i18n'
import {
  attach,
  close,
  current,
  expanded,
  guardSilence,
  hasNext,
  hasPrevious,
  listen,
  next,
  playerSource,
  playing,
  previous,
  refreshLockScreen,
  toggle,
  updateTime,
} from '../player'
import { rate, ratingOf } from '../ratings'
import NowPlaying from './NowPlaying.vue'
import PlaylistToggle from './PlaylistToggle.vue'
import SongMenu from './SongMenu.vue'
import VisualsButton from './VisualsButton.vue'

defineProps<{
  /** The playing song has a score, as far as the library knows. */
  hasScore: boolean
  /** The playing song's lyrics, for the full-screen player; empty for an instrumental or a song the library lacks. */
  lyrics: string
}>()

const emit = defineEmits<{ error: [message: string]; useScore: [songId: string]; newSong: [songId: string] }>()

const audio = useTemplateRef<HTMLAudioElement>('audio')
watch(audio, (element) => attach(element), { immediate: true })
onBeforeUnmount(() => attach(null))

async function rateCurrent(rating: number | null | undefined): Promise<void> {
  if (!current.value) {
    return
  }
  try {
    await rate(current.value.songId, rating ?? null)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

/** Also a start from the element's own controls or the lock screen, which bypass player.ts. */
function onPlay(): void {
  playing.value = true
  refreshLockScreen()
  listen(false)
  guardSilence()
}

/** Opens the full-screen player; the tap also routes the audio for its analyzer, which iOS allows only in one. */
function expand(): void {
  expanded.value = true
  listen()
}

function ended(): void {
  if (!next()) {
    playing.value = false
  }
}
</script>

<template>
  <!-- Always in the page, only hidden without a song: the element has to exist before the first click on "play". -->
  <div v-show="current" class="player" role="region" :aria-label="t('play')">
    <div class="flex items-center gap-2">
      <!-- Cover and title open the full-screen player, as a tap on the mini player does in the music apps. -->
      <img
        v-if="current?.cover"
        :src="current.cover"
        alt=""
        class="h-10 w-10 shrink-0 cursor-pointer rounded-md object-cover"
        @click="expand"
      />
      <div class="min-w-0 flex-1">
        <button
          v-if="current"
          type="button"
          class="title block w-full truncate text-left font-semibold text-color hover:underline"
          :title="t('openNowPlaying')"
          @click="expand"
        >
          {{ current.title }} <span class="text-sm font-normal text-muted-color">· {{ current.detail }}</span>
        </button>
        <!-- Rated while it plays, when the song is best judged. The line below the title has room for the stars and
             the analyzer. -->
        <div v-if="current" class="mt-1 flex items-center gap-3">
          <Rating :model-value="ratingOf(current.songId)" :aria-label="t('rating')" @update:model-value="rateCurrent" />
          <VisualsButton :source="playerSource" />
        </div>
      </div>
      <PlaylistToggle v-if="current" :song-id="current.songId" @error="emit('error', $event)" />
      <Button
        icon="pi pi-step-backward"
        text
        rounded
        :disabled="!hasPrevious && !playing"
        :aria-label="t('previousTrack')"
        @click="previous"
      />
      <!-- A play button of our own: its click is the gesture iOS wants for waking the analyzer's audio context,
           which the element's controls do not hand on. -->
      <Button
        :icon="playing ? 'pi pi-pause' : 'pi pi-play'"
        text
        rounded
        :aria-label="playing ? t('pause') : t('play')"
        @click="toggle"
      />
      <Button icon="pi pi-step-forward" text rounded :disabled="!hasNext" :aria-label="t('nextTrack')" @click="next" />
      <SongMenu
        v-if="current"
        :song-id="current.songId"
        :has-score="hasScore"
        @use-score="emit('useScore', $event)"
        @new-song="emit('newSong', $event)"
      />
      <Button icon="pi pi-times" text rounded severity="secondary" :aria-label="t('closePlayer')" @click="close" />
    </div>
    <audio
      ref="audio"
      controls
      preload="none"
      @play="onPlay"
      @pause="playing = false"
      @ended="ended"
      @timeupdate="updateTime"
      @durationchange="updateTime"
      @loadedmetadata="updateTime"
    />
    <Transition name="sheet">
      <NowPlaying
        v-if="expanded && current"
        :lyrics="lyrics"
        :has-score="hasScore"
        @use-score="emit('useScore', $event)"
        @new-song="emit('newSong', $event)"
        @error="emit('error', $event)"
      />
    </Transition>
  </div>
</template>

<style scoped>
.player {
  position: fixed;
  right: 0;
  bottom: 0;
  left: 0;
  z-index: 10;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  max-width: 72rem;
  margin: 0 auto;
  padding: 0.5rem max(1rem, env(safe-area-inset-right)) max(0.5rem, env(safe-area-inset-bottom))
    max(1rem, env(safe-area-inset-left));
  border-top: 1px solid var(--p-content-border-color);
  background: var(--p-content-background);
  box-shadow: 0 -4px 16px rgb(0 0 0 / 0.08);
}

.title {
  padding: 0;
  border: 0;
  font: inherit;
  background: none;
  cursor: pointer;
}

/* The full-screen player comes up from the mini player and goes back down into it. */
.sheet-enter-active,
.sheet-leave-active {
  transition:
    transform 0.25s ease,
    opacity 0.25s ease;
}

.sheet-enter-from,
.sheet-leave-to {
  transform: translateY(100%);
  opacity: 0.6;
}

@media (prefers-reduced-motion: reduce) {
  .sheet-enter-active,
  .sheet-leave-active {
    transition: none;
  }
}

audio {
  width: 100%;
  height: 2.5rem;
}
</style>
