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
    moreInfo: 'Mehr dazu',
    defaultValue: '{value} (Standard)',
    title: 'Titel',
    titlePlaceholder: 'optional',
    titleHint: 'Benennt den Ordner und steht in der Bibliothek. Auf die Musik hat er keinen Einfluss.',
    style: 'Stil',
    stylePlaceholder: 'z. B. German, dark synthwave, male baritone, analog synths, 110 BPM',
    styleHint: 'Sprache, Genre, Stimme, Instrumente, Tempo und Stimmung – als Stichworte, durch Kommas getrennt.',
    styleMore:
      'Der Stil prägt Klang, Arrangement und Gesang. Konkrete Stichworte wirken stärker als allgemeine: „rounded bass and light drums“ statt „gute Band“. Die YuE2-Beispiele beginnen mit der Sprache des Gesangs.\n\nBeispiele:\n• English, warm piano pop, expressive female voice, acoustic piano, rounded bass and light drums, lyrical memorable melody, unhurried phrasing, 88 BPM\n• English, jazz-funk, warm lead vocal, Rhodes, bass and drums\n• German, dark synthwave, male baritone, analog synths, gated reverb drums, melancholic, 110 BPM',
    lyrics: 'Songtext',
    lyricsOptional: 'Songtext (optional)',
    lyricsPlaceholder: '[Verse]\n…\n\n[Chorus]\n…',
    lyricsHint: 'Abschnitte mit [Verse], [Chorus], [Bridge] … markieren, eine gesungene Zeile pro Zeile.',
    lyricsMore:
      'Der Text bestimmt, was gesungen wird, und über seine Abschnitte die Form des Songs – und damit auch seine Länge. Übliche Marken: [Intro], [Verse], [Pre-Chorus], [Chorus], [Bridge], [Outro].\n\nBeispiel („City Lights“ aus den YuE2-Beispielen):\n[Verse]\nNeon fades along the lane\nFootsteps keep the time of rain\nFold the night and leave it here\nMorning has a sky to clear\n\n[Chorus]\nLet the day come into view\nEvery road begins with you\nHold a little room for light\nWe will sing beyond the night',
    instrumental: 'Instrumental',
    instrumentalHint: 'Ohne Gesang: vom Text zählen nur noch die Abschnittsmarken.',
    instrumentalMore:
      'Setzt „Instrumental, no vocals, no singing“ vor den Stil, behält vom Text nur die Marken wie [Verse] und [Chorus] und ersetzt die Gesangsstimme der geplanten Partitur durch Pausen. Ohne Text nimmt der Worker [Intro] [Verse] [Chorus] [Outro]. Das braucht eine Planung; „Keine“ wird dann zu „Melodie und Akkorde“.',
    quality: 'Qualität',
    qualityDraft: 'Entwurf',
    qualityFull: 'Voll',
    qualityHint: 'Ein Entwurf ist in wenigen Minuten fertig und lässt sich später in voller Qualität rendern.',
    qualityMore:
      'Beide komponieren denselben Song; der Unterschied liegt in der Synthese des Klangs.\n• Entwurf: 8 Solver-Schritte (einstellbar unter „Erweitert“), auf der GPU.\n• Voll: 32 Schritte, standardmäßig auf der Neural Engine, dauert deutlich länger.\n\n„Voll rendern“ in der Bibliothek synthetisiert einen Entwurf mit denselben Tokens und demselben Seed neu. Bewährt: mehrere Entwürfe erzeugen und nur den besten voll rendern.',
    batch: 'Anzahl',
    batchHint: 'Mehrere Varianten desselben Songs, jede mit eigenem Seed.',
    batchMore:
      'Song 1 bekommt den Seed, Song 2 den Seed + 1 usw. – gleicher Stil und Text, verschiedene Interpretationen. Bis zu vier Songs (zwei auf Macs mit weniger als 24 GB) werden gemeinsam komponiert und kosten dabei kaum mehr Zeit als einer; die Synthese läuft danach Song für Song.',
    advanced: 'Erweiterte Parameter',
    advancedChanged: 'angepasst',
    advancedIntro: 'Mit den Standardwerten vorbelegt; für einen normalen Song muss hier nichts geändert werden.',
    advancedReset: 'Standardwerte',
    cot: 'Planung',
    cotFull: 'Melodie und Akkorde',
    cotMelody: 'Nur Melodie',
    cotOff: 'Keine',
    cotHint: 'Ob YuE2 vor dem Audio eine Partitur schreibt, an der sich der Song orientiert.',
    cotMore:
      '• Melodie und Akkorde (Standard): zuerst eine Partitur im ABC-Format mit Melodie und Akkorden, dann der Song danach. Die Partitur lässt sich in der Bibliothek als ABC herunterladen und bearbeiten.\n• Nur Melodie: Partitur ohne Akkorde, die Begleitung ist frei. Empfohlen zusammen mit einer eigenen Partitur für Covers, damit sich das Arrangement dem neuen Stil anpasst.\n• Keine: erzeugt direkt aus Stil und Text. Spart die Planungsphase, liefert aber keine Partitur.',
    seed: 'Seed',
    seedPlaceholder: 'leer = zufällig',
    seedHint: 'Startwert des Zufalls. Gleicher Seed und gleiche Eingaben ergeben (fast) denselben Song.',
    seedMore:
      'Nützlich zum Vergleichen: Seed eines gelungenen Songs übernehmen (die Bibliothek zeigt ihn als #831001) und nur den Stil oder eine Textzeile ändern – der Rest bleibt vergleichbar. Leer lassen für einen neuen Zufallswert. Über Geräte und Softwareversionen hinweg ist das Ergebnis nicht garantiert gleich.',
    draftSteps: 'Schritte im Entwurf',
    draftStepsHint: 'Solver-Schritte der Klangsynthese eines Entwurfs, 1–32.',
    draftStepsFullHint: 'Volle Qualität nutzt immer 32 Schritte.',
    draftStepsMore:
      'Mehr Schritte ergeben einen saubereren Klang, die Synthese dauert entsprechend länger. Melodie und Text ändern sich nicht, nur die Klangqualität.\n• 4: schnellste Hörprobe\n• 8: Standard\n• 16: besserer Entwurf, etwa doppelt so lange Synthese\n• 32: entspricht „Voll“ mit „Nur GPU“',
    engines: 'Rechenwerke',
    enginesAuto: 'Automatisch',
    enginesGpu: 'Nur GPU',
    enginesAne: 'GPU und Neural Engine',
    enginesHint: 'Wo die Klangsynthese läuft. Automatisch: Entwurf auf der GPU, voll auf der Neural Engine.',
    enginesMore:
      '• Automatisch (Standard): Entwürfe auf der GPU, volle Qualität auf der Neural Engine – dort bei kurzen Songs etwa doppelt so schnell.\n• Nur GPU: lässt die rund 2,8 GB der Neural Engine frei, hilfreich bei knappem Speicher, z. B. wenn YuE Studio zugleich läuft.\n• GPU und Neural Engine: auch Entwürfe dürfen auf die Neural Engine.\n\nKann die Neural Engine einen Song nicht übernehmen (etwa einen sehr langen), läuft er auf der GPU.',
    maxLength: 'Maximale Länge',
    maxLengthHint: 'Obergrenze für die Songlänge; der Song endet sonst von selbst, wenn der Text gesungen ist.',
    maxLengthMore:
      'Wie lang ein Song wird, ergibt sich aus Text und Stil. Erreicht er die Grenze, wird er dort abgeschnitten. Eine kurze Grenze spart Zeit, wenn nur der Anfang interessiert, z. B. 1:00 zum Ausprobieren eines Stils. Mehr als 6:00 kann YuE2 nicht (9000 Tokens, 25 pro Sekunde).',
    abc: 'Eigene Partitur (ABC)',
    abcPlaceholder: 'X:1\nM:4/4\nL:1/16\nQ:1/4=88\nK:C\n…',
    abcHint: 'Ersetzt die Planung durch eine eigene Partitur: Melodie, Akkorde, Tempo und Form stehen dann fest.',
    abcMore:
      'Zum Beispiel die ABC-Datei eines Songs aus der Bibliothek mit geänderten Akkorden oder einem anderen Tempo (Q:), oder eine mit SheetSage2 transkribierte Melodie für ein Cover. Braucht die Planung „Melodie und Akkorde“ (Akkorde werden übernommen) oder „Nur Melodie“ mit einer Partitur ohne Akkordsymbole (die Begleitung ist frei). Die Silben des Textes sollten zu den Noten der Stimme „Vocal“ passen. Bei „Instrumental“ bleibt die Gesangsstimme einer eigenen Partitur erhalten – dort also die Vocal-Takte durch Pausen ersetzen.\n\n„Beispiel einsetzen“ lädt die Partitur zu „City Lights“ (siehe Beispiel beim Songtext).',
    abcExample: 'Beispiel einsetzen',
    abcNeedsPlanning: 'Eine eigene Partitur braucht eine Planung („Melodie und Akkorde“ oder „Nur Melodie“).',
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
    moreInfo: 'More',
    defaultValue: '{value} (default)',
    title: 'Title',
    titlePlaceholder: 'optional',
    titleHint: 'Names the folder and shows in the library. It has no effect on the music.',
    style: 'Style',
    stylePlaceholder: 'e.g. English, dark synthwave, male baritone, analog synths, 110 BPM',
    styleHint: 'Language, genre, voice, instruments, tempo and mood – as keywords, separated by commas.',
    styleMore:
      'The style shapes the sound, the arrangement and the singing. Concrete keywords work better than vague ones: “rounded bass and light drums” rather than “good band”. The YuE2 examples start with the language of the vocals.\n\nExamples:\n• English, warm piano pop, expressive female voice, acoustic piano, rounded bass and light drums, lyrical memorable melody, unhurried phrasing, 88 BPM\n• English, jazz-funk, warm lead vocal, Rhodes, bass and drums\n• German, dark synthwave, male baritone, analog synths, gated reverb drums, melancholic, 110 BPM',
    lyrics: 'Lyrics',
    lyricsOptional: 'Lyrics (optional)',
    lyricsPlaceholder: '[Verse]\n…\n\n[Chorus]\n…',
    lyricsHint: 'Mark sections with [Verse], [Chorus], [Bridge] …, one sung line per line.',
    lyricsMore:
      'The lyrics decide what is sung and, through their sections, the form of the song – and with it its length. Common tags: [Intro], [Verse], [Pre-Chorus], [Chorus], [Bridge], [Outro].\n\nExample (“City Lights” from the YuE2 examples):\n[Verse]\nNeon fades along the lane\nFootsteps keep the time of rain\nFold the night and leave it here\nMorning has a sky to clear\n\n[Chorus]\nLet the day come into view\nEvery road begins with you\nHold a little room for light\nWe will sing beyond the night',
    instrumental: 'Instrumental',
    instrumentalHint: 'No vocals: only the section tags of the lyrics still count.',
    instrumentalMore:
      'Puts “Instrumental, no vocals, no singing” in front of the style, keeps only tags such as [Verse] and [Chorus] from the lyrics and replaces the vocal voice of the planned score with rests. Without lyrics the worker uses [Intro] [Verse] [Chorus] [Outro]. This needs planning; “None” becomes “Melody and chords”.',
    quality: 'Quality',
    qualityDraft: 'Draft',
    qualityFull: 'Full',
    qualityHint: 'A draft is done in a few minutes and can be rendered at full quality later.',
    qualityMore:
      'Both compose the same song; the difference is in how the sound is synthesized.\n• Draft: 8 solver steps (adjustable under “Advanced”), on the GPU.\n• Full: 32 steps, on the Neural Engine by default, takes considerably longer.\n\n“Render full” in the library synthesizes a draft again from the same tokens and seed. A good habit: generate several drafts and render only the best one at full quality.',
    batch: 'Songs',
    batchHint: 'Several variations of the same song, each with its own seed.',
    batchMore:
      'Song 1 gets the seed, song 2 the seed + 1 and so on – same style and lyrics, different interpretations. Up to four songs (two on Macs with less than 24 GB) are composed together at hardly more cost than one; synthesis then runs song by song.',
    advanced: 'Advanced parameters',
    advancedChanged: 'changed',
    advancedIntro: 'Preset to the defaults; a normal song needs no changes here.',
    advancedReset: 'Defaults',
    cot: 'Planning',
    cotFull: 'Melody and chords',
    cotMelody: 'Melody only',
    cotOff: 'None',
    cotHint: 'Whether YuE2 writes a score before the audio for the song to follow.',
    cotMore:
      '• Melody and chords (default): first a score in ABC notation with melody and chords, then the song following it. The score can be downloaded from the library as ABC and edited.\n• Melody only: a score without chords, the accompaniment is free. Recommended with your own score for covers, so the arrangement adapts to the new style.\n• None: generates straight from style and lyrics. Skips the planning stage but produces no score.',
    seed: 'Seed',
    seedPlaceholder: 'empty = random',
    seedHint: 'Starting value of the randomness. Same seed and same inputs give (almost) the same song.',
    seedMore:
      'Useful for comparisons: take the seed of a song you like (the library shows it as #831001) and change only the style or one line of the lyrics – the rest stays comparable. Leave empty for a new random value. Results are not guaranteed identical across devices and software versions.',
    draftSteps: 'Draft steps',
    draftStepsHint: 'Solver steps of a draft’s sound synthesis, 1–32.',
    draftStepsFullHint: 'Full quality always uses 32 steps.',
    draftStepsMore:
      'More steps give a cleaner sound and take proportionally longer to synthesize. Melody and lyrics stay the same, only the sound quality changes.\n• 4: quickest preview\n• 8: default\n• 16: a better draft, about twice the synthesis time\n• 32: the same as “Full” with “GPU only”',
    engines: 'Engines',
    enginesAuto: 'Automatic',
    enginesGpu: 'GPU only',
    enginesAne: 'GPU and Neural Engine',
    enginesHint: 'Where the sound synthesis runs. Automatic: drafts on the GPU, full quality on the Neural Engine.',
    enginesMore:
      '• Automatic (default): drafts on the GPU, full quality on the Neural Engine – about twice as fast there for short songs.\n• GPU only: leaves the Neural Engine’s roughly 2.8 GB unmapped, which helps when memory is tight, e.g. while YuE Studio is running too.\n• GPU and Neural Engine: drafts may use the Neural Engine as well.\n\nA song the Neural Engine cannot take (a very long one, say) runs on the GPU.',
    maxLength: 'Maximum length',
    maxLengthHint: 'Upper limit for the song length; otherwise the song ends by itself once the lyrics are sung.',
    maxLengthMore:
      'How long a song gets follows from its lyrics and style. If it reaches the limit, it is cut off there. A short limit saves time when only the beginning matters, e.g. 1:00 to try out a style. YuE2 cannot go beyond 6:00 (9000 tokens, 25 per second).',
    abc: 'Your own score (ABC)',
    abcPlaceholder: 'X:1\nM:4/4\nL:1/16\nQ:1/4=88\nK:C\n…',
    abcHint: 'Replaces the planning with your own score: melody, chords, tempo and form are then fixed.',
    abcMore:
      'For example the ABC file of a song from the library with changed chords or a different tempo (Q:), or a melody transcribed with SheetSage2 for a cover. Needs the planning “Melody and chords” (the chords are kept) or “Melody only” with a score without chord symbols (the accompaniment is free). The syllables of the lyrics should match the notes of the “Vocal” voice. With “Instrumental” the vocal voice of your own score is kept – replace its bars with rests there.\n\n“Insert example” loads the score of “City Lights” (see the example under lyrics).',
    abcExample: 'Insert example',
    abcNeedsPlanning: 'Your own score needs planning (“Melody and chords” or “Melody only”).',
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
