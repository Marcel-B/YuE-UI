<script setup lang="ts">
import { onBeforeUnmount, useTemplateRef, watch } from 'vue'
import { t } from '../i18n'
import { attach, close, current, hasNext, hasPrevious, listen, next, playerSource, playing, previous } from '../player'
import { rate, ratingOf } from '../ratings'
import { showSong, songHref } from '../view'
import PlaylistToggle from './PlaylistToggle.vue'
import SongMenu from './SongMenu.vue'
import VisualsButton from './VisualsButton.vue'

/** The playing song has a score, as far as the library knows. */
defineProps<{ hasScore: boolean }>()

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
  listen(false)
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
      <img
        v-if="current?.cover"
        :src="current.cover"
        alt=""
        class="h-10 w-10 shrink-0 cursor-pointer rounded-md object-cover"
        @click="showSong(current.songId)"
      />
      <div class="min-w-0 flex-1">
        <a
          v-if="current"
          :href="songHref(current.songId)"
          class="block truncate font-semibold text-color no-underline hover:underline"
          :title="t('showSong')"
          @click.prevent="showSong(current.songId)"
          >{{ current.title }} <span class="text-sm font-normal text-muted-color">· {{ current.detail }}</span></a
        >
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
    <audio ref="audio" controls preload="none" @play="onPlay" @pause="playing = false" @ended="ended" />
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

audio {
  width: 100%;
  height: 2.5rem;
}
</style>
