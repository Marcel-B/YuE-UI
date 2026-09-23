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

The worker is loaded as a module and patched at a few seams (see ``SEAMS``). If one of them has changed — a
YuE Studio update rewrote the worker — it runs as shipped and its ready event says "yueui_extensions": false,
so the web interface can tell that the extra fields have no effect.
"""
import dataclasses
import importlib.util
import inspect
import sys
import threading
from pathlib import Path

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
)


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
        return {"sampling": sampling, "limit": None if limit is None else max(1, min(int(limit), MAX_TOKENS))}

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
            if extras["limit"] is not None and extras["limit"] > 9000:
                worker.log(f"Token limit {extras['limit']} (about {extras['limit'] / 25:.0f} s of audio)")
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

    worker.submit_generate = submit_generate_extended
    worker.SCHED.submit = scheduler_submit_extended
    worker.steps_for = steps_for_extended
    worker.generate_tokens = generate_tokens_extended


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
