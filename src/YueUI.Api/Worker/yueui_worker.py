#!/usr/bin/env python3
"""YueUI's extension of YuE Studio's yue2_worker.py: the same worker and protocol, plus generate fields the app
itself never sends. Run it with YuE Studio's Python:

    python -u yueui_worker.py "<install root>/src/tools/yue2_worker.py"

Extra fields of {"cmd": "generate"}:
  "full_steps"         solver steps at full quality, 1–64 (the worker always takes the model's 32)
  "max_tokens"         up to 15000 (600 s) instead of 9000; prompt, score (up to 4096) and song share a
                       context of 24576 tokens, so this is the longest limit that always fits
  "abc_sampling"       overrides of the score's sampling: temperature, top_p, top_k, repetition_penalty,
                       penalty_window
  "semantic_sampling"  the same for the song tokens (upstream YuE2's request files use these two names)
  "lora"               absolute path of a LoRA for the acoustic path (yueui_lora.py reads it), "lora_strength"
                       how strongly it applies (1 = as trained). A song's LoRA is written to ``yueui.json`` in its
                       folder when its synthesis starts, and a render of that song reads it from there again.

Only one LoRA can be in the synthesis engines' weights at a time: a song with a different LoRA (or none) waits until
the songs synthesizing now are through (see ``gate``), then the engines' weights are rebuilt with its LoRA. The
iPhone keeps the plain weights it was sent once, so songs with a LoRA never go there.

The worker is loaded as a module and patched at a few seams (see ``SEAMS``). If one of them has changed — a
YuE Studio update rewrote the worker — it runs as shipped and its ready event says "yueui_extensions": false,
so the web interface can tell that the extra fields have no effect.
"""
import dataclasses
import importlib.util
import inspect
import json
import sys
import threading
from pathlib import Path

import yueui_lora

MAX_TOKENS = 15000
MAX_FULL_STEPS = 64
SAMPLING_KEYS = ("temperature", "top_p", "top_k", "repetition_penalty", "penalty_window")
PHASES = {"abc_sampling": "abc", "semantic_sampling": "semantic"}

# What the patches rely on: the function and a piece of its source that has to be there unchanged.
SEAMS = (
    ("submit_generate", "SCHED.submit(songs)"),
    ("submit_generate", 'req.get("max_tokens"'),
    ("steps_for", "ode_steps"),
    ("run_batch", "SCHED.token_batch = songs"),
    ("run_batch", "s.needs_plan == songs[0].needs_plan"),
    ("run_batch", "generate_tokens(model, prefixes, pipe.generation_config.abc"),
    ("run_batch", "generate_tokens(model, [p.prefix for p in plans], sampling"),
    ("Scheduler", "s.needs_plan == q.needs_plan"),
    ("emit", "json.dumps(event)"),
    ("Scheduler", "def schedule(self)"),
    ("Scheduler", "first(SYNTH_WAIT"),
    ("run_ane", "pipe, model = acquire_model()"),
    ("run_gpu", "pipe, model = acquire_model()"),
    ("run_remote", "pipe, model = acquire_model()"),
    ("remote_can_take", "REMOTE is not None"),
    ("submit_render", "SCHED.submit([song])"),
)
# The engines read the NAR weights through this function (yue2.lean, imported by name into the Neural Engine's
# runtime), the MLX solver at the time it converts them.
LORA_SEAMS = (("yue2.lean", "nar_layer_state"), ("yue2.ane.runtime", "nar_layer_state"))


def load(path):
    """Import the worker as the module ``yue2_worker`` (its main loop only runs when we call it)."""
    sys.path.insert(0, str(path.parent))       # as if it had been started as a script
    spec = importlib.util.spec_from_file_location("yue2_worker", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules["yue2_worker"] = module
    spec.loader.exec_module(module)
    return module


def check(worker):
    """None if every seam is in place, otherwise what is missing."""
    for name, needle in SEAMS:
        target = getattr(worker, name, None)
        if target is None:
            return f"yue2_worker.{name} is gone"
        if needle not in inspect.getsource(target):
            return f"yue2_worker.{name} no longer contains {needle!r}"
    for module, name in LORA_SEAMS:
        if not hasattr(importlib.import_module(module), name):
            return f"{module}.{name} is gone"
    return None


class PlanKey:
    """Stands in for ``Song.needs_plan``. The scheduler batches queued songs whose ``mode`` and ``needs_plan``
    compare equal, and a batch samples with one setting: comparing the sampling overrides here as well keeps
    songs with different settings in separate batches. Its truth value is the plain flag, which is all the
    rest of the worker asks of it."""

    def __init__(self, needs_plan, sampling):
        self.needs_plan, self.sampling = bool(needs_plan), sampling

    def __bool__(self):
        return self.needs_plan

    def __eq__(self, other):
        if not isinstance(other, PlanKey):
            return NotImplemented
        return (self.needs_plan, self.sampling) == (other.needs_plan, other.sampling)

    def __hash__(self):
        return hash((self.needs_plan, self.sampling))

    def __repr__(self):
        return f"PlanKey({self.needs_plan}, {self.sampling})"


def patch(worker):
    from yue2.protocol import GenerationConfig, Sampling
    defaults = GenerationConfig()
    submit_generate, steps_for, generate_tokens = worker.submit_generate, worker.steps_for, worker.generate_tokens
    scheduler_submit = worker.SCHED.submit
    # submit_generate runs in a thread of its own per command; the songs it creates reach SCHED.submit there.
    pending = threading.local()

    def parse(req):
        sampling = {}
        for key, phase in PHASES.items():
            overrides = {k: v for k, v in (req.get(key) or {}).items() if k in SAMPLING_KEYS and v is not None}
            if overrides:
                Sampling(**{**dataclasses.asdict(getattr(defaults, phase)), **overrides})    # raises on bad values
                sampling[phase] = overrides
        limit = req.get("max_tokens")
        lora = loras.get(req["lora"], req.get("lora_strength", 1.0)) if req.get("lora") else None
        return {"sampling": sampling, "limit": None if limit is None else max(1, min(int(limit), MAX_TOKENS)),
                "lora": lora}

    def submit_generate_extended(req):
        pending.extras = parse(req)
        try:
            submit_generate(req)                   # clamps max_tokens to 9000; the songs get the real limit below
        finally:
            pending.extras = None

    def scheduler_submit_extended(songs):
        extras = getattr(pending, "extras", None)
        if extras is not None:
            key = tuple(sorted((phase, tuple(sorted(o.items()))) for phase, o in extras["sampling"].items()))
            for song in songs:
                song.yueui_sampling = extras["sampling"]
                song.needs_plan = PlanKey(song.needs_plan, key)
                if extras["limit"] is not None:
                    song.limit = extras["limit"]
                song.yueui_lora = extras["lora"]
            if extras["limit"] is not None and extras["limit"] > 9000:
                worker.log(f"Token limit {extras['limit']} (about {extras['limit'] / 25:.0f} s of audio)")
            if extras["lora"] is not None:
                worker.log(f"LoRA {extras['lora'].path.name} at {extras['lora'].strength:g} for {[s.label for s in songs]}")
        else:
            for song in songs:                     # a render: the LoRA its song was made with
                song.yueui_lora = loras.of_song(song)
        scheduler_submit(songs)

    def steps_for_extended(quality, req):
        if quality == "full" and req.get("full_steps") is not None:
            return max(1, min(int(req["full_steps"]), MAX_FULL_STEPS))
        return steps_for(quality, req)

    def generate_tokens_extended(model, prefixes, sampling, seeds, phase, **kwargs):
        # Only run_batch calls this, and it has published its songs as SCHED.token_batch by then.
        with worker.SCHED.cv:
            batch = list(worker.SCHED.token_batch or [])
        overrides = getattr(batch[0], "yueui_sampling", {}).get(phase) if batch else None
        if overrides:
            sampling = dataclasses.replace(sampling, **overrides)
            worker.log(f"Sampling of the {'score' if phase == 'abc' else 'song tokens'} for {[s.label for s in batch]}: "
                       + ", ".join(f"{k}={v}" for k, v in overrides.items()))
        return generate_tokens(model, prefixes, sampling, seeds, phase, **kwargs)

    loras = LoraSwitch(worker)
    loras.install()                                # its imports come before any assignment, as here
    worker.submit_generate = submit_generate_extended
    worker.SCHED.submit = scheduler_submit_extended
    worker.steps_for = steps_for_extended
    worker.generate_tokens = generate_tokens_extended


def lora_key(song):
    lora = getattr(song, "yueui_lora", None)
    return None if lora is None else lora.key


class LoraSwitch:
    """Keeps the synthesis engines' weights on the LoRA of the songs being synthesized.

    The model carries the LoRA its engines were built with (``model._yueui_lora``). Before a song's synthesis starts,
    ``ensure`` compares: on a change it drops the MLX weights (rebuilt on first use), marks the Neural Engine's weight
    surfaces stale (rewritten in place on their next bind, so programs that map them stay valid) and changes the few
    weights the engines take from the PyTorch model (latent projections, timestep embedding) in place, keeping the
    originals. The layers' NAR weights get their delta in ``nar_layer_state``, where the engines read them. A model
    dropped while idle comes back without a LoRA, which the marker on the new object says by its absence."""

    def __init__(self, worker):
        self.worker = worker
        self.cache = {}
        self.lock = threading.Lock()

    def get(self, path, strength):
        strength = float(strength)
        if not 0 <= strength <= 3:
            raise ValueError("lora_strength must be between 0 and 3")
        path = Path(path)
        key = (str(path), path.stat().st_mtime_ns, strength)
        if key not in self.cache:
            self.cache[key] = yueui_lora.Lora(path, strength)    # raises on a missing or unusable file
        return self.cache[key]

    def of_song(self, song):
        marker = song.directory / "yueui.json"
        try:
            saved = json.loads(marker.read_text()) if marker.exists() else {}
            return self.get(saved["lora"], saved.get("lora_strength", 1.0)) if saved.get("lora") else None
        except Exception as exc:
            self.worker.log(f"{song.label} was made with a LoRA that cannot be read ({exc}); it is synthesized without")
            return None

    def install(self):
        worker = self.worker
        import yue2.lean as lean
        import yue2.ane.runtime as ane_runtime
        original_state = lean.nar_layer_state

        def nar_layer_state(model, index):
            state = original_state(model, index)
            lora = getattr(model, "_yueui_lora", None)
            if lora is None or not lean.is_lean(model):         # a full model got its change in the modules
                return state
            return yueui_lora.apply(state, lora.layer_deltas(index, model.config))

        lean.nar_layer_state = nar_layer_state
        ane_runtime.nar_layer_state = nar_layer_state

        def synthesizing(run):
            def run_with_lora(song):
                try:
                    _, model = worker.acquire_model()
                    self.ensure(model, getattr(song, "yueui_lora", None))
                except Exception as exc:
                    worker.fail(song, exc)             # finish() also frees the resource the scheduler gave it
                    return
                self.mark(song)
                return run(song)
            return run_with_lora

        # The iPhone takes no song with a LoRA, but a plain one there must not get latent projections changed in place.
        worker.run_ane, worker.run_gpu, worker.run_remote = (synthesizing(worker.run_ane), synthesizing(worker.run_gpu),
                                                             synthesizing(worker.run_remote))
        remote_can_take = worker.remote_can_take
        worker.remote_can_take = lambda song: lora_key(song) is None and remote_can_take(song)
        schedule = worker.SCHED.schedule
        worker.SCHED.schedule = lambda: self.gate(schedule)

    def gate(self, schedule):
        """Runs the scheduler's policy (under its lock) with every waiting synthesis hidden whose LoRA differs from
        the one being synthesized now, or, with nothing synthesizing, from the first waiting song's."""
        worker, sched = self.worker, self.worker.SCHED
        busy = [s for s in (sched.gpu_synth, sched.ane_synth, sched.remote_synth) if s is not None]
        waiting = sorted((s for s in sched.songs if s.state == worker.SYNTH_WAIT and not s.cancel.is_set()),
                         key=lambda s: s.priority)
        lead = busy[0] if busy else waiting[0] if waiting else None
        held = [s for s in waiting if lead is not None and lora_key(s) != lora_key(lead)]
        for s in held:
            s.state = "yueui-lora-wait"
        try:
            return schedule()
        finally:
            for s in held:
                if s.state == "yueui-lora-wait":
                    s.state = worker.SYNTH_WAIT

    def ensure(self, model, lora):
        import yue2.lean as lean
        with self.lock:
            current = getattr(model, "_yueui_lora", None)
            if (None if current is None else current.key) == (None if lora is None else lora.key):
                return
            model._yue2_mlx_weights = None
            surfaces = getattr(model, "_yue2_ane_weights", None)
            if surfaces is not None:
                surfaces.holding = [None] * len(surfaces.holding)
            originals = model.__dict__.setdefault("_yueui_lora_originals", {})
            with self.worker.TORCH_LOCK:
                for name, weight in originals.items():
                    model.get_parameter(name).data.copy_(weight)
                originals.clear()
                if lora is not None:
                    deltas = {f"{name}.weight": d for name, d in lora.top_deltas(model).items()}
                    if not lean.is_lean(model):
                        for index in range(len(model.model.layers)):
                            for short, delta in lora.layer_deltas(index, model.config).items():
                                deltas[f"model.layers.{index}.{yueui_lora.LAYER_TARGETS[short]}.weight"] = delta
                    for name, delta in deltas.items():
                        weight = model.get_parameter(name)
                        originals[name] = weight.detach().clone()
                        weight.data.copy_((weight.float() + delta.to(weight.device)).to(weight.dtype))
            model._yueui_lora = lora
            self.worker.log("Synthesis weights " + (f"with LoRA {lora.path.name} at {lora.strength:g}" if lora else "without LoRA"))

    def mark(self, song):
        """Remembers the song's LoRA in its folder, for renders and for the app's library."""
        lora = getattr(song, "yueui_lora", None)
        marker = song.directory / "yueui.json"
        try:
            if lora is None:
                marker.unlink(missing_ok=True)
            else:
                from yue2.storage import write_json
                write_json(marker, {"lora": str(lora.path), "lora_name": lora.path.stem, "lora_strength": lora.strength})
        except OSError as exc:
            self.worker.log(f"Cannot write {marker}: {exc}")


def announce(worker, active):
    """Adds the extension's state to the worker's ready event."""
    emit = worker.emit

    def emit_announcing(**event):
        if event.get("event") == "ready":
            event["yueui_extensions"] = active
        emit(**event)

    worker.emit = emit_announcing


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: yueui_worker.py <path to yue2_worker.py>")
    worker = load(Path(sys.argv[1]).resolve())
    problem = check(worker)
    if problem is None:
        try:
            patch(worker)
        except Exception as exc:                  # patch() assigns only at its end, so nothing is half-patched
            problem = f"{type(exc).__name__}: {exc}"
    announce(worker, problem is None)
    if problem is not None:
        worker.log(f"YueUI extensions are off, the worker runs as YuE Studio ships it: {problem}")
    worker.main()


if __name__ == "__main__":
    main()
