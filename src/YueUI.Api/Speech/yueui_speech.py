"""Speaks one text with one mlx-audio model, for YuE UI's speech lab (Speech/MlxAudioEngine.cs).

Run with the lab's own Python environment (deploy/install-speech.sh), not YuE Studio's: mlx-audio pins
transformers and mlx versions that YuE2 does not share.

In: one JSON object on stdin
    {"model": "<hf repo>", "text": "...", "output": "/abs/take.wav",
     "refAudio": "/abs/voice.wav" | null, "refText": "..." | null,
     "langCode": "de" | null, "options": {...} | null, "chunkCharacters": 300 | null, "contextPieces": 1 | null}
Out: the take as WAV at "output", and lines starting with "YUEUI " on stdout, each a JSON event:
    {"event": "stage", "stage": "loading" | "speaking"}
    {"event": "done", "loadSeconds": 12.3, "speakSeconds": 4.5, "peakMemoryGb": 5.1}
Anything else on stdout or stderr is mlx-audio's own output, which the server keeps for the error message.
The model is loaded for this one take and freed with the process: on 24 GB it must not stay beside YuE2.
"""

import functools
import json
import os
import re
import sys
import time


def emit(event: dict) -> None:
    print("YUEUI " + json.dumps(event), flush=True)


def peak_memory_gb() -> float | None:
    try:
        import mlx.core as mx

        return round(mx.get_peak_memory() / 1e9, 2)
    except Exception:  # an older mlx only has it under mx.metal
        try:
            import mlx.core as mx

            return round(mx.metal.get_peak_memory() / 1e9, 2)
        except Exception:
            return None


# Where a sentence may end: its punctuation, closing quotes or brackets and footnote marks ("[5]"), then space.
SENTENCE_END = re.compile(r"[.!?…](?:\[\d+\]|[\"'»«“”)\]])*\s+")
# Where a sentence too long for one piece may be cut instead.
CLAUSE_END = re.compile(r"(?<=[,;:])\s+")


def sentences(line: str) -> list[str]:
    """
    The line cut after each sentence. Not at a full stop after a number or a short word: in German that is a date
    ("am 13. Mai") or an abbreviation ("z. B.", "Nr. 5"), and a cut there would make the model pause mid-sentence.
    Missing a real sentence end only makes a piece longer.
    """
    result, start = [], 0
    for match in SENTENCE_END.finditer(line):
        word = line[start : match.start()].rsplit(" ", 1)[-1]
        if match.group()[0] == "." and (len(word) < 4 or word[-1].isdigit()):
            continue
        result.append(line[start : match.end()].rstrip())
        start = match.end()
    return result + [line[start:]]


def pieces(text: str, limit: int) -> list[str]:
    """
    The text cut at sentence ends into pieces of at most `limit` characters (a longer sentence at commas; a longer
    clause stays whole), with the lines joined.

    A line that ends without punctuation, such as a heading, gets a full stop: read as the start of the next sentence,
    the models left it out.
    """
    spans = []
    for line in text.splitlines():
        line = " ".join(line.split())
        if not line:
            continue
        if line[-1].isalnum():
            line += "."
        for sentence in sentences(line):
            spans.extend(CLAUSE_END.split(sentence) if len(sentence) > limit else [sentence])
    result = []
    for sentence in spans:
        if result and len(result[-1]) + 1 + len(sentence) <= limit:
            result[-1] += " " + sentence
        else:
            result.append(sentence)
    return result


class LabModel:
    """
    The model as generate_audio sees it: with its own sampling defaults, and a long text spoken in pieces where the
    request asks for that. A wrapper rather than a replaced method, so a model that calls its own generate still gets
    what it asks for.

    generate_audio hands every model temperature=0.7 and max_tokens=1200 unless told otherwise, over the model's own
    defaults. MOSS-TTS samples its audio at 1.7 (its authors call it sensitive to that); at 0.7 it spoke the first
    line and then drifted into chirping noise, and 1200 tokens cut it off after 96 s. So these two reach the model
    only when the request names them, and otherwise each model uses what its authors chose.

    Chatterbox and Higgs Audio speak about 40 s per generation at most and split nothing themselves (Higgs Audio v3
    recommends 1024 tokens, 41 s); given a whole paragraph, v3 spoke its first long sentence and then buzzed on. With
    chunkCharacters, each piece is a generation of its own, with a short pause after it. Without a recorded voice,
    the pieces after the first clone the first one, as Higgs Audio's own long-form example does with the audio it
    made, or every piece would get a voice of its own.

    Each piece is still sampled afresh, and even with the same recording the voice shifted a little from one piece to
    the next. With contextPieces, a model that takes several reference clips (Higgs Audio v3: several <|ref_text|>
    <|ref_audio|> pairs in one prompt) gets the pieces just spoken beside the recording, so that it clones the voice as
    the last piece left it. Nobody documents this: Higgs Audio v3 describes its model as grounding each chunk on the
    reference and the chunks before, but serves one turn per request, and a community long-form node reuses the
    recording for every chunk. So it is an experiment, to be judged by ear; a piece that chirps is passed on with it.
    """

    DEFAULTED = ("temperature", "max_tokens")
    PAUSE_SECONDS = 0.3

    def __init__(self, model, named, chunk_characters=None, context_pieces=None):
        self._model = model

        @functools.wraps(model.generate)  # generate_audio reads the signature to see whether the model takes ref_text
        def generate(**kwargs):
            import mlx.core as mx  # loaded with the model by now

            for key in self.DEFAULTED:
                if key not in named:
                    kwargs.pop(key, None)
            text = kwargs.pop("text")
            parts = pieces(text, chunk_characters) if chunk_characters else [text]
            # The voice every piece clones: the recording, or without one the first piece. Then the pieces just
            # spoken, for a model that takes several references and so continues where the last piece ended.
            voice, before = [], []
            if kwargs.get("ref_audio") is not None:
                voice.append((kwargs.pop("ref_audio"), kwargs.pop("ref_text", None)))
            for index, part in enumerate(parts):
                clips = voice + before
                if clips:
                    audios, texts = zip(*clips)
                    kwargs["ref_audio"] = audios[0] if len(clips) == 1 else list(audios)
                    kwargs["ref_text"] = texts[0] if len(clips) == 1 else list(texts)
                results = list(model.generate(text=part, **kwargs))
                if not results:
                    continue
                if index < len(parts) - 1 and (not voice or context_pieces):
                    spoken = (mx.concatenate([result.audio for result in results], axis=0), part)
                    if voice:
                        before = (before + [spoken])[-context_pieces:]
                    else:
                        voice.append(spoken)
                if index < len(parts) - 1:
                    last = results[-1]
                    pause = int(self.PAUSE_SECONDS * last.sample_rate)
                    silence = mx.zeros((pause, *last.audio.shape[1:]), dtype=last.audio.dtype)
                    last.audio = mx.concatenate([last.audio, silence], axis=0)
                yield from results

        self.generate = generate

    def __getattr__(self, name):
        return getattr(self._model, name)


def main() -> int:
    request = json.load(sys.stdin)
    output = request["output"]
    directory, name = os.path.split(output)
    prefix, _ = os.path.splitext(name)

    emit({"event": "stage", "stage": "loading"})
    started = time.perf_counter()
    try:
        # Imported here so that a missing package shows up as the take's error rather than before the first event.
        from mlx_audio.tts.generate import generate_audio
        from mlx_audio.tts.utils import load_model

        model = load_model(request["model"])
    except ModuleNotFoundError as missing:
        # Some models need a package the others do without (Higgs Audio v3 reads its bfloat16 codec through PyTorch);
        # the server shows this last "Error" line as the take's message, so it says what to do.
        print(
            f"Error: this model needs the Python package '{missing.name}', which the speech lab's environment lacks;"
            " run deploy/install-speech.sh again.",
            flush=True,
        )
        return 1
    loaded = time.perf_counter()

    emit({"event": "stage", "stage": "speaking"})
    kwargs = dict(request.get("options") or {})
    if request.get("langCode"):
        kwargs["lang_code"] = request["langCode"]
    # generate_audio rather than model.generate: it prepares the reference the way each model expects it (some want
    # the path, some samples at their own rate) and joins the segments a long text is split into.
    generate_audio(
        text=request["text"],
        model=LabModel(model, kwargs, request.get("chunkCharacters"), request.get("contextPieces")),
        ref_audio=request.get("refAudio"),
        ref_text=request.get("refText"),
        output_path=directory,
        file_prefix=prefix,
        audio_format="wav",
        join_audio=True,
        verbose=True,
        **kwargs,
    )
    finished = time.perf_counter()
    if not os.path.exists(output):
        print(f"mlx-audio wrote no audio to {output}", file=sys.stderr)
        return 1
    emit(
        {
            "event": "done",
            "loadSeconds": round(loaded - started, 2),
            "speakSeconds": round(finished - loaded, 2),
            "peakMemoryGb": peak_memory_gb(),
        }
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
