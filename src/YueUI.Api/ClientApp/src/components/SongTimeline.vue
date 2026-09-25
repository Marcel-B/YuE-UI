<script setup lang="ts">
import { computed, onUnmounted, ref, watchEffect } from 'vue'
import { formatDuration, stageLabel, t } from '../i18n'
import type { SongState, Stage } from '../types'

const props = defineProps<{ song: SongState }>()

/**
 * The stages a song still has ahead of it, in the worker's order. A render starts from saved tokens, so it only
 * synthesizes and decodes. Planning is skipped when planning is off, which the worker does not announce: the step
 * shows as pending until composing begins and then drops out.
 */
const PIPELINE: Stage[] = ['planning', 'tokens', 'synth', 'decode']
const RENDER_PIPELINE: Stage[] = ['synth', 'decode']

type Step = {
  stage: Stage
  state: 'done' | 'current' | 'pending' | 'end'
  /** Seconds the stage took, or has taken so far. */
  seconds?: number
}

// The current stage's time runs while the page is open; one tick a second is enough for m:ss.
const now = ref(Date.now())
let timer: number | undefined
watchEffect(() => {
  if (!props.song.finished && timer === undefined) {
    timer = window.setInterval(() => (now.value = Date.now()), 1000)
  } else if (props.song.finished && timer !== undefined) {
    window.clearInterval(timer)
    timer = undefined
  }
})
onUnmounted(() => window.clearInterval(timer))

function seconds(from: string, to: string | number): number {
  return Math.max(0, (new Date(to).getTime() - new Date(from).getTime()) / 1000)
}

const steps = computed<Step[]>(() => {
  const song = props.song
  const history = song.stages ?? []
  const steps: Step[] = history.map((entry, i) => {
    const next = history[i + 1]
    if (next) {
      return { stage: entry.stage, state: 'done', seconds: seconds(entry.startedAt, next.startedAt) }
    }
    return song.finished
      ? { stage: entry.stage, state: 'end' }
      : { stage: entry.stage, state: 'current', seconds: seconds(entry.startedAt, now.value) }
  })
  if (!song.finished) {
    const pipeline = song.render ? RENDER_PIPELINE : PIPELINE
    const reached = pipeline.indexOf(song.stage)
    steps.push(...pipeline.slice(reached + 1).map((stage): Step => ({ stage, state: 'pending' })))
  }
  return steps
})

/** From joining the queue to the end, once finished. */
const total = computed(() => {
  const history = props.song.stages ?? []
  const first = history[0]
  const last = history[history.length - 1]
  return props.song.finished && first && last ? seconds(first.startedAt, last.startedAt) : undefined
})

function icon(step: Step): string {
  if (step.state === 'done') return 'pi pi-check'
  if (step.state === 'current') return 'pi pi-spin pi-spinner'
  if (step.state === 'pending') return 'pi pi-circle'
  return step.stage === 'ready' ? 'pi pi-check-circle' : step.stage === 'failed' ? 'pi pi-times-circle' : 'pi pi-ban'
}

function iconColor(step: Step): string {
  if (step.state === 'pending') return 'text-muted-color'
  if (step.state === 'end' && step.stage === 'failed') return 'text-red-500'
  if (step.state === 'end' && step.stage === 'cancelled') return 'text-muted-color'
  return 'text-primary'
}
</script>

<template>
  <Timeline :value="steps" class="mt-2" :pt="{ eventOpposite: { class: 'hidden' } }">
    <template #marker="{ item }">
      <i :class="[icon(item), iconColor(item), 'text-sm']" aria-hidden="true" />
    </template>
    <template #content="{ item }">
      <div class="pb-2">
        <div class="flex gap-2 items-baseline">
          <span :class="['text-sm', item.state === 'pending' ? 'text-muted-color' : 'font-medium']">
            {{ stageLabel(item.stage) }}
          </span>
          <span v-if="item.seconds !== undefined" class="text-sm text-muted-color tabular-nums">
            {{ formatDuration(item.seconds) }}
          </span>
          <span v-if="item.state === 'end' && total !== undefined" class="text-sm text-muted-color">
            {{ t('stageTotal', { time: formatDuration(total) }) }}
          </span>
        </div>
        <template v-if="item.state === 'current'">
          <div class="text-sm text-muted-color">
            <span v-if="song.engine">{{ song.engine }} · </span>{{ song.detail }}
          </div>
          <ProgressBar
            v-if="item.stage !== 'queued'"
            :value="Math.round(song.fraction * 100)"
            :show-value="false"
            class="h-1.5 mt-1"
            :aria-label="stageLabel(item.stage)"
          />
        </template>
      </div>
    </template>
  </Timeline>
</template>
