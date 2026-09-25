<script setup lang="ts">
import { computed, onBeforeUnmount, useTemplateRef, watch } from 'vue'
import { t } from '../i18n'
import { attach, close, current, hasNext, hasPrevious, next, playing, previous } from '../player'
import { playlistIds, toggleInPlaylist } from '../playlist'

const emit = defineEmits<{ error: [message: string] }>()

const inPlaylist = computed(() => !!current.value && playlistIds.value.includes(current.value.id))

const audio = useTemplateRef<HTMLAudioElement>('audio')
watch(audio, (element) => attach(element), { immediate: true })
onBeforeUnmount(() => attach(null))

/** The song that is playing, into the playlist or out of it, without looking for it in the library first. */
async function togglePlaylist(): Promise<void> {
  if (!current.value) {
    return
  }
  try {
    await toggleInPlaylist(current.value.id)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
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
      <div class="min-w-0 flex-1">
        <div class="truncate font-semibold">{{ current?.title }}</div>
        <div class="truncate text-sm text-muted-color">{{ current?.detail }}</div>
      </div>
      <Button
        :icon="inPlaylist ? 'pi pi-check-circle' : 'pi pi-plus-circle'"
        text
        rounded
        v-tooltip.top="inPlaylist ? t('removeFromPlaylist') : t('addToPlaylist')"
        :aria-label="inPlaylist ? t('removeFromPlaylist') : t('addToPlaylist')"
        :aria-pressed="inPlaylist"
        @click="togglePlaylist"
      />
      <Button
        icon="pi pi-step-backward"
        text
        rounded
        :disabled="!hasPrevious && !playing"
        :aria-label="t('previousTrack')"
        @click="previous"
      />
      <Button icon="pi pi-step-forward" text rounded :disabled="!hasNext" :aria-label="t('nextTrack')" @click="next" />
      <Button icon="pi pi-times" text rounded severity="secondary" :aria-label="t('closePlayer')" @click="close" />
    </div>
    <audio ref="audio" controls preload="none" @play="playing = true" @pause="playing = false" @ended="ended" />
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
