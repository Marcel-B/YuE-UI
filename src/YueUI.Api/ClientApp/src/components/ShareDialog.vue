<script setup lang="ts">
import { computed } from 'vue'
import Dialog from 'primevue/dialog'
import { saveBlob } from '../api'
import { t } from '../i18n'
import { shareNow, shareState } from '../share'

/** What `shareSong` is doing; App.vue holds the one dialog, so every share button (library, player, playlist) uses it. */
const visible = computed(() => shareState.value !== null)

function close(): void {
  shareState.value = null
}

function save(file: File): void {
  saveBlob(file, file.name)
  close()
}
</script>

<template>
  <Dialog
    :visible="visible"
    modal
    :header="t('share')"
    :draggable="false"
    :style="{ width: 'min(24rem, calc(100vw - 2rem))' }"
    @update:visible="(open: boolean) => !open && close()"
  >
    <div v-if="shareState?.step === 'preparing'" class="flex items-center gap-3">
      <i class="pi pi-spin pi-spinner text-xl" aria-hidden="true" />
      <span>{{ t('sharePreparing') }}</span>
    </div>
    <div v-else-if="shareState?.step === 'ready'" class="flex flex-col gap-3">
      <p class="m-0">{{ t('shareReady', { name: shareState.file.name }) }}</p>
      <div class="flex justify-end gap-2">
        <Button
          :label="t('shareSave')"
          icon="pi pi-download"
          severity="secondary"
          text
          @click="save(shareState.file)"
        />
        <Button :label="t('share')" icon="pi pi-share-alt" @click="shareNow" />
      </div>
    </div>
    <p v-else-if="shareState?.step === 'failed'" class="m-0 danger" role="status">
      {{ t('shareFailed', { message: shareState.message }) }}
    </p>
  </Dialog>
</template>
