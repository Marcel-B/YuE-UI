<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import Panel from 'primevue/panel'
import Slider from 'primevue/slider'
import { computed } from 'vue'
import { t } from '../../logic/i18n'
import type { FormState } from '../../logic/options'
import { TRACK_NAMES } from '../../logic/types'

const form = defineModel<FormState>({ required: true })

const octaves = [-4, -3, -2, -1, 0, 1, 2, 3, 4]
const bassOctaves = [-2, -1, 0, 1, 2]
const countInBars = [0, 1, 2, 4]
const channels = Array.from({ length: 16 }, (_, i) => i + 1)

/**
 * Channels and programs are kept only for the tracks that have one, so that saved settings stay small and
 * a track without a setting keeps following the converter.
 */
function assign(values: Record<string, number>, track: string, value: number): void {
  if (value > 0) {
    values[track] = value
  } else {
    delete values[track]
  }
}

function signed(value: number): string {
  return value > 0 ? `+${value}` : String(value)
}

const numbered = (values: number[]) => values.map((value) => ({ label: signed(value), value }))
const octaveOptions = numbered(octaves)
const smallOctaveOptions = numbered(bassOctaves)

// Computed, so the labels follow a change of language.
/** "Same as both" stands for null; the select shows that null through its placeholder. */
const partOctaveOptions = computed(() => [{ label: t('sameAsBoth'), value: null }, ...octaveOptions])
const chordPatterns = computed(() => [
  { label: t('chordsAsWritten'), value: 'as-written' },
  { label: t('chordsEighths'), value: 'Eighths' },
  { label: t('chordsOffbeat'), value: 'Offbeat' },
  { label: t('chordsSixteenths'), value: 'Sixteenths' },
  { label: t('chordsArpeggio'), value: 'ArpeggioUp' },
  { label: t('chordsArpeggioUpDown'), value: 'ArpeggioUpDown' },
])
const chordInversions = computed(() => [
  { label: t('chordRoot'), value: 'RootPosition' },
  { label: t('chordClosest'), value: 'Closest' },
  { label: t('chordFirst'), value: 'First' },
  { label: t('chordSecond'), value: 'Second' },
])
const bassPatterns = computed(() => [
  { label: t('bassOff'), value: 'off' },
  { label: t('bassEighths'), value: 'Eighths' },
  { label: t('bassQuarters'), value: 'Quarters' },
  { label: t('bassRootFifth'), value: 'RootFifth' },
  { label: t('bassOctaves'), value: 'Octaves' },
  { label: t('bassOffbeat'), value: 'Offbeat' },
  { label: t('bassSustained'), value: 'Sustained' },
  { label: t('bassWalking'), value: 'Walking' },
])
const drumPatterns = computed(() => [
  { label: t('drumsOff'), value: 'off' },
  { label: t('drumsOn'), value: 'FourOnTheFloor' },
  { label: t('drumsBackbeat'), value: 'Backbeat' },
  { label: t('drumsHalfTime'), value: 'HalfTime' },
  { label: t('drumsDisco'), value: 'Disco' },
  { label: t('drumsSixteenthHats'), value: 'SixteenthHats' },
  { label: t('drumsShuffle'), value: 'Shuffle' },
])
const swingUnits = computed(() => [
  { label: t('swingEighths'), value: 'Eighths' },
  { label: t('swingSixteenths'), value: 'Sixteenths' },
])
const countInOptions = computed(() =>
  countInBars.map((value) => ({ label: value === 0 ? t('countInOff') : String(value), value })),
)
const channelOptions = computed(() => [
  { label: t('midiAuto'), value: 0 },
  ...channels.map((channel) => ({ label: String(channel), value: channel })),
])
</script>

<template>
  <div class="options">
    <fieldset>
      <legend>{{ t('tracks') }}</legend>
      <div class="check">
        <Checkbox v-model="form.includeChords" binary input-id="opt-chords" />
        <label for="opt-chords">{{ t('includeChords') }}</label>
      </div>
      <Select
        v-model="form.chordPattern"
        :options="chordPatterns"
        option-label="label"
        option-value="value"
        :aria-label="t('chordPattern')"
        :disabled="!form.includeChords"
        fluid
      />
      <div class="row">
        <div class="field grow">
          <label for="opt-inversion">{{ t('chordInversion') }}</label>
          <Select
            v-model="form.chordInversion"
            input-id="opt-inversion"
            :options="chordInversions"
            option-label="label"
            option-value="value"
            :disabled="!form.includeChords"
            fluid
          />
        </div>
        <div class="field">
          <label for="opt-chord-octave">{{ t('chordOctave') }}</label>
          <Select
            v-model="form.chordOctave"
            input-id="opt-chord-octave"
            :options="smallOctaveOptions"
            option-label="label"
            option-value="value"
            :disabled="!form.includeChords"
            fluid
          />
        </div>
      </div>
      <div class="check">
        <Checkbox v-model="form.guideTones" binary input-id="opt-guide" />
        <label for="opt-guide">{{ t('guideTones') }}</label>
      </div>
      <div class="check">
        <Checkbox v-model="form.doubleVocal" binary input-id="opt-double" />
        <label for="opt-double">{{ t('doubleVocal') }}</label>
      </div>
    </fieldset>

    <fieldset>
      <legend>{{ t('octaves') }}</legend>
      <div class="row">
        <div class="field">
          <label for="opt-octave">{{ t('octaveBoth') }}</label>
          <Select
            v-model="form.octave"
            input-id="opt-octave"
            :options="octaveOptions"
            option-label="label"
            option-value="value"
            fluid
          />
        </div>
        <div class="field">
          <label for="opt-vocal-octave">{{ t('octaveVocal') }}</label>
          <Select
            v-model="form.vocalOctave"
            input-id="opt-vocal-octave"
            :options="partOctaveOptions"
            option-label="label"
            option-value="value"
            :placeholder="t('sameAsBoth')"
            fluid
          />
        </div>
        <div class="field">
          <label for="opt-ins-octave">{{ t('octaveIns') }}</label>
          <Select
            v-model="form.insOctave"
            input-id="opt-ins-octave"
            :options="partOctaveOptions"
            option-label="label"
            option-value="value"
            :placeholder="t('sameAsBoth')"
            fluid
          />
        </div>
        <div class="field" :class="{ disabled: !form.guideTones }">
          <label for="opt-guide-octave">{{ t('guideOctave') }}</label>
          <Select
            v-model="form.guideOctave"
            input-id="opt-guide-octave"
            :options="smallOctaveOptions"
            option-label="label"
            option-value="value"
            :disabled="!form.guideTones"
            fluid
          />
        </div>
        <div class="field" :class="{ disabled: !form.doubleVocal }">
          <label for="opt-double-octave">{{ t('doubleOctave') }}</label>
          <Select
            v-model="form.doubleOctave"
            input-id="opt-double-octave"
            :options="smallOctaveOptions"
            option-label="label"
            option-value="value"
            :disabled="!form.doubleVocal"
            fluid
          />
        </div>
      </div>
    </fieldset>

    <fieldset>
      <legend>{{ t('bass') }}</legend>
      <div class="row">
        <div class="field grow">
          <Select
            v-model="form.bass"
            :options="bassPatterns"
            option-label="label"
            option-value="value"
            :aria-label="t('bass')"
            fluid
          />
        </div>
        <div class="field">
          <label for="opt-bass-octave">{{ t('bassOctave') }}</label>
          <Select
            v-model="form.bassOctave"
            input-id="opt-bass-octave"
            :options="smallOctaveOptions"
            option-label="label"
            option-value="value"
            :disabled="form.bass === 'off'"
            fluid
          />
        </div>
      </div>
    </fieldset>

    <fieldset>
      <legend>{{ t('drums') }}</legend>
      <Select
        v-model="form.drums"
        :options="drumPatterns"
        option-label="label"
        option-value="value"
        :aria-label="t('drums')"
        fluid
      />
      <div class="check" :class="{ disabled: form.drums === 'off' }">
        <Checkbox v-model="form.crash" binary input-id="opt-crash" :disabled="form.drums === 'off'" />
        <label for="opt-crash">{{ t('crash') }}</label>
      </div>
      <div class="check" :class="{ disabled: form.drums === 'off' }">
        <Checkbox v-model="form.splitDrums" binary input-id="opt-split-drums" :disabled="form.drums === 'off'" />
        <label for="opt-split-drums">{{ t('splitDrums') }}</label>
      </div>
    </fieldset>

    <fieldset>
      <legend>{{ t('groove') }}</legend>
      <div class="row">
        <div class="field grow">
          <span>{{ t('swing') }}</span>
          <span class="slider">
            <Slider v-model="form.swing" :min="0" :max="100" :step="5" :aria-label="t('swing')" class="flex-1" />
            <output>{{ t('percentValue', { value: form.swing }) }}</output>
          </span>
        </div>
        <div class="field">
          <label for="opt-swing-unit">{{ t('swingUnit') }}</label>
          <Select
            v-model="form.swingUnit"
            input-id="opt-swing-unit"
            :options="swingUnits"
            option-label="label"
            option-value="value"
            :disabled="form.swing === 0"
            fluid
          />
        </div>
      </div>
      <div class="check" :class="{ disabled: form.swing === 0 || form.drums === 'off' }">
        <Checkbox
          v-model="form.straightDrums"
          binary
          input-id="opt-straight-drums"
          :disabled="form.swing === 0 || form.drums === 'off'"
        />
        <label for="opt-straight-drums">{{ t('straightDrums') }}</label>
      </div>
      <div class="field">
        <span>{{ t('humanize') }}</span>
        <span class="slider">
          <Slider v-model="form.humanize" :min="0" :max="100" :step="5" :aria-label="t('humanize')" class="flex-1" />
          <output>{{ t('percentValue', { value: form.humanize }) }}</output>
        </span>
      </div>
    </fieldset>

    <fieldset>
      <legend>{{ t('mono') }}</legend>
      <div class="check">
        <Checkbox v-model="form.mono" binary input-id="opt-mono" />
        <label for="opt-mono">{{ t('monoPrepare') }}</label>
      </div>
      <div class="check" :class="{ disabled: !form.mono }">
        <Checkbox v-model="form.legato" binary input-id="opt-legato" :disabled="!form.mono" />
        <label for="opt-legato">{{ t('legato') }}</label>
      </div>
    </fieldset>

    <fieldset>
      <legend>{{ t('countIn') }}</legend>
      <div class="row">
        <div class="field">
          <label for="opt-count-in">{{ t('countInBars') }}</label>
          <Select
            v-model="form.countIn"
            input-id="opt-count-in"
            :options="countInOptions"
            option-label="label"
            option-value="value"
            fluid
          />
        </div>
      </div>
      <div class="check" :class="{ disabled: form.countIn === 0 }">
        <Checkbox v-model="form.countInClick" binary input-id="opt-count-in-click" :disabled="form.countIn === 0" />
        <label for="opt-count-in-click">{{ t('countInClick') }}</label>
      </div>
    </fieldset>

    <fieldset>
      <legend>{{ t('logicProject') }}</legend>
      <div class="check">
        <Checkbox v-model="form.splitSections" binary input-id="opt-split-sections" />
        <label for="opt-split-sections">{{ t('splitSections') }}</label>
      </div>
    </fieldset>

    <!-- Rarely needed, so it starts collapsed like the advanced parameters in YuE UI. -->
    <Panel :header="t('advanced')" toggleable collapsed class="advanced">
      <div class="flex flex-wrap items-center gap-3">
        <label for="opt-ppq">{{ t('ppq') }}</label>
        <InputNumber
          v-model="form.ppq"
          input-id="opt-ppq"
          :min="24"
          :max="32767"
          :step="24"
          :use-grouping="false"
          :allow-empty="false"
          input-class="w-28"
        />
      </div>

      <p class="muted text-sm mt-3 mb-1">{{ t('midiTracksInfo') }}</p>
      <table class="tracks">
        <thead>
          <tr>
            <th>{{ t('midiTrack') }}</th>
            <th>{{ t('midiChannel') }}</th>
            <th>{{ t('midiProgram') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="track in TRACK_NAMES" :key="track">
            <th scope="row">{{ track }}</th>
            <td>
              <Select
                :model-value="form.channels[track] ?? 0"
                :options="channelOptions"
                option-label="label"
                option-value="value"
                :aria-label="`${t('midiChannel')} ${track}`"
                size="small"
                fluid
                @update:model-value="assign(form.channels, track, $event)"
              />
            </td>
            <td>
              <InputNumber
                :model-value="form.programs[track] ?? null"
                :min="1"
                :max="128"
                :placeholder="t('midiNone')"
                :aria-label="`${t('midiProgram')} ${track}`"
                size="small"
                fluid
                @update:model-value="assign(form.programs, track, $event ?? 0)"
              />
            </td>
          </tr>
        </tbody>
      </table>
    </Panel>
  </div>
</template>

<style scoped>
/* On a wide screen the groups stand next to each other instead of stretching across the whole card. */
.options {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(20rem, 100%), 1fr));
  gap: 1.25rem 2rem;
  align-items: start;
}

.options > .advanced {
  grid-column: 1 / -1;
}

fieldset {
  display: grid;
  gap: 0.5rem;
  min-width: 0;
  margin: 0;
  padding: 0;
  border: 0;
}

/* A checkbox and its text, clickable as one. */
.check {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
}

.check.disabled {
  color: var(--text-muted);
  cursor: default;
}

legend {
  margin-bottom: 0.4rem;
  padding: 0;
  font-weight: 600;
}

.row {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 0.75rem;
}

.field {
  display: grid;
  flex: 1 1 8.5rem;
  gap: 0.25rem;
  min-width: 0;
  color: var(--text-muted);
  font-size: 0.875rem;
}

/* A select is as wide as its label unless told otherwise, which would push the row past a phone's width. */
.field > * {
  min-width: 0;
}

.row .grow {
  flex: 1 1 14rem;
}

.field.disabled {
  opacity: 0.6;
}

/* A slider and the value it stands at, so the percentage is readable while dragging. */
.slider {
  display: flex;
  align-items: center;
  gap: 1rem;
  min-height: 2.5rem;
  padding-left: 0.5rem;
}

.slider output {
  min-width: 3rem;
  color: var(--text);
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.tracks {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.875rem;
}

.tracks th {
  color: var(--text-muted);
  font-weight: 500;
  text-align: left;
}

.tracks td,
.tracks th {
  padding: 0.15rem 0.5rem 0.15rem 0;
}
</style>
