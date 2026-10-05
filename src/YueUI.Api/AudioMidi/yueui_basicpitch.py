"""Tonwerk's audio to MIDI: one recording (typically a singing voice) to a Standard MIDI File with Spotify's Basic Pitch.

    python yueui_basicpitch.py '<json>'

The JSON names `audio` (a file librosa can read: WAV or FLAC), `output` (the .mid to write), `name` (the track's
name), `mono` (one note at a time, as a voice sings), `quantize` (start and end on the sixteenth grid of `tempo`),
`bends` (keep Basic Pitch's pitch bends; only with `mono`, since all notes share one channel) and `tempo` (written
into the file, so Logic's bars line up with the recording; 120 when unknown).

Basic Pitch runs through ONNX Runtime on the CPU: the model is small (a few MB, well under a GB of memory with the
audio), so it needs no place in Tonwerk's queue, and the same path runs on the Mac and in the tests' container.
Answers one `YUEUI {json}` line when done; Basic Pitch's own output goes around it.
"""

import json
import logging
import os
import sys
import time

# Basic Pitch warns about every backend it does not find (CoreML, TensorFlow, TFLite); only ONNX is installed.
logging.disable(logging.WARNING)


def emit(**event):
    print("YUEUI " + json.dumps(event), flush=True)


def single_line(notes, minimum):
    """One note at a time: where two overlap, the louder one wins and the other gives way.

    Basic Pitch is polyphonic, so a voice comes back with ghost notes: an overtone an octave or a twelfth up, quieter
    than the sung note, or the next note starting before the last one let go. A quieter note inside a louder one is
    dropped, or keeps only the part that sticks out; a louder one cuts the one before it short.
    """
    kept = []
    for note in sorted(notes, key=lambda n: (n.start, -n.velocity)):
        if kept and note.start < kept[-1].end:
            last = kept[-1]
            if note.velocity < last.velocity:
                if note.end - last.end < minimum:
                    continue
                note.start = last.end
            else:
                last.end = note.start
                if last.end - last.start < minimum:
                    kept.pop()
        kept.append(note)
    return kept


def on_grid(notes, tempo):
    """Start and end on the nearest sixteenth; a note keeps at least one, and none overlaps the next afterwards."""
    step = 60.0 / tempo / 4
    for note in notes:
        start = round(note.start / step) * step
        end = max(start + step, round(note.end / step) * step)
        note.start, note.end = start, end
    notes.sort(key=lambda n: (n.start, n.pitch))
    return notes


def without_overlap(notes):
    """After the grid two notes of a line can share a sixteenth; the earlier one ends where the next begins."""
    kept = []
    for note in notes:
        if kept and note.start < kept[-1].end:
            if note.start <= kept[-1].start:
                continue
            kept[-1].end = note.start
        kept.append(note)
    return kept


def main():
    job = json.loads(sys.argv[1])
    started = time.monotonic()
    try:
        from basic_pitch import FilenameSuffix, build_icassp_2022_model_path
        from basic_pitch.inference import predict
    except ImportError as error:
        print(f"Error: Basic Pitch is not installed completely ({error}); run deploy/install-midi.sh.", flush=True)
        sys.exit(1)

    tempo = float(job.get("tempo") or 120)
    mono = bool(job.get("mono", True))
    _, midi, _ = predict(
        job["audio"],
        build_icassp_2022_model_path(FilenameSuffix.onnx),
        onset_threshold=0.5,
        frame_threshold=0.3,
        # Shorter than a sixteenth at 120 bpm is a consonant or a breath, not a note.
        minimum_note_length=100,
        # A singing voice's range, so hiss and low rumble do not become notes; an instrument's stem keeps the full one.
        minimum_frequency=job.get("minHz"),
        maximum_frequency=job.get("maxHz"),
        multiple_pitch_bends=False,
        melodia_trick=True,
        midi_tempo=tempo,
    )

    instrument = midi.instruments[0]
    instrument.name = job.get("name") or "Vocal"
    notes = instrument.notes
    if mono:
        notes = single_line(notes, 0.06)
    if job.get("quantize"):
        notes = on_grid(notes, tempo)
        if mono:
            notes = without_overlap(notes)
    instrument.notes = notes
    if not (mono and job.get("bends")):
        instrument.pitch_bends = []
    midi.write(job["output"])
    emit(event="done", notes=len(notes), seconds=round(time.monotonic() - started, 2))


if __name__ == "__main__":
    main()
    # Leave without Python's shutdown: on the Mac, a library's static destructors (onnxruntime's, at one
    # interpreter's exit beside numba and scikit-learn) ran into a mutex already torn down and aborted with
    # "recursive_mutex lock failed" after the file was written. Nothing is left to clean up at this point.
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(0)
