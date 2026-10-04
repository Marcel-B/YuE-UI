"""Paints one picture with FLUX.2 Klein through mflux, for Tonwerk's covers (Images/MfluxEngine.cs).

Run with its own Python environment (deploy/install-images.sh), not YuE Studio's or the speech lab's: mflux pins mlx,
transformers and torch versions the others do not share.

In: one JSON object on stdin
    {"model": "flux2-klein-4b", "quantize": 8 | null, "steps": 4, "prompt": "...", "seed": 42,
     "width": 1024, "height": 1024, "output": "/abs/picture.jpg"}
Out: the picture as JPEG at "output", and lines starting with "YUEUI " on stdout, each a JSON event:
    {"event": "stage", "stage": "loading" | "painting", "fraction": 0.5}
    {"event": "done", "loadSeconds": 12.3, "paintSeconds": 30.1, "peakMemoryGb": 8.9}
Anything else on stdout or stderr is mflux's own output, which the server keeps for the error message.
The model is loaded for this one picture and freed with the process: on 24 GB it must not stay beside YuE2.
"""

import json
import sys
import time


def emit(event: dict) -> None:
    print("YUEUI " + json.dumps(event), flush=True)


def peak_memory_gb() -> float | None:
    try:
        import mlx.core as mx

        return round(mx.get_peak_memory() / 1e9, 2)
    except Exception:  # an older mlx only has it under mx.metal
        return None


class Progress:
    """mflux calls every registered object that has call_in_loop once per denoising step."""

    def __init__(self, steps: int):
        self.steps = max(steps, 1)

    def call_in_loop(self, t, seed, prompt, latents, config, time_steps, **_) -> None:
        emit({"event": "stage", "stage": "painting", "fraction": round(min((t + 1) / self.steps, 1), 3)})


def main() -> int:
    request = json.load(sys.stdin)
    emit({"event": "stage", "stage": "loading", "fraction": 0})
    started = time.perf_counter()
    try:
        # Imported here so that a missing package shows up as the picture's error rather than before the first event.
        from mflux.models.common.config import ModelConfig
        from mflux.models.flux2.variants import Flux2Klein
    except ModuleNotFoundError as missing:
        print(f"Error: the Python package '{missing.name}' is missing; run deploy/install-images.sh again.", flush=True)
        return 1
    try:
        model = Flux2Klein(model_config=ModelConfig.from_name(request["model"]), quantize=request.get("quantize"))
    except Exception as error:
        # A gated repository (Klein 9B) answers 401/403 until its licence is accepted with the token's account.
        if type(error).__name__ in ("GatedRepoError", "RepositoryNotFoundError") or "401" in str(error) or "403" in str(error):
            print(
                "Error: Hugging Face refused the download. Accept the model's licence on huggingface.co and put an"
                " access token of that account into ~/.config/tonwerk/huggingface.token.",
                flush=True,
            )
            return 1
        raise
    steps = int(request.get("steps") or 4)
    model.callbacks.register(Progress(steps))
    loaded = time.perf_counter()

    emit({"event": "stage", "stage": "painting", "fraction": 0})
    image = model.generate_image(
        seed=int(request["seed"]),
        prompt=request["prompt"],
        num_inference_steps=steps,
        width=int(request.get("width") or 1024),
        height=int(request.get("height") or 1024),
        # Distilled Klein accepts no other guidance.
        guidance=1.0,
    )
    finished = time.perf_counter()
    # PIL directly rather than mflux's save, which swallows errors and writes PNGs of several megabytes: a cover goes
    # into every export and streaming copy.
    image.image.convert("RGB").save(request["output"], "JPEG", quality=92)
    emit(
        {
            "event": "done",
            "loadSeconds": round(loaded - started, 2),
            "paintSeconds": round(finished - loaded, 2),
            "peakMemoryGb": peak_memory_gb(),
        }
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
