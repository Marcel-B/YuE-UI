<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import { exportSongFile, saveBlob } from '../api'
import {
  drawCover,
  exportFormats,
  exportTarget,
  genreOf,
  loadExportSettings,
  saveExportSettings,
  type ExportFormat,
  type ExportTarget,
} from '../export'
import { t } from '../i18n'

/**
 * App.vue holds the one dialog for sharing and exporting; the library, the player and the playlist open it through
 * `openExport`/`openExportFor`. The file is made first, then saved or handed to the share sheet from a fresh tap,
 * since Safari refuses `navigator.share` once the tap that asked for it is a few seconds old.
 */
type Step =
  { step: 'options' } | { step: 'preparing' } | { step: 'ready'; file: File } | { step: 'failed'; message: string }

const settings = loadExportSettings()
const format = ref<ExportFormat>(settings.format)
const artist = ref(settings.artist)
const genre = ref('')
const state = ref<Step>({ step: 'options' })

/**
 * The cover is chosen in one place, the library's "Choose cover", and kept with the song; the server puts it into
 * every file itself. Only a song without one gets the drawn cover, sent along with this export.
 */
const drawn = ref<Blob | null>(null)
const drawnUrl = ref<string | null>(null)
const coverUrl = computed(() => exportTarget.value?.cover ?? drawnUrl.value)

const formatOptions = computed(() =>
  exportFormats.map((value) => ({ value, label: value === 'small' ? t('exportFormatSmall') : value.toUpperCase() })),
)

function setDrawn(blob: Blob | null): void {
  if (drawnUrl.value) {
    URL.revokeObjectURL(drawnUrl.value)
  }
  drawn.value = blob
  drawnUrl.value = blob ? URL.createObjectURL(blob) : null
}

async function drawFor(target: ExportTarget): Promise<void> {
  try {
    const blob = await drawCover(target)
    if (exportTarget.value === target) {
      setDrawn(blob)
    }
  } catch {
    // exported without a cover rather than not at all
  }
}

// A fresh start for every song: the genre and the drawn cover are the song's own.
watch(
  exportTarget,
  (target) => {
    if (target) {
      state.value = { step: 'options' }
      genre.value = genreOf(target.style)
      setDrawn(null)
      if (!target.cover) {
        void drawFor(target)
      }
    }
  },
  { immediate: true },
)

onBeforeUnmount(() => setDrawn(null))

async function start(): Promise<void> {
  const target = exportTarget.value
  if (!target || state.value.step === 'preparing') {
    return
  }
  saveExportSettings({ format: format.value, artist: artist.value.trim() })
  state.value = { step: 'preparing' }
  try {
    const file = await exportSongFile(
      target.songId,
      format.value,
      artist.value.trim(),
      genre.value.trim(),
      target.cover ? null : drawn.value,
    )
    // The dialog may have been closed and opened for another song meanwhile.
    if (exportTarget.value === target) {
      state.value = { step: 'ready', file }
    }
  } catch (caught) {
    if (exportTarget.value === target) {
      state.value = { step: 'failed', message: caught instanceof Error ? caught.message : String(caught) }
    }
  }
}

/** A share sheet for files exists on phones and in Safari; elsewhere only saving. */
const canShare = computed(
  () =>
    state.value.step === 'ready' &&
    typeof navigator.canShare === 'function' &&
    navigator.canShare({ files: [state.value.file] }),
)

function save(file: File): void {
  saveBlob(file, file.name)
  close()
}

/** Called from the button's own tap: Safari opens the share sheet only right after one. */
async function share(file: File): Promise<void> {
  try {
    await navigator.share({ files: [file], title: file.name })
    close()
  } catch (caught) {
    if (caught instanceof DOMException && caught.name === 'AbortError') {
      return // closed the share sheet; the file is still there to save
    }
    state.value = { step: 'failed', message: caught instanceof Error ? caught.message : String(caught) }
  }
}

function close(): void {
  exportTarget.value = null
}
</script>

<template>
  <Dialog
    :visible="exportTarget !== null"
    modal
    :header="t('exportTitle', { title: exportTarget?.title ?? '' })"
    :draggable="false"
    :style="{ width: 'min(26rem, calc(100vw - 2rem))' }"
    @update:visible="(open: boolean) => !open && close()"
  >
    <form v-if="state.step === 'options'" class="flex flex-col gap-3" @submit.prevent="start">
      <div class="flex flex-col gap-1">
        <SelectButton
          v-model="format"
          :options="formatOptions"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('exportFormat')"
          class="w-full"
          :pt="{ pcToggleButton: { root: { class: 'flex-1' } } }"
        />
        <small class="muted">{{ t(`exportFormatHint_${format}`) }}</small>
      </div>
      <div class="flex gap-3 items-start">
        <div class="flex flex-col gap-2 shrink-0">
          <img v-if="coverUrl" :src="coverUrl" :alt="t('exportCover')" class="w-24 h-24 rounded-md object-cover" />
          <div v-else class="w-24 h-24 rounded-md bg-emphasis" />
        </div>
        <small class="muted min-w-0 flex-1">{{
          t(exportTarget?.cover ? 'exportCoverSong' : 'exportCoverDrawn')
        }}</small>
      </div>
      <label class="flex flex-col gap-1">
        <span>{{ t('exportArtist') }}</span>
        <InputText v-model="artist" fluid :placeholder="t('exportArtistPlaceholder')" maxlength="200" />
      </label>
      <label class="flex flex-col gap-1">
        <span>{{ t('exportGenre') }}</span>
        <InputText v-model="genre" fluid maxlength="200" />
      </label>
      <p class="m-0 muted text-sm">{{ t('exportIncludes') }}</p>
      <div class="flex justify-end">
        <Button type="submit" :label="t('exportStart')" icon="pi pi-download" />
      </div>
    </form>
    <div v-else-if="state.step === 'preparing'" class="flex items-center gap-3">
      <i class="pi pi-spin pi-spinner text-xl" aria-hidden="true" />
      <span>{{ t('exportPreparing') }}</span>
    </div>
    <div v-else-if="state.step === 'ready'" class="flex flex-col gap-3">
      <p class="m-0">{{ t('shareReady', { name: state.file.name }) }}</p>
      <div class="flex justify-end gap-2">
        <Button
          :label="t('shareSave')"
          icon="pi pi-download"
          :severity="canShare ? 'secondary' : undefined"
          :text="canShare"
          @click="save(state.file)"
        />
        <Button v-if="canShare" :label="t('share')" icon="pi pi-share-alt" @click="share(state.file)" />
      </div>
    </div>
    <div v-else class="flex flex-col gap-3">
      <p class="m-0 danger" role="status">{{ t('exportFailed', { message: state.message }) }}</p>
      <div class="flex justify-end">
        <Button :label="t('exportBack')" severity="secondary" text @click="state = { step: 'options' }" />
      </div>
    </div>
  </Dialog>
</template>
