<script setup lang="ts">
import { computed, useTemplateRef } from 'vue'
import type Menu from 'primevue/menu'
import { t } from '../i18n'
import { inAnyPlaylist, inPlaylist, playlists, toggleInPlaylist } from '../playlist'

/**
 * The plus by a song (library, player). With one playlist a tap puts the song in or takes it out; with several it
 * opens a menu of them, each marked when the song is in it.
 */
const props = defineProps<{ songId: string; size?: 'small' }>()

const emit = defineEmits<{ error: [message: string] }>()

const menu = useTemplateRef<InstanceType<typeof Menu>>('menu')

const several = computed(() => playlists.value.length > 1)
const contained = computed(() => (several.value ? inAnyPlaylist(props.songId) : inPlaylist(props.songId)))
const label = computed(() =>
  several.value ? t('choosePlaylists') : contained.value ? t('removeFromPlaylist') : t('addToPlaylist'),
)

const items = computed(() =>
  playlists.value.map((playlist) => ({
    label: playlist.name,
    icon: playlist.songIds.includes(props.songId) ? 'pi pi-check-circle' : 'pi pi-circle',
    command: () => void toggle(playlist.id),
  })),
)

async function toggle(id?: number): Promise<void> {
  try {
    await toggleInPlaylist(props.songId, id)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

function click(event: Event): void {
  if (several.value) {
    menu.value?.toggle(event)
  } else {
    void toggle()
  }
}
</script>

<template>
  <Button
    :icon="contained ? 'pi pi-check-circle' : 'pi pi-plus-circle'"
    text
    rounded
    :size="size"
    v-tooltip.top="label"
    :aria-label="label"
    :aria-pressed="several ? undefined : contained"
    :aria-haspopup="several ? 'true' : undefined"
    @click="click"
  />
  <Menu v-if="several" ref="menu" :model="items" popup />
</template>
