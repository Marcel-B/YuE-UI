<script setup lang="ts">
import { computed } from 'vue'
import { t } from '../../logic/i18n'
import type { Algorithm } from '../../logic/fm'

/**
 * The algorithm drawn as on the TX81Z's panel: carriers in the bottom row (filled, they are heard), each modulator
 * above what it modulates, operator 4's feedback as a loop. The boxes are also the switch between the operators,
 * so which one is edited and where it sits in the sound are seen at once.
 */
const props = defineProps<{ algorithm: Algorithm }>()
const selected = defineModel<number>({ required: true })

const BOX_W = 22
const BOX_H = 16
const GAP_X = 32
const ROW = 30
const PAD = 7

const layout = computed(() => {
  const { mods, carriers } = props.algorithm
  const targets = (op: number) => mods.filter(([from]) => from === op).map(([, to]) => to)
  // A modulator always has a higher number than its target, so rows and positions fill in from operator 1 up.
  const row: number[] = []
  const x: number[] = []
  for (let op = 0; op < 4; op++) {
    const below = targets(op)
    row[op] = carriers.includes(op) || below.length === 0 ? 0 : Math.max(...below.map((to) => row[to]! + 1))
    x[op] = row[op] === 0 ? carriers.indexOf(op) * GAP_X : below.reduce((sum, to) => sum + x[to]!, 0) / below.length
  }
  // Operators that ended up on the same spot in a row are spread around it, in their order.
  const rows = Math.max(...row) + 1
  for (let r = 1; r < rows; r++) {
    const ops = [0, 1, 2, 3].filter((op) => row[op] === r).sort((a, b) => x[a]! - x[b]! || a - b)
    const crowded = ops.some((op, i) => i > 0 && x[op]! - x[ops[i - 1]!]! < GAP_X)
    if (crowded) {
      const middle = ops.reduce((sum, op) => sum + x[op]!, 0) / ops.length
      ops.forEach((op, i) => (x[op] = middle + (i - (ops.length - 1) / 2) * GAP_X))
    }
  }
  const left = Math.min(...x)
  const boxes = [0, 1, 2, 3].map((op) => ({
    op,
    x: x[op]! - left + PAD,
    y: (rows - 1 - row[op]!) * ROW + PAD,
    carrier: carriers.includes(op),
  }))
  const width = Math.max(...boxes.map((box) => box.x)) + BOX_W + PAD + 6
  const height = (rows - 1) * ROW + BOX_H + PAD + 10
  const lines = mods.map(([from, to]) => {
    const a = boxes[from]!
    const b = boxes[to]!
    return { x1: a.x + BOX_W / 2, y1: a.y + BOX_H, x2: b.x + BOX_W / 2, y2: b.y }
  })
  const outs = boxes.filter((box) => box.carrier)
  const bus = {
    y: height - 4,
    x1: Math.min(...outs.map((box) => box.x + BOX_W / 2)),
    x2: Math.max(...outs.map((box) => box.x + BOX_W / 2)),
  }
  const four = boxes[3]!
  // From the right edge of operator 4 up, over and back down into its top: the feedback loop.
  const loop = `M${four.x + BOX_W} ${four.y + BOX_H / 2} h4 V${four.y - 4} H${four.x + BOX_W / 2} V${four.y}`
  return { boxes, lines, outs, bus, loop, width, height }
})

function role(op: number): string {
  if (props.algorithm.carriers.includes(op)) {
    return t('fmCarrier')
  }
  const targets = props.algorithm.mods.filter(([from]) => from === op).map(([, to]) => to + 1)
  return t('fmModulates', { targets: targets.join(', ') })
}

function choose(event: KeyboardEvent, op: number): void {
  if (event.key === 'Enter' || event.key === ' ') {
    selected.value = op
    event.preventDefault()
  }
}
</script>

<template>
  <svg
    class="diagram"
    :viewBox="`0 0 ${layout.width} ${layout.height}`"
    :style="{ width: `${layout.width * 1.75}px` }"
    role="group"
    :aria-label="t('fmOperators')"
  >
    <line v-for="(line, i) in layout.lines" :key="i" class="wire" v-bind="line" />
    <line
      v-for="box in layout.outs"
      :key="`out${box.op}`"
      class="wire"
      :x1="box.x + BOX_W / 2"
      :y1="box.y + BOX_H"
      :x2="box.x + BOX_W / 2"
      :y2="layout.bus.y"
    />
    <line class="bus" :x1="layout.bus.x1 - 4" :x2="layout.bus.x2 + 4" :y1="layout.bus.y" :y2="layout.bus.y" />
    <path class="wire" :d="layout.loop" />
    <g
      v-for="box in layout.boxes"
      :key="box.op"
      class="op"
      :class="{ carrier: box.carrier, selected: box.op === selected }"
      role="button"
      tabindex="0"
      :aria-pressed="box.op === selected"
      :aria-label="`${t('fmOperator', { n: box.op + 1 })}: ${role(box.op)}`"
      @click="selected = box.op"
      @keydown="choose($event, box.op)"
    >
      <rect :x="box.x" :y="box.y" :width="BOX_W" :height="BOX_H" rx="3" />
      <text :x="box.x + BOX_W / 2" :y="box.y + BOX_H / 2 + 0.5">{{ box.op + 1 }}</text>
    </g>
  </svg>
</template>

<style scoped>
.diagram {
  display: block;
  max-width: 100%;
  height: auto;
  overflow: visible;
}

.wire,
.bus {
  fill: none;
  stroke: var(--text-muted);
  stroke-width: 1.2;
}

.bus {
  stroke: var(--accent);
  stroke-width: 2;
  stroke-linecap: round;
}

.op {
  cursor: pointer;
  outline: none;
}

.op rect {
  fill: var(--surface);
  stroke: var(--border-strong);
  stroke-width: 1.2;
}

.op.carrier rect {
  fill: var(--accent-soft);
  stroke: var(--accent);
}

.op.selected rect {
  fill: var(--accent);
  stroke: var(--accent);
}

.op:focus-visible rect {
  stroke: var(--text);
  stroke-width: 2;
}

text {
  fill: var(--text);
  font-size: 9px;
  font-weight: 600;
  text-anchor: middle;
  dominant-baseline: middle;
  pointer-events: none;
}

.op.selected text {
  fill: var(--p-primary-contrast-color);
}
</style>
