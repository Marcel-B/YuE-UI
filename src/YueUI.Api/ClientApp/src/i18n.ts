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
    notificationsOn: 'Benachrichtigungen an – antippen zum Ausschalten',
    notificationsOff: 'Benachrichtigen, wenn etwas fertig ist',
    notificationsEnabled: 'Benachrichtigungen sind an. Eine Test-Nachricht ist unterwegs.',
    notificationsDisabled: 'Benachrichtigungen sind aus.',
    notificationsInstall:
      'Auf iPhone und iPad gibt es Benachrichtigungen nur für die App auf dem Home-Bildschirm: Teilen → „Zum Home-Bildschirm“, dann von dort öffnen.',
    notificationsBlocked:
      'Benachrichtigungen sind für diese Seite blockiert. Das lässt sich nur in den Einstellungen des Systems oder Browsers wieder erlauben.',
    notificationsUnsupported: 'Dieser Browser kann keine Benachrichtigungen empfangen.',
    notificationsFailed: 'Benachrichtigungen ließen sich nicht einrichten: {message}',

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
    styleBlocks: 'Bausteine',
    styleBlocksIntro: 'Antippen setzt einen Baustein in den Stil, nochmal Antippen nimmt ihn heraus.',
    styleBlocksMore:
      'Eigene Begriffe im Stil bleiben stehen. Nach YuEs Prompt-Leitfaden wirken Genre, Instrument, Stimmung, Stimme und Klangfarbe am stabilsten, am besten alle fünf. Die Auswahl stammt aus YuEs Liste der 200 häufigsten Tags (top_200_tags.json); YuE2 versteht aber auch freie Beschreibungen wie „rounded bass and light drums“. Sprache kommt nach vorn, Tempo ans Ende, der Rest in der Reihenfolge der Reiter dazwischen.',
    styleBlocksInstrumental: 'Bei „Instrumental“ ohne Wirkung.',
    styleBlocks_language: 'Sprache',
    styleBlocks_languageHint:
      'Die Sprache des Gesangs, eine pro Song; sie steht vorn wie in den YuE2-Beispielen. Englisch ist am besten trainiert, Mandarin und Kantonesisch unterscheidet YuE ausdrücklich.',
    styleBlocks_genre: 'Genre',
    styleBlocks_genreHint: 'Mehrere lassen sich mischen, zwei bis drei bleiben meist stimmig.',
    styleBlocks_voice: 'Stimme',
    styleBlocks_voiceHint:
      'Wer singt, eine Wahl pro Song. „male and female duet“ und „choir“ sind freie Beschreibungen, keine Tags aus YuEs Liste: Wer welche Zeile singt, lässt sich nicht festlegen.',
    styleBlocks_timbre: 'Klangfarbe',
    styleBlocks_timbreHint:
      'Wie die Stimme klingt, auch die Stimmlage (soprano, alto, tenor, baritone). Ein bis zwei genügen.',
    styleBlocks_instruments: 'Instrumente',
    styleBlocks_instrumentsHint: 'Was die Begleitung spielt; drei bis vier prägen das Arrangement deutlich.',
    styleBlocks_mood: 'Stimmung',
    styleBlocks_moodHint: 'Die Stimmung färbt Melodie, Harmonie und Gesang.',
    styleBlocks_tempo: 'Tempo',
    styleBlocks_tempoHint:
      'Eines pro Song, es steht hinten wie in den YuE2-Beispielen und ersetzt ein von Hand geschriebenes. Ballade etwa 60–80, Pop 100–120, Dance 120–130 BPM.',
    lyrics: 'Songtext',
    lyricsOptional: 'Songtext (optional)',
    lyricsPlaceholder: '[Verse]\n…\n\n[Chorus]\n…',
    lyricsHint: 'Abschnitte mit [Verse], [Chorus], [Bridge] … markieren, eine gesungene Zeile pro Zeile.',
    lyricsMore:
      'Der Text bestimmt, was gesungen wird, und über seine Abschnitte die Form des Songs – und damit auch seine Länge. Übliche Marken: [Intro], [Verse], [Pre-Chorus], [Chorus], [Bridge], [Outro].\n\nBeispiel („City Lights“ aus den YuE2-Beispielen):\n[Verse]\nNeon fades along the lane\nFootsteps keep the time of rain\nFold the night and leave it here\nMorning has a sky to clear\n\n[Chorus]\nLet the day come into view\nEvery road begins with you\nHold a little room for light\nWe will sing beyond the night',
    lyricsIdea: 'Worum geht es?',
    lyricsIdeaPlaceholder: 'z. B. Nachtzug, Abschied von zu Hause, Hoffnung',
    lyricsIdeaHint:
      'Stichwörter oder ein Satz – ein Sprachmodell auf dem Mac schreibt daraus einen Songtext, auf Englisch oder Deutsch.',
    lyricsIdeaMore:
      'Der Entwurf entsteht in LM Studio auf dem Mac, mit dem Modell aus „Lyrics“ in appsettings.json (Standard: Gemma 4 E4B); LM Studio startet bei Bedarf von selbst. Der Stil fließt mit ein, damit Stimmung und Tempo passen. Sprachmodell und YuE2 passen nicht gleichzeitig in den Speicher: Während YuE2 rechnet, geht es deshalb nicht, ein ruhender Worker wird vorher beendet, und das Modell wird gleich danach wieder entladen. Durch das Laden dauert ein Entwurf etwas. Der Text landet im Songtext-Feld und lässt sich dort weiter bearbeiten.\n\nEN oder DE wählt die Sprache des Entwurfs; die Abschnittsmarken wie [Verse] bleiben englisch, weil YuE2 sie so liest. Damit YuE2 deutsch singt, gehört „German“ an den Anfang des Stils (Bausteine → Sprache).',
    lyricsLanguage: 'Sprache des Entwurfs',
    lyricsLanguageEnglish: 'EN',
    lyricsLanguageGerman: 'DE',
    lyricsDraftedGerman: 'Entwurf eingesetzt. Setz noch „German“ in den Stil, damit YuE2 deutsch singt.',
    draftLyrics: 'Text entwerfen',
    draftingLyrics: 'Schreibt …',
    draftBusy: 'Während YuE2 rechnet, ist kein Platz für das Sprachmodell.',
    confirmReplaceLyrics: 'Den vorhandenen Songtext durch einen neuen Entwurf ersetzen?',
    replaceLyrics: 'Songtext ersetzen',
    replace: 'Ersetzen',
    lyricsDrafted: 'Entwurf eingesetzt – am besten einmal durchlesen.',
    instrumental: 'Instrumental',
    instrumentalHint: 'Ohne Gesang: vom Text zählen nur noch die Abschnittsmarken.',
    instrumentalMore:
      'Setzt „Instrumental, no vocals, no singing“ vor den Stil, behält vom Text nur die Marken wie [Verse] und [Chorus] und ersetzt die Gesangsstimme der geplanten Partitur durch Pausen. Ohne Text nimmt der Worker [Intro] [Verse] [Chorus] [Outro]. Das braucht eine Planung; „Keine“ wird dann zu „Melodie und Akkorde“.',
    quality: 'Qualität',
    qualityDraft: 'Entwurf',
    qualityFull: 'Voll',
    qualityHint: 'Ein Entwurf ist in wenigen Minuten fertig und lässt sich später in voller Qualität rendern.',
    qualityMore:
      'Beide komponieren denselben Song; der Unterschied liegt in der Synthese des Klangs.\n• Entwurf: 8 Solver-Schritte, auf der GPU.\n• Voll: 32 Schritte, standardmäßig auf der Neural Engine, dauert deutlich länger.\n\n„Voll rendern“ in der Bibliothek synthetisiert einen Entwurf mit denselben Tokens und demselben Seed neu. Bewährt: mehrere Entwürfe erzeugen und nur den besten voll rendern. Die Schrittzahl beider lässt sich unter „Erweiterte Parameter“ ändern.',
    batch: 'Anzahl',
    batchHint: 'Mehrere Varianten desselben Songs, jede mit eigenem Seed.',
    batchMore:
      'Song 1 bekommt den Seed, Song 2 den Seed + 1 usw. – gleicher Stil und Text, verschiedene Interpretationen. Bis zu vier Songs (zwei auf Macs mit weniger als 24 GB) werden gemeinsam komponiert und kosten dabei kaum mehr Zeit als einer; die Synthese läuft danach Song für Song.',
    advanced: 'Erweiterte Parameter',
    advancedChanged: 'angepasst',
    advancedIntro:
      'Mit den Standardwerten vorbelegt; für einen normalen Song muss hier nichts geändert werden. Das Zurücksetzen lässt eine eigene Partitur samt ihrer Planung stehen.',
    advancedReset: 'Auf Standardwerte zurücksetzen',
    advancedAtDefaults: 'Alles auf Standardwerten',
    advancedWithScore: 'mit Partitur',
    samplingReset: 'Diese Gruppe zurücksetzen',
    samplingAtDefaults: 'Auf Standardwerten',
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
    stepsDraft: 'Schritte (Entwurf)',
    stepsFull: 'Schritte (Voll)',
    draftStepsHint: 'Solver-Schritte der Klangsynthese eines Entwurfs, 1–32 (Standard 8).',
    fullStepsHint: 'Solver-Schritte der Klangsynthese bei voller Qualität, 1–64 (Standard 32).',
    stepsMore:
      'Mehr Schritte ergeben einen saubereren Klang, die Synthese dauert entsprechend länger. Melodie und Text ändern sich nicht, nur die Klangqualität.\n\nEntwurf:\n• 4: schnellste Hörprobe\n• 8: Standard\n• 16: besserer Entwurf, etwa doppelt so lange Synthese\n• 32: entspricht „Voll“ mit „Nur GPU“\n\nVoll:\n• 32: Standard des Modells\n• 48: anderthalbfache Synthesezeit; ob man den Unterschied hört, ist nicht belegt\n• 16: spart die Hälfte der Synthesezeit\n\n„Voll rendern“ in der Bibliothek nutzt immer 32.',
    engines: 'Rechenwerke',
    enginesAuto: 'Automatisch',
    enginesGpu: 'Nur GPU',
    enginesAne: 'GPU und Neural Engine',
    enginesHint: 'Wo die Klangsynthese läuft. Automatisch: Entwurf auf der GPU, voll auf der Neural Engine.',
    enginesMore:
      '• Automatisch (Standard): Entwürfe auf der GPU, volle Qualität auf der Neural Engine – dort bei kurzen Songs etwa doppelt so schnell.\n• Nur GPU: lässt die rund 2,8 GB der Neural Engine frei, hilfreich bei knappem Speicher, z. B. wenn YuE Studio zugleich läuft.\n• GPU und Neural Engine: auch Entwürfe dürfen auf die Neural Engine.\n\nKann die Neural Engine einen Song nicht übernehmen (etwa einen sehr langen), läuft er auf der GPU.',
    maxLength: 'Maximale Länge',
    maxLengthHint:
      'Obergrenze für die Songlänge; der Song endet sonst von selbst, wenn der Text gesungen ist. Über 6:00 ist Neuland für das Modell.',
    maxLengthMore:
      'Wie lang ein Song wird, ergibt sich aus Text und Stil. Erreicht er die Grenze, wird er dort abgeschnitten. Eine kurze Grenze spart Zeit, wenn nur der Anfang interessiert, z. B. 1:00 zum Ausprobieren eines Stils.\n\nStandard des Modells sind 6:00 (9000 Tokens, 25 pro Sekunde). Bis 10:00 geht es über die YueUI-Erweiterung des Workers; mehr passt nicht in den Kontext des Modells, den sich Prompt, Partitur und Song teilen (24576 Tokens). Über 6:00 ist ungetestet: Das Modell muss dafür einen längeren Text auch wirklich ausfüllen, und die Neural Engine übernimmt Songs über etwa 8 Minuten nicht mehr, sie laufen dann auf der GPU.',
    abc: 'Eigene Partitur (ABC)',
    abcPlaceholder: 'X:1\nM:4/4\nL:1/16\nQ:1/4=88\nK:C\n…',
    abcHint: 'Ersetzt die Planung durch eine eigene Partitur: Melodie, Akkorde, Tempo und Form stehen dann fest.',
    abcMore:
      'Zum Beispiel die ABC-Datei eines Songs aus der Bibliothek mit geänderten Akkorden oder einem anderen Tempo (Q:), oder eine mit SheetSage2 transkribierte Melodie für ein Cover. Braucht die Planung „Melodie und Akkorde“ (Akkorde werden übernommen) oder „Nur Melodie“ mit einer Partitur ohne Akkordsymbole (die Begleitung ist frei). Die Silben des Textes sollten zu den Noten der Stimme „Vocal“ passen. Bei „Instrumental“ bleibt die Gesangsstimme einer eigenen Partitur erhalten – dort also die Vocal-Takte durch Pausen ersetzen.\n\n„Beispiel einsetzen“ lädt die Partitur zu „City Lights“ (siehe Beispiel beim Songtext).',
    abcExample: 'Beispiel einsetzen',
    abcClear: 'Partitur entfernen',
    samplingSemantic: 'Sampling: Song',
    samplingSemanticIntro:
      'Steuert, wie die Song-Tokens gezogen werden – also Klang, Arrangement und Gesang. Vorbelegt mit den Werten des Modells; laut YuE2-Dokumentation kann jede Änderung die Qualität verändern. Ein Auftrag mit geändertem Sampling wird nicht mit anderen Aufträgen zusammen komponiert.',
    samplingAbc: 'Sampling: Partitur',
    samplingAbcIntro:
      'Steuert, wie die Partitur mit Melodie und Akkorden geschrieben wird. Die Werte des Modells sind hier vorsichtiger als beim Song.',
    samplingAbcInactive: 'Gerade ohne Wirkung: Ohne Planung oder mit eigener Partitur schreibt YuE2 keine Partitur.',
    samplingDefault: 'Standard: {value}',
    temperature: 'Temperatur',
    temperatureHint: 'Wie mutig gezogen wird: niedriger = vorhersehbarer, höher = überraschender.',
    temperatureMore:
      'Teilt die Wahrscheinlichkeiten vor dem Ziehen. Unter 1 werden wahrscheinliche Tokens noch wahrscheinlicher (vorhersehbarer, gleichförmiger), über 1 bekommen seltene mehr Chancen (überraschender, aber fehleranfälliger). 0 nimmt immer das wahrscheinlichste Token. Erlaubt: 0–5.\n\nBeispiele:\n• Song 0.9: konservativer als der Standard 1.0\n• Partitur 0.9: ungewöhnlichere Melodien und Akkorde als mit 0.7',
    topP: 'top_p',
    topPHint: 'Nur die wahrscheinlichsten Tokens, bis zusammen dieser Anteil erreicht ist.',
    topPMore:
      'Nucleus-Sampling: Pro Schritt kommen nur die wahrscheinlichsten Tokens in Frage, bis ihre Wahrscheinlichkeit zusammen diesen Anteil erreicht. Kleiner = nur die sichersten Kandidaten, 1 = keine Einschränkung. Erlaubt: über 0 bis 1.\n\nBeispiel: 0.8 schneidet mehr unwahrscheinliche Wendungen ab als 0.95.',
    topK: 'top_k',
    topKHint: 'Höchstens so viele Kandidaten pro Schritt.',
    topKMore:
      'Pro Schritt kommen höchstens die k wahrscheinlichsten Tokens in Frage; top_p schränkt danach weiter ein. Kleiner = sicherer und gleichförmiger, größer = mehr Vielfalt. Erlaubt: 1–1000.\n\nBeispiele: 1 nimmt praktisch immer das wahrscheinlichste Token, 300 lässt beim Song deutlich mehr zu als 100.',
    repetitionPenalty: 'Wiederholungsstrafe',
    repetitionPenaltyHint: 'Über 1 dämpft Wiederholungen, 1 = aus.',
    repetitionPenaltyMore:
      'Macht Tokens unwahrscheinlicher, je öfter sie im Fenster der letzten Tokens schon vorkamen. Über 1 dämpft Wiederholungen wie hängende Töne oder Schleifen; zu hoch zwingt ständig zu Neuem und kann zerfahren klingen. Unter 1 fördert Wiederholungen. Erlaubt: über 0 bis 5.\n\nBeispiele: Song 1.3 gegen hörbare Loops, 1.1 wenn Motive stärker wiederkehren dürfen.',
    penaltyWindow: 'Strafenfenster',
    penaltyWindowHint: 'Wie viele der letzten Tokens die Wiederholungsstrafe betrachtet, 1–100.',
    penaltyWindowMore:
      'Größer = Wiederholungen werden über längere Strecken bestraft. Beim Song sind 50 Tokens 2 Sekunden Audio, bei der Partitur umfassen 100 Tokens einige Takte ABC.',
    extensionsOff:
      'Der Worker läuft ohne die YueUI-Erweiterung, vermutlich weil ein Update von YuE Studio den Worker geändert hat. Sampling, Schritte bei voller Qualität und Längen über 6:00 haben dann keine Wirkung. Details stehen im Protokoll.',
    abcNeedsPlanning: 'Eine eigene Partitur braucht eine Planung („Melodie und Akkorde“ oder „Nur Melodie“).',
    generate: 'Erzeugen',
    generating: 'Wird gesendet …',
    queued: 'In der Warteschlange. Der Fortschritt steht unten.',
    resetForm: 'Leeren',

    transcription: 'Transkription (SheetSage2)',
    transcriptionIntro:
      'Macht aus einer Aufnahme eine Melodie-Partitur (ABC), die sich als eigene Partitur für einen neuen Song nutzen lässt, etwa für ein Cover.',
    transcriptionMore:
      'SheetSage2 erkennt Takt, Tonart, Form und Melodie einer Aufnahme und schreibt sie als ABC in dem Format, das YuE2 versteht (Stimmen „Vocal“ und „Ins“). Akkorde lässt es weg, damit sich die Begleitung dem neuen Stil anpasst. Es läuft auf der CPU und braucht einige Minuten; rechnet gleichzeitig ein Song, wird beides langsamer.\n\nFür ein Cover: „Als Partitur übernehmen“ (stellt die Planung auf „Nur Melodie“), dann einen neuen Stil und einen Text schreiben, dessen Silben zur Melodie passen.\n\nDie Gewichte von SheetSage2 stehen unter CC BY-NC 4.0 (nicht kommerziell).',
    transcriptionNotInstalled:
      'SheetSage2 ist noch nicht installiert. Einmal in YuE Studio „Transcribe recording“ öffnen und „Install transcription support“ wählen (etwa 2 GB); danach geht es auch hier.',
    recording: 'Aufnahme',
    recordingHint: 'Jedes Format, das macOS lesen kann: MP3, M4A/AAC, WAV, AIFF, FLAC …, bis 300 MB.',
    transcriptionTask: 'Melodie',
    taskFull: 'Gesang und Instrumente',
    taskVocal: 'Nur Gesang',
    taskHint: 'Welche Melodien in die Partitur kommen.',
    taskMore:
      '• Gesang und Instrumente (Standard): die Gesangsmelodie in der Stimme „Vocal“ und die Instrumentalmelodie in „Ins“.\n• Nur Gesang: nur die Gesangsmelodie, wenn für das Cover allein sie zählt.',
    transcribe: 'Transkribieren',
    uploading: 'Wird hochgeladen …',
    transcriptionStarted: 'Läuft. Das dauert einige Minuten; die Seite darf dabei zu sein.',
    transcriptionStage_starting: 'Startet',
    transcriptionStage_progress: 'Transkribiert',
    transcriptionStage_done: 'Fertig',
    transcriptionStage_failed: 'Fehlgeschlagen',
    transcriptionStage_cancelled: 'Abgebrochen',
    transcriptions: 'Fertige Transkriptionen',
    transcriptionsEmpty: 'Noch keine.',
    useScore: 'Als Partitur übernehmen',
    scoreApplied: 'Die Partitur von „{name}“ steht unter „Erweiterte Parameter“, die Planung auf „Nur Melodie“.',
    showScore: 'ABC ansehen',
    files: 'Alle Dateien',
    filesTitle: 'Partitur, MIDI-Spuren und Analyse als ZIP',
    deleteTranscription: 'Transkription löschen',
    confirmDeleteTranscription: 'Die Transkription von „{name}“ mit Partitur und MIDI-Dateien endgültig löschen?',
    warnings: 'Hinweise von SheetSage2: {list}',

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
    songScoreApplied:
      'Stil, Text, Seed und Partitur von „{title}“, {song}, stehen im Formular, die Planung auf „{planning}“. Die Partitur lässt sich unter „Erweiterte Parameter“ bearbeiten.',
    renderFull: 'Voll rendern',
    rendering: 'In Arbeit',
    download: 'FLAC',
    score: 'ABC',
    zip: 'ZIP',
    zipTitle: 'FLAC und ABC als ZIP',
    zipRun: 'Alle als ZIP',
    openInLogic: 'Als Logic-Projekt laden',
    logicWarnings: 'Logic-Projekt geladen, mit Hinweisen: {messages}',
    noAudio: 'Kein Audio',
    showLyrics: 'Text',
    untitled: 'Ohne Titel',
    storage: '{used} belegt, {free} frei',
    deleteSong: 'Song löschen',
    deleteRun: 'Alle Songs löschen',
    deleteBusy: 'Der Worker arbeitet noch daran.',
    confirmDeleteSong: '{song} von „{title}“ ({size}) endgültig löschen? Audio, Partitur und Tokens sind danach weg.',
    confirmDeleteLastSong:
      '{song} von „{title}“ ({size}) endgültig löschen? Es ist der letzte Song, der Eintrag verschwindet damit aus der Bibliothek.',
    confirmDeleteRun: 'Alle {count} Songs von „{title}“ ({size}) endgültig löschen?',
    confirmDelete: 'Löschen bestätigen',
    delete: 'Löschen',
    keep: 'Behalten',
    deleted: '„{title}“ ist gelöscht.',

    menuCreate: 'Erstellen',
    menuSongs: 'Titel',
    menuPlaylist: 'Playlist',
    menu: 'Menü',
    playlist: 'Playlist',
    playlistEmpty: 'Die Playlist ist leer. Unter „Titel“ fügt das Plus neben einem Song ihn hinzu.',
    playlistSummary: '{count} Songs, {duration}',
    playAll: 'Alle abspielen',
    play: 'Abspielen',
    pause: 'Pause',
    nextTrack: 'Nächster Titel',
    previousTrack: 'Vorheriger Titel',
    closePlayer: 'Player schließen',
    addToPlaylist: 'Zur Playlist hinzufügen',
    removeFromPlaylist: 'Aus der Playlist entfernen',
    moveUp: 'Nach oben',
    moveDown: 'Nach unten',

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
    notificationsOn: 'Notifications on – tap to switch off',
    notificationsOff: 'Notify me when something finishes',
    notificationsEnabled: 'Notifications are on. A test notification is on its way.',
    notificationsDisabled: 'Notifications are off.',
    notificationsInstall:
      'On iPhone and iPad, only the app on the home screen gets notifications: Share → “Add to Home Screen”, then open it from there.',
    notificationsBlocked:
      'Notifications are blocked for this site. Only the system or browser settings can allow them again.',
    notificationsUnsupported: 'This browser cannot receive notifications.',
    notificationsFailed: 'Notifications could not be set up: {message}',

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
    styleBlocks: 'Building blocks',
    styleBlocksIntro: 'Tap to put a building block into the style, tap again to take it out.',
    styleBlocksMore:
      'Your own words in the style stay. According to YuE’s prompt guide, genre, instrument, mood, voice and timbre give the most stable results, ideally all five. The choice comes from YuE’s list of its 200 most common tags (top_200_tags.json); YuE2 also understands free descriptions such as “rounded bass and light drums”. Language goes first, tempo last, the rest in between in the order of the tabs.',
    styleBlocksInstrumental: 'No effect with “Instrumental”.',
    styleBlocks_language: 'Language',
    styleBlocks_languageHint:
      'The language of the vocals, one per song; it goes first as in the YuE2 examples. English is trained best, and YuE tells Mandarin and Cantonese apart explicitly.',
    styleBlocks_genre: 'Genre',
    styleBlocks_genreHint: 'Several can be mixed, two or three usually stay coherent.',
    styleBlocks_voice: 'Voice',
    styleBlocks_voiceHint:
      'Who sings, one choice per song. “male and female duet” and “choir” are free descriptions, not tags from YuE’s list: which line goes to whom cannot be set.',
    styleBlocks_timbre: 'Timbre',
    styleBlocks_timbreHint:
      'How the voice sounds, including its range (soprano, alto, tenor, baritone). One or two are enough.',
    styleBlocks_instruments: 'Instruments',
    styleBlocks_instrumentsHint: 'What the accompaniment plays; three or four shape the arrangement clearly.',
    styleBlocks_mood: 'Mood',
    styleBlocks_moodHint: 'The mood colours melody, harmony and singing.',
    styleBlocks_tempo: 'Tempo',
    styleBlocks_tempoHint:
      'One per song; it goes last as in the YuE2 examples and replaces one typed by hand. Ballad about 60–80, pop 100–120, dance 120–130 BPM.',
    lyrics: 'Lyrics',
    lyricsOptional: 'Lyrics (optional)',
    lyricsPlaceholder: '[Verse]\n…\n\n[Chorus]\n…',
    lyricsHint: 'Mark sections with [Verse], [Chorus], [Bridge] …, one sung line per line.',
    lyricsMore:
      'The lyrics decide what is sung and, through their sections, the form of the song – and with it its length. Common tags: [Intro], [Verse], [Pre-Chorus], [Chorus], [Bridge], [Outro].\n\nExample (“City Lights” from the YuE2 examples):\n[Verse]\nNeon fades along the lane\nFootsteps keep the time of rain\nFold the night and leave it here\nMorning has a sky to clear\n\n[Chorus]\nLet the day come into view\nEvery road begins with you\nHold a little room for light\nWe will sing beyond the night',
    lyricsIdea: 'What is it about?',
    lyricsIdeaPlaceholder: 'e.g. night train, leaving home, hope',
    lyricsIdeaHint:
      'Keywords or a sentence – a language model on the Mac writes lyrics from them, in English or German.',
    lyricsIdeaMore:
      'The draft is written in LM Studio on the Mac, with the model set under “Lyrics” in appsettings.json (default: Gemma 4 E4B); LM Studio starts by itself when needed. The style is passed along so that mood and pace fit. The language model and YuE2 do not fit into memory together: so it cannot run while YuE2 is generating, an idle worker is stopped first, and the model is unloaded right after. Loading it makes a draft take a moment. The lyrics land in the lyrics field, where they can be edited.\n\nEN or DE picks the language of the draft; section tags such as [Verse] stay English, since that is how YuE2 reads them. For YuE2 to sing in German, put “German” at the start of the style (Building blocks → Language).',
    lyricsLanguage: 'Language of the draft',
    lyricsLanguageEnglish: 'EN',
    lyricsLanguageGerman: 'DE',
    lyricsDraftedGerman: 'Draft inserted. Add “German” to the style so that YuE2 sings it in German.',
    draftLyrics: 'Draft lyrics',
    draftingLyrics: 'Writing …',
    draftBusy: 'While YuE2 is generating there is no room for the language model.',
    confirmReplaceLyrics: 'Replace the current lyrics with a new draft?',
    replaceLyrics: 'Replace lyrics',
    replace: 'Replace',
    lyricsDrafted: 'Draft inserted – worth a read-through.',
    instrumental: 'Instrumental',
    instrumentalHint: 'No vocals: only the section tags of the lyrics still count.',
    instrumentalMore:
      'Puts “Instrumental, no vocals, no singing” in front of the style, keeps only tags such as [Verse] and [Chorus] from the lyrics and replaces the vocal voice of the planned score with rests. Without lyrics the worker uses [Intro] [Verse] [Chorus] [Outro]. This needs planning; “None” becomes “Melody and chords”.',
    quality: 'Quality',
    qualityDraft: 'Draft',
    qualityFull: 'Full',
    qualityHint: 'A draft is done in a few minutes and can be rendered at full quality later.',
    qualityMore:
      'Both compose the same song; the difference is in how the sound is synthesized.\n• Draft: 8 solver steps, on the GPU.\n• Full: 32 steps, on the Neural Engine by default, takes considerably longer.\n\n“Render full” in the library synthesizes a draft again from the same tokens and seed. A good habit: generate several drafts and render only the best one at full quality. Both step counts can be changed under “Advanced parameters”.',
    batch: 'Songs',
    batchHint: 'Several variations of the same song, each with its own seed.',
    batchMore:
      'Song 1 gets the seed, song 2 the seed + 1 and so on – same style and lyrics, different interpretations. Up to four songs (two on Macs with less than 24 GB) are composed together at hardly more cost than one; synthesis then runs song by song.',
    advanced: 'Advanced parameters',
    advancedChanged: 'changed',
    advancedIntro:
      'Preset to the defaults; a normal song needs no changes here. Resetting leaves your own score and its planning in place.',
    advancedReset: 'Reset to defaults',
    advancedAtDefaults: 'All at defaults',
    advancedWithScore: 'with score',
    samplingReset: 'Reset this group',
    samplingAtDefaults: 'At defaults',
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
    stepsDraft: 'Steps (draft)',
    stepsFull: 'Steps (full)',
    draftStepsHint: 'Solver steps of a draft’s sound synthesis, 1–32 (default 8).',
    fullStepsHint: 'Solver steps of the sound synthesis at full quality, 1–64 (default 32).',
    stepsMore:
      'More steps give a cleaner sound and take proportionally longer to synthesize. Melody and lyrics stay the same, only the sound quality changes.\n\nDraft:\n• 4: quickest preview\n• 8: default\n• 16: a better draft, about twice the synthesis time\n• 32: the same as “Full” with “GPU only”\n\nFull:\n• 32: the model’s default\n• 48: one and a half times the synthesis time; whether the difference is audible is not documented\n• 16: saves half the synthesis time\n\n“Render full” in the library always uses 32.',
    engines: 'Engines',
    enginesAuto: 'Automatic',
    enginesGpu: 'GPU only',
    enginesAne: 'GPU and Neural Engine',
    enginesHint: 'Where the sound synthesis runs. Automatic: drafts on the GPU, full quality on the Neural Engine.',
    enginesMore:
      '• Automatic (default): drafts on the GPU, full quality on the Neural Engine – about twice as fast there for short songs.\n• GPU only: leaves the Neural Engine’s roughly 2.8 GB unmapped, which helps when memory is tight, e.g. while YuE Studio is running too.\n• GPU and Neural Engine: drafts may use the Neural Engine as well.\n\nA song the Neural Engine cannot take (a very long one, say) runs on the GPU.',
    maxLength: 'Maximum length',
    maxLengthHint:
      'Upper limit for the song length; otherwise the song ends by itself once the lyrics are sung. Beyond 6:00 is new ground for the model.',
    maxLengthMore:
      'How long a song gets follows from its lyrics and style. If it reaches the limit, it is cut off there. A short limit saves time when only the beginning matters, e.g. 1:00 to try out a style.\n\nThe model’s default is 6:00 (9000 tokens, 25 per second). Up to 10:00 works through YueUI’s worker extension; more does not fit the model’s context, which prompt, score and song share (24576 tokens). Beyond 6:00 is untested: the model has to actually fill it with longer lyrics, and the Neural Engine no longer takes songs over about 8 minutes, so they run on the GPU.',
    abc: 'Your own score (ABC)',
    abcPlaceholder: 'X:1\nM:4/4\nL:1/16\nQ:1/4=88\nK:C\n…',
    abcHint: 'Replaces the planning with your own score: melody, chords, tempo and form are then fixed.',
    abcMore:
      'For example the ABC file of a song from the library with changed chords or a different tempo (Q:), or a melody transcribed with SheetSage2 for a cover. Needs the planning “Melody and chords” (the chords are kept) or “Melody only” with a score without chord symbols (the accompaniment is free). The syllables of the lyrics should match the notes of the “Vocal” voice. With “Instrumental” the vocal voice of your own score is kept – replace its bars with rests there.\n\n“Insert example” loads the score of “City Lights” (see the example under lyrics).',
    abcExample: 'Insert example',
    abcClear: 'Remove score',
    samplingSemantic: 'Sampling: song',
    samplingSemanticIntro:
      'Controls how the song tokens are drawn – the sound, arrangement and singing. Preset to the model’s values; according to the YuE2 documentation any change may change the quality. A job with changed sampling is not composed together with other jobs.',
    samplingAbc: 'Sampling: score',
    samplingAbcIntro:
      'Controls how the score with melody and chords is written. The model’s values are more cautious here than for the song.',
    samplingAbcInactive: 'No effect right now: without planning or with your own score, YuE2 writes no score.',
    samplingDefault: 'Default: {value}',
    temperature: 'Temperature',
    temperatureHint: 'How boldly tokens are drawn: lower = more predictable, higher = more surprising.',
    temperatureMore:
      'Scales the probabilities before drawing. Below 1 likely tokens get even likelier (more predictable, more uniform), above 1 rare ones get more of a chance (more surprising, but more error-prone). 0 always takes the likeliest token. Allowed: 0–5.\n\nExamples:\n• Song 0.9: more conservative than the default 1.0\n• Score 0.9: more unusual melodies and chords than with 0.7',
    topP: 'top_p',
    topPHint: 'Only the likeliest tokens, until together they reach this share.',
    topPMore:
      'Nucleus sampling: at each step only the likeliest tokens qualify, until their probabilities add up to this share. Smaller = only the safest candidates, 1 = no restriction. Allowed: above 0 up to 1.\n\nExample: 0.8 cuts off more unlikely turns than 0.95.',
    topK: 'top_k',
    topKHint: 'At most this many candidates per step.',
    topKMore:
      'At each step at most the k likeliest tokens qualify; top_p narrows them further. Smaller = safer and more uniform, larger = more variety. Allowed: 1–1000.\n\nExamples: 1 practically always takes the likeliest token, 300 allows considerably more for the song than 100.',
    repetitionPenalty: 'Repetition penalty',
    repetitionPenaltyHint: 'Above 1 dampens repetition, 1 = off.',
    repetitionPenaltyMore:
      'Makes tokens less likely the more often they already occurred within the window of recent tokens. Above 1 dampens repetition such as stuck notes or loops; too high forces constant novelty and can sound disjointed. Below 1 encourages repetition. Allowed: above 0 up to 5.\n\nExamples: song 1.3 against audible loops, 1.1 when motifs may return more often.',
    penaltyWindow: 'Penalty window',
    penaltyWindowHint: 'How many recent tokens the repetition penalty looks at, 1–100.',
    penaltyWindowMore:
      'Larger = repetition is penalized over longer stretches. For the song 50 tokens are 2 seconds of audio; for the score 100 tokens span a few bars of ABC.',
    extensionsOff:
      'The worker runs without the YueUI extension, probably because a YuE Studio update changed the worker. Sampling, steps at full quality and lengths over 6:00 then have no effect. The log has the details.',
    abcNeedsPlanning: 'Your own score needs planning (“Melody and chords” or “Melody only”).',
    generate: 'Generate',
    generating: 'Sending …',
    queued: 'Queued. Progress is shown below.',
    resetForm: 'Clear',

    transcription: 'Transcription (SheetSage2)',
    transcriptionIntro:
      'Turns a recording into a melody score (ABC) that can serve as your own score for a new song, for a cover, say.',
    transcriptionMore:
      'SheetSage2 recognizes the meter, key, form and melody of a recording and writes them as ABC in the format YuE2 reads (voices “Vocal” and “Ins”). It leaves out chords so the accompaniment can adapt to the new style. It runs on the CPU and takes a few minutes; if a song is generating at the same time, both get slower.\n\nFor a cover: “Use as score” (sets planning to “Melody only”), then write a new style and lyrics whose syllables fit the melody.\n\nSheetSage2’s weights are licensed CC BY-NC 4.0 (non-commercial).',
    transcriptionNotInstalled:
      'SheetSage2 is not installed yet. Open “Transcribe recording” in YuE Studio once and choose “Install transcription support” (about 2 GB); it then works here as well.',
    recording: 'Recording',
    recordingHint: 'Any format macOS can read: MP3, M4A/AAC, WAV, AIFF, FLAC …, up to 300 MB.',
    transcriptionTask: 'Melody',
    taskFull: 'Vocals and instruments',
    taskVocal: 'Vocals only',
    taskHint: 'Which melodies go into the score.',
    taskMore:
      '• Vocals and instruments (default): the sung melody in the “Vocal” voice and the instrumental melody in “Ins”.\n• Vocals only: just the sung melody, when that is all that matters for the cover.',
    transcribe: 'Transcribe',
    uploading: 'Uploading …',
    transcriptionStarted: 'Running. This takes a few minutes; the page may be closed meanwhile.',
    transcriptionStage_starting: 'Starting',
    transcriptionStage_progress: 'Transcribing',
    transcriptionStage_done: 'Done',
    transcriptionStage_failed: 'Failed',
    transcriptionStage_cancelled: 'Cancelled',
    transcriptions: 'Finished transcriptions',
    transcriptionsEmpty: 'None yet.',
    useScore: 'Use as score',
    scoreApplied: 'The score of “{name}” is under “Advanced parameters”, planning is set to “Melody only”.',
    showScore: 'View ABC',
    files: 'All files',
    filesTitle: 'Score, MIDI parts and analysis as ZIP',
    deleteTranscription: 'Delete transcription',
    confirmDeleteTranscription: 'Delete the transcription of “{name}” with its score and MIDI files for good?',
    warnings: 'Notes from SheetSage2: {list}',

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
    songScoreApplied:
      'Style, lyrics, seed and score of “{title}”, {song}, are in the form, planning is set to “{planning}”. The score can be edited under “Advanced parameters”.',
    renderFull: 'Render full',
    rendering: 'In progress',
    download: 'FLAC',
    score: 'ABC',
    zip: 'ZIP',
    zipTitle: 'FLAC and ABC as ZIP',
    zipRun: 'All as ZIP',
    openInLogic: 'Download as Logic project',
    logicWarnings: 'Logic project downloaded, with warnings: {messages}',
    noAudio: 'No audio',
    showLyrics: 'Lyrics',
    untitled: 'Untitled',
    storage: '{used} used, {free} free',
    deleteSong: 'Delete song',
    deleteRun: 'Delete all songs',
    deleteBusy: 'The worker is still on it.',
    confirmDeleteSong: 'Delete {song} of “{title}” ({size}) for good? Its audio, score and tokens will be gone.',
    confirmDeleteLastSong:
      'Delete {song} of “{title}” ({size}) for good? It is the last song, so the entry disappears from the library.',
    confirmDeleteRun: 'Delete all {count} songs of “{title}” ({size}) for good?',
    confirmDelete: 'Confirm deletion',
    delete: 'Delete',
    keep: 'Keep',
    deleted: '“{title}” has been deleted.',

    menuCreate: 'Create',
    menuSongs: 'Songs',
    menuPlaylist: 'Playlist',
    menu: 'Menu',
    playlist: 'Playlist',
    playlistEmpty: 'The playlist is empty. Under “Songs”, the plus next to a song adds it.',
    playlistSummary: '{count} songs, {duration}',
    playAll: 'Play all',
    play: 'Play',
    pause: 'Pause',
    nextTrack: 'Next song',
    previousTrack: 'Previous song',
    closePlayer: 'Close player',
    addToPlaylist: 'Add to playlist',
    removeFromPlaylist: 'Remove from playlist',
    moveUp: 'Move up',
    moveDown: 'Move down',

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
  return Number.isNaN(date.getTime())
    ? iso
    : date.toLocaleString(locale.value, { dateStyle: 'short', timeStyle: 'short' })
}

export function formatTime(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleTimeString(locale.value, { timeStyle: 'medium' })
}

/** 48_300_000 → "48 MB", 2_150_000_000 → "2.2 GB" (decimal units, as Finder counts). */
export function formatBytes(bytes: number): string {
  const [value, unit] =
    bytes >= 1e9 ? [bytes / 1e9, 'gigabyte'] : bytes >= 1e6 ? [bytes / 1e6, 'megabyte'] : [bytes / 1e3, 'kilobyte']
  return new Intl.NumberFormat(locale.value, {
    style: 'unit',
    unit,
    maximumFractionDigits: value < 10 ? 1 : 0,
  }).format(value)
}

/** 309.4 → "5:09" */
export function formatDuration(seconds: number): string {
  const total = Math.round(seconds)
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`
}
