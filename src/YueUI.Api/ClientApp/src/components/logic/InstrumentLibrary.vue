<script setup lang="ts">
import Fieldset from 'primevue/fieldset'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ApiError, createInstrument, deleteInstrument, saveInstrumentNote, updateInstrument } from '../../logic/api'
import SongNote from '../SongNote.vue'
import { instruments, reloadInstruments } from '../../logic/instrumentLibrary'
import { t, type MessageKey } from '../../logic/i18n'
import { noteName, parseNote } from '../../logic/notes'
import {
  listMidiPorts,
  midiAlreadyAllowed,
  midiSupported,
  midiUsable,
  onMidiPortsChanged,
  rescanMidi,
  type MidiPort,
} from '../../logic/midiPlayer'
import { DRUMS, GENERAL_MIDI_DRUMS, type DrumNotes, type Instrument, type InstrumentKind } from '../../logic/types'

/**
 * The instrument library: a name for each MIDI port and channel the user's hardware listens on, and for a
 * drum machine the note each of its drums sits on. Kept on the server, so the list is the same from every
 * browser; the instruments page edits it here, the Logic page assigns tracks to it.
 */
const emit = defineEmits<{ /** Asks the page's keyboard to play this instrument. */ play: [instrument: Instrument] }>()

/** The port select's value for a port typed by hand, e.g. for an interface that is not plugged in right now. */
const OTHER_PORT = '\u0000other'

/**
 * Whether the browser can read the MIDI outputs of this machine, which Chromium and Firefox can. Elsewhere the
 * library is still edited - it lives on the server and decides channels and Logic routing, not playback - but
 * the output can only be chosen among the names that are already stored, since none can be looked up.
 */
const canReadPorts = midiUsable()

const ports = ref<MidiPort[]>([])
const canAskForMidi = ref(false)
/** The id being edited, or null while adding. */
const editing = ref<number | null>(null)
const name = ref('')
const port = ref<string>(OTHER_PORT)
const otherPort = ref('')
const channel = ref(1)
const kind = ref<InstrumentKind>('Synth')
/** The drum notes as typed: a name ("C1") or a number, checked on the way out. */
const drumText = ref<Record<keyof DrumNotes, string>>(drumTexts(GENERAL_MIDI_DRUMS))
const busy = ref(false)
const error = ref<string | null>(null)

const channels = Array.from({ length: 16 }, (_, index) => index + 1)
const kinds = computed(() => [
  { label: t('instrumentKindSynth'), value: 'Synth' },
  { label: t('instrumentKindDrumMachine'), value: 'DrumMachine' },
])

/**
 * The outputs to choose from: what the browser offers, then what the stored instruments name. The second half
 * is what makes the dialog work without Web MIDI - a port someone once entered stays available to everyone -
 * and it also helps in Chromium, where an interface that is unplugged right now is otherwise gone from the list.
 */
const portNames = computed(() => [
  ...new Set([
    ...ports.value.map((entry) => entry.name.trim()),
    ...instruments.value.map((entry) => entry.port.trim()),
  ]),
])

/** A port may be typed where the browser can read them; elsewhere only a stored name can be picked. */
const canTypePort = canReadPorts
/** The port select's entries: the known names, then "other" where a port may be typed by hand. */
const portOptions = computed(() => [
  ...portNames.value.map((entry) => ({ label: entry, value: entry })),
  ...(canTypePort ? [{ label: t('instrumentPortOther'), value: OTHER_PORT }] : []),
])
/** Without a single known output there is nothing to point an instrument at, so nothing can be added here. */
const canAdd = computed(() => canTypePort || portNames.value.length > 0)
const chosenPort = computed(() => (port.value === OTHER_PORT ? otherPort.value : port.value).trim())
/** The drum notes as numbers, or null where a field holds nothing a note can be read from. */
const drumNotes = computed<Record<keyof DrumNotes, number | null>>(
  () =>
    Object.fromEntries(DRUMS.map((drum) => [drum, parseNote(drumText.value[drum])])) as Record<
      keyof DrumNotes,
      number | null
    >,
)
const drumsComplete = computed(
  () => kind.value !== 'DrumMachine' || DRUMS.every((drum) => drumNotes.value[drum] !== null),
)
const complete = computed(() => name.value.trim().length > 0 && chosenPort.value.length > 0 && drumsComplete.value)

function drumTexts(notes: DrumNotes): Record<keyof DrumNotes, string> {
  return Object.fromEntries(DRUMS.map((drum) => [drum, noteName(notes[drum])])) as Record<keyof DrumNotes, string>
}

/** "C1 · 36" next to a field, so that either way of writing a note shows the other. */
function drumHint(drum: keyof DrumNotes): string {
  const note = drumNotes.value[drum]
  return note === null ? t('instrumentDrumInvalid') : `${noteName(note)} · ${note}`
}

/** One line for the table: "Kick C1 · Snare D1 · …". */
function drumSummary(notes: DrumNotes): string {
  return DRUMS.map((drum) => `${t(`instrumentDrum_${drum}` as MessageKey)} ${noteName(notes[drum])}`).join(' · ')
}

onMounted(async () => {
  startAdding()
  error.value = null
  try {
    await reloadInstruments()
  } catch (caught) {
    fail(caught)
  }
  if (!canReadPorts) {
    return
  }
  if (midiSupported() && (await midiAlreadyAllowed())) {
    await loadPorts()
  } else {
    canAskForMidi.value = midiSupported()
  }
})

async function loadPorts(): Promise<void> {
  const found = await listMidiPorts()
  ports.value = found.ports
  unreadable.value = found.unreadable
  restartFirefox.value = found.restartFirefox
  canAskForMidi.value = false
  hasAccess.value = found.access !== null
  // The page stays mounted while hidden, so a device plugged in meanwhile has to reach the list on its own.
  stopWatchingPorts ??= found.access
    ? onMidiPortsChanged(async () => {
        const changed = await listMidiPorts()
        ports.value = changed.ports
        unreadable.value = changed.unreadable
        restartFirefox.value = changed.restartFirefox
      })
    : null
  // A form opened before the ports were known can now offer the typed port from the list.
  if (port.value === OTHER_PORT && portNames.value.includes(otherPort.value.trim())) {
    port.value = otherPort.value.trim()
  } else if (port.value === OTHER_PORT && otherPort.value === '' && portNames.value.length > 0) {
    port.value = portNames.value[0]!
  }
}

const hasAccess = ref(false)
const unreadable = ref(false)
const restartFirefox = ref(false)
const rescanning = ref(false)
let stopWatchingPorts: (() => void) | null = null
onBeforeUnmount(() => stopWatchingPorts?.())

/** A device switched on after the page asked is only found by asking again in Firefox; see rescanMidi. */
async function rescan(): Promise<void> {
  rescanning.value = true
  try {
    await rescanMidi()
  } catch {
    // The access the page had stays.
  } finally {
    rescanning.value = false
  }
  await loadPorts()
}

function startAdding(): void {
  editing.value = null
  name.value = ''
  port.value = portNames.value[0] ?? OTHER_PORT
  otherPort.value = ''
  channel.value = 1
  kind.value = 'Synth'
  drumText.value = drumTexts(GENERAL_MIDI_DRUMS)
  error.value = null
}

function edit(instrument: Instrument): void {
  editing.value = instrument.id
  name.value = instrument.name
  channel.value = instrument.channel
  kind.value = instrument.kind
  drumText.value = drumTexts(instrument.drums ?? GENERAL_MIDI_DRUMS)
  error.value = null
  // Every stored port is among the names, so this is the usual way; the branch below is for a name that
  // changed between loading the list and editing.
  if (portNames.value.includes(instrument.port)) {
    port.value = instrument.port
    otherPort.value = ''
  } else {
    // The stored port is not connected right now; the name still shows, and can be changed.
    port.value = OTHER_PORT
    otherPort.value = instrument.port
  }
}

async function submit(): Promise<void> {
  if (!complete.value || busy.value) {
    return
  }

  busy.value = true
  error.value = null
  const drums =
    kind.value === 'DrumMachine'
      ? (Object.fromEntries(
          DRUMS.map((drum) => [drum, drumNotes.value[drum] ?? GENERAL_MIDI_DRUMS[drum]]),
        ) as unknown as DrumNotes)
      : null
  const input = { name: name.value.trim(), port: chosenPort.value, channel: channel.value, kind: kind.value, drums }
  try {
    if (editing.value === null) {
      await createInstrument(input)
    } else {
      await updateInstrument(editing.value, input)
    }
    await refresh()
    startAdding()
  } catch (caught) {
    fail(caught)
  } finally {
    busy.value = false
  }
}

async function remove(instrument: Instrument): Promise<void> {
  if (busy.value || !window.confirm(t('instrumentDeleteConfirm', { name: instrument.name }))) {
    return
  }

  busy.value = true
  error.value = null
  try {
    await deleteInstrument(instrument.id)
    await refresh()
    if (editing.value === instrument.id) {
      startAdding()
    }
  } catch (caught) {
    fail(caught)
  } finally {
    busy.value = false
  }
}

/** Instruments whose note is open; closed by default, the button shows whether one is written, as by a song. */
const openNotes = ref(new Set<number>())

function toggleNote(id: number): void {
  const open = new Set(openNotes.value)
  if (!open.delete(id)) {
    open.add(id)
  }
  openNotes.value = open
}

/** Kept in the shared list at once, so the button fills without reloading every instrument. */
function noteSaved(id: number, note: string): void {
  const instrument = instruments.value.find((entry) => entry.id === id)
  if (instrument) {
    instrument.note = note || null
  }
}

async function refresh(): Promise<void> {
  await reloadInstruments()
}

function fail(caught: unknown): void {
  error.value =
    caught instanceof ApiError && caught.status === 409
      ? t('instrumentNameTaken')
      : caught instanceof ApiError && caught.status === 0
        ? t('networkError')
        : `${caught instanceof Error ? caught.message : caught}`
}
</script>

<template>
  <div>
    <p class="muted mt-0 mb-4 text-sm">{{ t('instrumentsIntro') }}</p>

    <div v-if="instruments.length > 0" class="@container mb-4 overflow-x-auto">
      <table class="w-full border-collapse text-sm">
        <thead>
          <tr class="text-left text-muted-color text-xs">
            <th class="py-1.5 pr-2 font-medium">{{ t('instrumentName') }}</th>
            <th class="py-1.5 pr-2 font-medium">{{ t('instrumentPort') }}</th>
            <th class="py-1.5 pr-2 font-medium">{{ t('instrumentChannel') }}</th>
            <th class="py-1.5 pr-2 font-medium">{{ t('instrumentKind') }}</th>
            <th class="py-1.5 font-medium">
              <span class="sr-only">{{ t('instrumentEdit') }}</span>
            </th>
          </tr>
        </thead>
        <tbody>
          <template v-for="instrument in instruments" :key="instrument.id">
            <tr class="rule" :class="{ 'bg-highlight': instrument.id === editing }">
              <td class="py-1 pr-2 align-middle">{{ instrument.name }}</td>
              <td class="py-1 pr-2 align-middle">{{ instrument.port }}</td>
              <td class="py-1 pr-2 align-middle">{{ instrument.channel }}</td>
              <td class="py-1 pr-2 align-middle">
                {{ instrument.kind === 'DrumMachine' ? t('instrumentKindDrumMachine') : t('instrumentKindSynth') }}
                <span v-if="instrument.drums" class="muted block text-xs">{{ drumSummary(instrument.drums) }}</span>
              </td>
              <td class="py-1 align-middle">
                <div class="flex gap-1 whitespace-nowrap">
                  <Button
                    icon="pi pi-comment"
                    :text="!instrument.note"
                    size="small"
                    rounded
                    :severity="instrument.note ? undefined : 'secondary'"
                    v-tooltip.bottom="instrument.note ? t('instrumentNoteHas') : t('instrumentNote')"
                    :aria-label="instrument.note ? t('instrumentNoteHas') : t('instrumentNote')"
                    :aria-expanded="openNotes.has(instrument.id)"
                    @click="toggleNote(instrument.id)"
                  />
                  <Button
                    link
                    size="small"
                    icon="pi pi-play"
                    :aria-label="t('instrumentPlay')"
                    v-tooltip.bottom="t('instrumentPlay')"
                    @click="emit('play', instrument)"
                  />
                  <Button link size="small" :label="t('instrumentEdit')" :disabled="busy" @click="edit(instrument)" />
                  <Button
                    link
                    size="small"
                    severity="danger"
                    :label="t('instrumentDelete')"
                    :disabled="busy"
                    @click="remove(instrument)"
                  />
                </div>
              </td>
            </tr>
            <tr v-if="openNotes.has(instrument.id)">
              <td colspan="5" class="pb-1">
                <!-- As wide as the visible part of the table, which scrolls sideways on a phone, and held at its left. -->
                <SongNote
                  class="sticky left-0 w-[100cqw]"
                  :note="instrument.note ?? ''"
                  :save="(note) => saveInstrumentNote(instrument.id, note)"
                  :placeholder="t('instrumentNotePlaceholder')"
                  @saved="noteSaved(instrument.id, $event)"
                  @error="error = $event"
                />
              </td>
            </tr>
          </template>
        </tbody>
      </table>
    </div>
    <p v-else class="muted mt-0 mb-4 text-sm">{{ t('instrumentsEmpty') }}</p>

    <p v-if="!canReadPorts" class="hint muted">{{ t('instrumentsChromiumOnly') }}</p>

    <div v-if="!canAdd" class="mt-4 flex flex-col gap-3 rule pt-4">
      <p class="muted m-0 text-sm">{{ t('instrumentsNeedPort') }}</p>
    </div>

    <form v-else class="mt-4 flex flex-col gap-3 rule pt-4" @submit.prevent="submit">
      <h4 class="m-0 text-base">{{ editing === null ? t('instrumentAdd') : t('instrumentEditing', { name }) }}</h4>
      <div class="flex flex-col gap-1">
        <label for="instrument-name" class="text-sm">{{ t('instrumentName') }}</label>
        <InputText
          id="instrument-name"
          v-model.trim="name"
          required
          maxlength="64"
          spellcheck="false"
          placeholder="Mother32"
          fluid
        />
      </div>
      <div class="flex flex-col gap-1">
        <label id="instrument-port-label" class="text-sm">{{ t('instrumentPort') }}</label>
        <div class="flex gap-2">
          <Select
            v-model="port"
            :options="portOptions"
            option-label="label"
            option-value="value"
            aria-labelledby="instrument-port-label"
            class="min-w-0 flex-1"
          />
          <Button
            v-if="hasAccess"
            type="button"
            severity="secondary"
            outlined
            icon="pi pi-refresh"
            :aria-label="t('midiRescan')"
            v-tooltip.bottom="t('midiRescan')"
            :loading="rescanning"
            @click="rescan"
          />
        </div>
      </div>
      <p v-if="unreadable" class="hint m-0 text-sm text-(--warning-text)">{{ t('midiUnreadable') }}</p>
      <p v-else-if="restartFirefox" class="hint m-0 text-sm text-(--warning-text)">{{ t('midiRestartFirefox') }}</p>
      <InputText
        v-if="port === OTHER_PORT"
        v-model.trim="otherPort"
        :aria-label="t('instrumentPort')"
        required
        maxlength="128"
        spellcheck="false"
        placeholder="MIDI4x4 Midi Out 1"
        fluid
      />
      <div class="flex flex-col gap-1">
        <label id="instrument-channel-label" class="text-sm">{{ t('instrumentChannel') }}</label>
        <Select v-model="channel" :options="channels" aria-labelledby="instrument-channel-label" class="w-28" />
      </div>
      <p class="muted m-0 text-sm">{{ canTypePort ? t('instrumentPortHint') : t('instrumentPortStoredOnly') }}</p>
      <div class="flex flex-col gap-1">
        <label id="instrument-kind-label" class="text-sm">{{ t('instrumentKind') }}</label>
        <Select
          v-model="kind"
          :options="kinds"
          option-label="label"
          option-value="value"
          aria-labelledby="instrument-kind-label"
          fluid
        />
      </div>
      <Fieldset v-if="kind === 'DrumMachine'" :legend="t('instrumentDrums')" class="m-0">
        <p class="muted mt-0 mb-2 text-sm">{{ t('instrumentDrumsHint') }}</p>
        <div class="drum-grid grid gap-x-3 gap-y-2">
          <div v-for="drum in DRUMS" :key="drum" class="flex flex-col gap-1">
            <label :for="`instrument-drum-${drum}`" class="text-sm">{{
              t(`instrumentDrum_${drum}` as MessageKey)
            }}</label>
            <span class="flex items-center gap-2">
              <InputText
                :id="`instrument-drum-${drum}`"
                v-model.trim="drumText[drum]"
                required
                maxlength="5"
                spellcheck="false"
                :placeholder="noteName(GENERAL_MIDI_DRUMS[drum])"
                :invalid="drumNotes[drum] === null"
                class="w-20"
              />
              <span class="whitespace-nowrap text-xs" :class="drumNotes[drum] === null ? 'text-(--danger)' : 'muted'">
                {{ drumHint(drum) }}
              </span>
            </span>
          </div>
        </div>
      </Fieldset>
      <div class="mt-1 flex flex-wrap gap-2">
        <Button
          v-if="canAskForMidi"
          type="button"
          severity="secondary"
          outlined
          :label="t('previewFindMidi')"
          @click="loadPorts"
        />
        <Button
          type="submit"
          :label="editing === null ? t('instrumentAdd') : t('instrumentSave')"
          :disabled="!complete || busy"
          :loading="busy"
        />
        <Button
          v-if="editing !== null"
          type="button"
          severity="secondary"
          outlined
          :label="t('instrumentCancel')"
          @click="startAdding"
        />
      </div>
      <p v-if="error" class="hint danger m-0" role="alert">{{ error }}</p>
    </form>
  </div>
</template>

<style scoped>
/* Tailwind runs without preflight, so a border utility alone would have no style; a plain rule is simpler. */
.rule {
  border-top: 1px solid var(--border);
}

/* As many drum fields per row as fit, one per row on a narrow phone. */
.drum-grid {
  grid-template-columns: repeat(auto-fill, minmax(10rem, 1fr));
}
</style>
