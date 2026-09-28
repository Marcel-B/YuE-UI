<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import { exportSongFile, putCover, saveBlob } from '../api'
import {
  drawCover,
  exportFormats,
  exportTarget,
  genreOf,
  loadExportSettings,
  photoCover,
  saveExportSettings,
  type ExportFormat,
} from '../export'
import { t } from '../i18n'

/** App.vue holds the one dialog; the library's "Export" opens it through `openExport`. */
type Step =
  { step: 'options' } | { step: 'preparing' } | { step: 'ready'; file: File } | { step: 'failed'; message: string }

const settings = loadExportSettings()
const format = ref<ExportFormat>(settings.format)
const artist = ref(settings.artist)
const genre = ref('')
const state = ref<Step>({ step: 'options' })

/** The cover sent along; null with the song's own, which the server takes itself. */
const cover = ref<Blob | null>(null)
const blobUrl = ref<string | null>(null)
/** Which cover goes in: the song's own, a photo just chosen (kept as the song's own too) or the drawn one. */
const coverKind = ref<'song' | 'photo' | 'drawn'>('drawn')
/** Saving the chosen photo as the song's cover failed; the export still takes it. */
const coverError = ref<string | null>(null)
const coverInput = ref<HTMLInputElement | null>(null)
const coverUrl = computed(() => (coverKind.value === 'song' ? (exportTarget.value?.cover ?? null) : blobUrl.value))

const formatOptions = computed(() => exportFormats.map((value) => ({ value, label: value.toUpperCase() })))

function setCover(blob: Blob | null): void {
  if (blobUrl.value) {
    URL.revokeObjectURL(blobUrl.value)
  }
  cover.value = blob
  blobUrl.value = blob ? URL.createObjectURL(blob) : null
}

function useSongCover(): void {
  setCover(null)
  coverKind.value = 'song'
}

async function useDrawnCover(): Promise<void> {
  const target = exportTarget.value
  if (!target) {
    return
  }
  coverKind.value = 'drawn'
  try {
    setCover(await drawCover(target))
  } catch {
    setCover(null) // exported without a cover rather than not at all
  }
}

async function pickCover(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = '' // so that picking the same photo again fires a change
  if (!file) {
    return
  }
  const target = exportTarget.value
  let photo: Blob
  try {
    photo = await photoCover(file)
  } catch {
    state.value = { step: 'failed', message: t('photoUnreadable') }
    return
  }
  setCover(photo)
  coverKind.value = 'photo'
  coverError.value = null
  if (!target) {
    return
  }
  // The photo was chosen for the song, not only for this file: the library and the player show it from now on.
  try {
    await putCover(target.songId, photo)
  } catch (caught) {
    if (exportTarget.value === target) {
      coverError.value = t('coverSaveFailed', { message: caught instanceof Error ? caught.message : String(caught) })
    }
  }
}

// A fresh start for every song: the genre and the drawn cover are the song's own.
watch(
  exportTarget,
  (target) => {
    if (target) {
      state.value = { step: 'options' }
      genre.value = genreOf(target.style)
      coverError.value = null
      if (target.cover) {
        useSongCover()
      } else {
        void useDrawnCover()
      }
    }
  },
  { immediate: true },
)

onBeforeUnmount(() => setCover(null))

async function start(): Promise<void> {
  const target = exportTarget.value
  if (!target || state.value.step === 'preparing') {
    return
  }
  saveExportSettings({ format: format.value, artist: artist.value.trim() })
  state.value = { step: 'preparing' }
  try {
    const file = await exportSongFile(target.songId, format.value, artist.value.trim(), genre.value.trim(), cover.value)
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
        <div class="flex flex-col gap-2 min-w-0 flex-1">
          <Button
            :label="t('exportCoverPhoto')"
            icon="pi pi-image"
            severity="secondary"
            size="small"
            @click="coverInput?.click()"
          />
          <Button
            v-if="exportTarget?.cover && coverKind === 'drawn'"
            :label="t('exportCoverSong')"
            icon="pi pi-image"
            severity="secondary"
            text
            size="small"
            @click="useSongCover"
          />
          <Button
            v-if="coverKind !== 'drawn'"
            :label="t('exportCoverDrawn')"
            icon="pi pi-palette"
            severity="secondary"
            text
            size="small"
            @click="useDrawnCover"
          />
          <small v-if="coverError" class="danger">{{ coverError }}</small>
          <small v-else-if="coverKind === 'photo'" class="muted">{{ t('exportCoverKept') }}</small>
          <input ref="coverInput" type="file" accept="image/*" class="hidden" @change="pickCover" />
        </div>
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
