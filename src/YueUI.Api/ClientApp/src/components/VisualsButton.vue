<script setup lang="ts">
import { computed, useTemplateRef } from 'vue'
import type Menu from 'primevue/menu'
import { t } from '../i18n'
import { reducedMotion, setVisual, visuals, type SpectrumSource } from '../spectrum'
import SpectrumBars from './SpectrumBars.vue'

/**
 * The player's small analyzer, which is also the button for what moves with the music: switched off, a chart icon
 * takes its place, so the switches stay within reach without a button of their own.
 */
defineProps<{ source: SpectrumSource }>()

const menu = useTemplateRef<InstanceType<typeof Menu>>('menu')

const check = (on: boolean) => (on ? 'pi pi-check-square' : 'pi pi-stop')

const items = computed(() => [
  {
    label: t('visualsPlayer'),
    icon: check(visuals.value.player),
    command: () => setVisual('player', !visuals.value.player),
  },
  {
    label: reducedMotion.value ? t('visualsBackgroundReduced') : t('visualsBackground'),
    icon: check(visuals.value.background),
    command: () => setVisual('background', !visuals.value.background),
  },
])
</script>

<template>
  <button
    type="button"
    class="visuals"
    :aria-label="t('visuals')"
    :title="t('visuals')"
    aria-haspopup="true"
    @click="menu?.toggle($event)"
  >
    <SpectrumBars v-if="visuals.player" :sources="[source]" :bars="16" />
    <i v-else class="pi pi-chart-bar" aria-hidden="true" />
  </button>
  <Menu ref="menu" :model="items" popup />
</template>

<style scoped>
.visuals {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 3.5rem;
  height: 1.5rem;
  padding: 0;
  border: 0;
  border-radius: var(--radius-small);
  color: var(--text-muted);
  background: transparent;
  cursor: pointer;
}

.visuals:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
</style>
