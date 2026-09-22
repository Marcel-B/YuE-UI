import { ref } from 'vue'
import type { Stage, WorkerStatus } from './types'

export type Locale = 'de' | 'en'

const messages = {
  de: {
    subtitle: 'YuE Studio aus der Ferne',
    language: 'English',
    workerStopped: 'Worker aus',
    workerStarting: 'Worker startet',
    workerReady: 'Worker bereit',
    workerBusy: 'Rechnet',
    disconnected: 'Verbindung zum Server unterbrochen – verbinde neu …',
    studioRunning:
      'YuE Studio ist geöffnet und hat einen eigenen Worker. Beide gleichzeitig rechnen zu lassen kann den Speicher sprengen – am besten die App beenden.',
    workerError: 'Der Worker meldet: {message}',

    newSong: 'Neuer Song',
    title: 'Titel',
    titlePlaceholder: 'optional, benennt den Ordner',
    style: 'Stil',
    stylePlaceholder: 'z. B. Dark Synthwave, 110 BPM, weibliche Stimme, melancholisch',
    lyrics: 'Songtext',
    lyricsPlaceholder: '[verse]\n…\n\n[chorus]\n…',
    lyricsHint: 'Abschnitte mit [verse], [chorus], [bridge] … markieren.',
    instrumental: 'Instrumental (der Text liefert nur noch die Abschnitte)',
    quality: 'Qualität',
    qualityDraft: 'Entwurf',
    qualityFull: 'Voll',
    qualityHint: 'Ein Entwurf ist in wenigen Minuten fertig und lässt sich später in voller Qualität rendern.',
    batch: 'Anzahl',
    advanced: 'Erweitert',
    cot: 'Planung',
    cotFull: 'Melodie und Struktur (empfohlen)',
    cotMelody: 'Nur Melodie',
    cotOff: 'Keine',
    seed: 'Seed',
    seedPlaceholder: 'leer = zufällig',
    draftSteps: 'Schritte im Entwurf',
    engines: 'Rechenwerke',
    enginesAuto: 'Automatisch',
    enginesGpu: 'Nur GPU',
    enginesAne: 'GPU und Neural Engine',
    generate: 'Erzeugen',
    generating: 'Wird gesendet …',
    queued: 'In der Warteschlange. Der Fortschritt steht unten.',
    resetForm: 'Leeren',

    queue: 'Warteschlange',
    queueEmpty: 'Nichts in Arbeit.',
    songN: 'Song {n}',
    cancel: 'Abbrechen',
    stopAll: 'Alle abbrechen',
    shutdown: 'Worker beenden',
    shutdownHint: 'Beendet den Python-Prozess und gibt den Speicher frei, z. B. bevor YuE Studio wieder rechnen soll.',
    clearFinished: 'Erledigte ausblenden',
    log: 'Protokoll',
    logEmpty: 'Noch keine Meldungen.',

    stage_queued: 'Wartet',
    stage_planning: 'Plant',
    stage_tokens: 'Komponiert',
    stage_synth: 'Synthetisiert',
    stage_decode: 'Rendert Audio',
    stage_ready: 'Fertig',
    stage_failed: 'Fehlgeschlagen',
    stage_cancelled: 'Abgebrochen',

    library: 'Bibliothek',
    libraryEmpty: 'Noch keine Songs.',
    libraryError: 'Die Bibliothek konnte nicht geladen werden: {message}',
    refresh: 'Aktualisieren',
    showMore: 'Mehr anzeigen',
    useAsTemplate: 'Als Vorlage',
    templateLoaded: 'Stil und Text von „{title}“ stehen im Formular.',
    renderFull: 'Voll rendern',
    rendering: 'In Arbeit',
    download: 'FLAC',
    score: 'ABC',
    zip: 'ZIP',
    zipTitle: 'FLAC und ABC als ZIP',
    zipRun: 'Alle als ZIP',
    noAudio: 'Kein Audio',
    showLyrics: 'Text',
    untitled: 'Ohne Titel',

    errorNetwork: 'Der Server ist nicht erreichbar.',
    errorGeneric: 'Das hat nicht geklappt: {message}',
  },
  en: {
    subtitle: 'YuE Studio from afar',
    language: 'Deutsch',
    workerStopped: 'Worker off',
    workerStarting: 'Worker starting',
    workerReady: 'Worker ready',
    workerBusy: 'Working',
    disconnected: 'Lost the connection to the server – reconnecting …',
    studioRunning:
      'YuE Studio is open and has a worker of its own. Letting both generate at once can run out of memory – better quit the app.',
    workerError: 'The worker reports: {message}',

    newSong: 'New song',
    title: 'Title',
    titlePlaceholder: 'optional, names the folder',
    style: 'Style',
    stylePlaceholder: 'e.g. dark synthwave, 110 BPM, female vocals, melancholic',
    lyrics: 'Lyrics',
    lyricsPlaceholder: '[verse]\n…\n\n[chorus]\n…',
    lyricsHint: 'Mark sections with [verse], [chorus], [bridge] …',
    instrumental: 'Instrumental (the lyrics only provide the sections)',
    quality: 'Quality',
    qualityDraft: 'Draft',
    qualityFull: 'Full',
    qualityHint: 'A draft is done in a few minutes and can be rendered at full quality later.',
    batch: 'Songs',
    advanced: 'Advanced',
    cot: 'Planning',
    cotFull: 'Melody and structure (recommended)',
    cotMelody: 'Melody only',
    cotOff: 'None',
    seed: 'Seed',
    seedPlaceholder: 'empty = random',
    draftSteps: 'Draft steps',
    engines: 'Engines',
    enginesAuto: 'Automatic',
    enginesGpu: 'GPU only',
    enginesAne: 'GPU and Neural Engine',
    generate: 'Generate',
    generating: 'Sending …',
    queued: 'Queued. Progress is shown below.',
    resetForm: 'Clear',

    queue: 'Queue',
    queueEmpty: 'Nothing in progress.',
    songN: 'Song {n}',
    cancel: 'Cancel',
    stopAll: 'Cancel all',
    shutdown: 'Stop worker',
    shutdownHint: 'Ends the Python process and frees its memory, e.g. before YuE Studio should generate again.',
    clearFinished: 'Hide finished',
    log: 'Log',
    logEmpty: 'No messages yet.',

    stage_queued: 'Waiting',
    stage_planning: 'Planning',
    stage_tokens: 'Composing',
    stage_synth: 'Synthesizing',
    stage_decode: 'Rendering audio',
    stage_ready: 'Done',
    stage_failed: 'Failed',
    stage_cancelled: 'Cancelled',

    library: 'Library',
    libraryEmpty: 'No songs yet.',
    libraryError: 'Could not load the library: {message}',
    refresh: 'Refresh',
    showMore: 'Show more',
    useAsTemplate: 'Use as template',
    templateLoaded: 'Style and lyrics of “{title}” are in the form.',
    renderFull: 'Render full',
    rendering: 'In progress',
    download: 'FLAC',
    score: 'ABC',
    zip: 'ZIP',
    zipTitle: 'FLAC and ABC as ZIP',
    zipRun: 'All as ZIP',
    noAudio: 'No audio',
    showLyrics: 'Lyrics',
    untitled: 'Untitled',

    errorNetwork: 'The server cannot be reached.',
    errorGeneric: 'That did not work: {message}',
  },
} as const satisfies Record<Locale, Record<string, string>>

export type MessageKey = keyof (typeof messages)['de']

const storageKey = 'yue-ui.locale'

function initialLocale(): Locale {
  try {
    const stored = localStorage.getItem(storageKey)
    if (stored === 'de' || stored === 'en') {
      return stored
    }
  } catch {
    // Storage may be blocked; fall back to the browser's language.
  }
  return navigator.language.toLowerCase().startsWith('de') ? 'de' : 'en'
}

export const locale = ref<Locale>(initialLocale())

export function setLocale(value: Locale): void {
  locale.value = value
  document.documentElement.lang = value
  try {
    localStorage.setItem(storageKey, value)
  } catch {
    // Remembering the choice is a convenience only.
  }
}

export function t(key: MessageKey, params: Record<string, string | number> = {}): string {
  return messages[locale.value][key].replace(/\{(\w+)\}/g, (_, name: string) => String(params[name] ?? `{${name}}`))
}

export function stageLabel(stage: Stage): string {
  return t(`stage_${stage}`)
}

export function workerLabel(status: WorkerStatus, busy: boolean): string {
  if (busy) {
    return t('workerBusy')
  }
  return t(status === 'ready' ? 'workerReady' : status === 'starting' ? 'workerStarting' : 'workerStopped')
}

/** An ISO timestamp as the locale writes date and time, e.g. "22.09.2026, 10:05". */
export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString(locale.value, { dateStyle: 'short', timeStyle: 'short' })
}

export function formatTime(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleTimeString(locale.value, { timeStyle: 'medium' })
}

/** 309.4 → "5:09" */
export function formatDuration(seconds: number): string {
  const total = Math.round(seconds)
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`
}
