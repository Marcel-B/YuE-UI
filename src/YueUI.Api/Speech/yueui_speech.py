"""Speaks one text with one mlx-audio model, for YuE UI's speech lab (Speech/MlxAudioEngine.cs).

Run with the lab's own Python environment (deploy/install-speech.sh), not YuE Studio's: mlx-audio pins
transformers and mlx versions that YuE2 does not share.

In: one JSON object on stdin
    {"model": "<hf repo>", "text": "...", "output": "/abs/take.wav",
     "refAudio": "/abs/voice.wav" | null, "refText": "..." | null,
     "langCode": "de" | null, "options": {...} | null}
Out: the take as WAV at "output", and lines starting with "YUEUI " on stdout, each a JSON event:
    {"event": "stage", "stage": "loading" | "speaking"}
    {"event": "done", "loadSeconds": 12.3, "speakSeconds": 4.5, "peakMemoryGb": 5.1}
Anything else on stdout or stderr is mlx-audio's own output, which the server keeps for the error message.
The model is loaded for this one take and freed with the process: on 24 GB it must not stay beside YuE2.
"""

import functools
import json
import os
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


class OwnDefaults:
    """
    The model as generate_audio sees it, minus generate_audio's own sampling defaults.

    generate_audio hands every model temperature=0.7 and max_tokens=1200 unless told otherwise, over the model's own
    defaults. MOSS-TTS samples its audio at 1.7 (its authors call it sensitive to that); at 0.7 it speaks the first
    line and then drifts into chirping noise, and 1200 tokens cut it off after 96 s. So these two reach the model only
    when the request names them, and otherwise each model uses what its authors chose. A wrapper rather than a
    replaced method, so a model that calls its own generate still gets what it asks for.
    """

    DEFAULTED = ("temperature", "max_tokens")

    def __init__(self, model, named):
        self._model = model

        @functools.wraps(model.generate)  # generate_audio reads the signature to see whether the model takes ref_text
        def generate(**kwargs):
            for key in self.DEFAULTED:
                if key not in named:
                    kwargs.pop(key, None)
            return model.generate(**kwargs)

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
        model=OwnDefaults(model, kwargs),
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
