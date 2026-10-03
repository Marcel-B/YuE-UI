#!/usr/bin/env python3
"""Trains a LoRA on YuE2's acoustic (NAR) path from a folder of songs, on Apple Silicon (PyTorch on MPS) or CUDA.
Run it with YuE Studio's Python, which has torch, safetensors and yue2 (deploy/train-lora.sh does that):

    python yueui_lora_train.py --data ~/dubstep --output "<data folder>/loras/dubstep.safetensors" --trigger dubstep

What it learns: the sound (instruments, mix, timbre). The composition (score and song tokens) comes from YuE2's
AR path, which stays untouched, so a style still has to be asked for in the prompt; the trigger word goes into
every training caption and belongs in the style of songs made with the LoRA.

How: the VAE encodes every song once into latents (cached next to the output). Each step takes a random
``--clip-seconds`` slice, noises it to a random point t of the flow and trains the NAR path to predict the
velocity (noise - latents), exactly the field yue2.nar's solver integrates from t = 1 down to 0.

The conditioning is the model's own codec-dropout regime (``nar_velocity(..., nar_cond_end)``): the NAR sees the
text prompt but not the song tokens, which sit invisible between prompt and latents. YuE2 has no released tokenizer
from audio to song tokens, so a real recording cannot be conditioned the way generation is; the dropout regime is
the one the base model was trained for without them, positions and all. Songs made with the LoRA do see their song
tokens, so the LoRA tends to act a little weaker than its training; ``lora_strength`` above 1 makes up for it.

Memory: only the NAR half of every layer is trained. The AR half is needed once per caption to compute the prompt's
keys and values, then dropped, so training holds ~3 GB of weights plus activations (about 6 GB at 20 s clips).

Progress goes to stdout as ``YUEUI {json}`` lines (stage, step, saved, done), readable text to stderr.
"""
from __future__ import annotations

import argparse
import gc
import hashlib
import json
import math
import random
import signal
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F
import torch.utils.checkpoint
from torch import nn

sys.path.insert(0, str(Path(__file__).resolve().parent))
import yueui_lora  # noqa: E402

SAMPLE_RATE = 48000
HOP = 1920                                   # samples per latent frame: 25 frames per second
AUDIO = {".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff"}


def event(kind, **fields):
    print("YUEUI " + json.dumps({"event": kind, **fields}), flush=True)


def say(text):
    print(text, file=sys.stderr, flush=True)


# ── Data ──────────────────────────────────────────────────────────────────────

def scan(folder):
    """[(audio file, caption)]: the caption is a same-named .txt beside it, else empty."""
    files = sorted(p for p in Path(folder).expanduser().iterdir() if p.is_file() and p.suffix.lower() in AUDIO)
    if not files:
        raise SystemExit(f"No audio files ({', '.join(sorted(AUDIO))}) in {folder}")
    pairs = []
    for file in files:
        caption = file.with_suffix(".txt")
        pairs.append((file, caption.read_text(encoding="utf-8", errors="replace").strip() if caption.is_file() else ""))
    return pairs


def decode(path):
    """48 kHz stereo float32 [2, samples] through ffmpeg, which reads every format the folder may hold."""
    result = subprocess.run(["ffmpeg", "-v", "error", "-nostdin", "-i", str(path), "-f", "f32le", "-ac", "2",
                             "-ar", str(SAMPLE_RATE), "-"], capture_output=True)
    if result.returncode != 0:
        raise RuntimeError(f"ffmpeg cannot read {path.name}: {result.stderr.decode(errors='replace').strip()[:200]}")
    audio = np.frombuffer(result.stdout, dtype=np.float32).reshape(-1, 2).T
    return torch.from_numpy(audio.copy()).clamp(-1, 1)


def encode(vae, audio, device, chunk_s=30, overlap_s=2):
    """Latents [frames, 64] in overlapping chunks, so a long song needs no more memory than a short one.
    Chunk and overlap are whole frames, so the trimmed pieces join exactly."""
    total = audio.shape[1] - audio.shape[1] % HOP
    step, overlap = chunk_s * SAMPLE_RATE, overlap_s * SAMPLE_RATE
    pieces = []
    for start in range(0, total, step):
        end = min(start + step, total)
        low, high = max(0, start - overlap), min(total, end + overlap)
        with torch.inference_mode():
            z = vae.encode(audio[None, :, low:high].to(device))[0].float().cpu()      # [64, frames]
        left, kept = (start - low) // HOP, (end - start) // HOP
        pieces.append(z[:, left:left + kept])
    return torch.cat(pieces, 1).T.contiguous().numpy()


def latents_for(pairs, cache, vae_dir, device):
    """Each song's latents, from the cache when the file is unchanged."""
    cache.mkdir(parents=True, exist_ok=True)
    vae, songs = None, []
    for index, (path, caption) in enumerate(pairs):
        stat = path.stat()
        key = hashlib.sha256(f"{path.resolve()}|{stat.st_size}|{stat.st_mtime_ns}|v1".encode()).hexdigest()[:20]
        file = cache / f"{path.stem[:40]}-{key}.npy"
        if not file.exists():
            if vae is None:
                say("Loading the VAE encoder")
                from yue2.modeling_vae import YuE2VAE
                vae = YuE2VAE.from_pretrained(vae_dir, decoder_only=False, device=str(device))
            event("stage", stage="encoding", file=path.name, index=index + 1, total=len(pairs))
            say(f"Encoding {path.name} ({index + 1}/{len(pairs)})")
            np.save(file, encode(vae, decode(path), device).astype(np.float16))
        latents = np.load(file, mmap_mode="r")
        songs.append((path, caption, latents))
        say(f"  {path.name}: {len(latents) / 25:.0f} s" + (f", caption {caption!r}" if caption else ""))
    del vae
    release(device)
    return songs


# ── Model ─────────────────────────────────────────────────────────────────────

class LoRALinear(nn.Module):
    """A frozen Linear plus up @ down * alpha / rank; the pair in fp32, so AdamW updates are not lost to bf16."""

    def __init__(self, base, rank, alpha):
        super().__init__()
        self.base, self.scale = base, alpha / rank
        self.down = nn.Parameter(torch.empty(rank, base.in_features, device=base.weight.device))
        self.up = nn.Parameter(torch.zeros(base.out_features, rank, device=base.weight.device))
        nn.init.kaiming_uniform_(self.down, a=math.sqrt(5))

    def forward(self, x):
        return self.base(x) + ((x.float() @ self.down.T) @ self.up.T * self.scale).to(x.dtype)


def inject(model, rank, alpha):
    """LoRA on every NAR projection; everything else frozen. Returns {HF weight prefix: module}."""
    for p in model.parameters():
        p.requires_grad_(False)
    wrapped = {}
    for i, layer in enumerate(model.model.layers):
        for suffix in yueui_lora.LAYER_TARGETS.values():
            owner, _, attr = suffix.rpartition(".")
            parent = layer.get_submodule(owner)
            module = LoRALinear(getattr(parent, attr), rank, alpha)
            setattr(parent, attr, module)
            wrapped[f"model.layers.{i}.{suffix}"] = module
    return wrapped


@torch.no_grad()
def prompt_cache(model, ids, device):
    """Keys and values of the prompt in every layer, through the AR path (causal), as generation computes them."""
    backbone = model.model
    tokens = torch.tensor([ids], device=device)
    cos, sin = backbone.rotary_emb(torch.arange(len(ids), device=device)[None])
    x = backbone.embed_tokens(tokens)
    cache = []
    for layer in backbone.layers:
        attn = layer.self_attn
        q, k, v = attn.project_qkv(layer.input_layernorm(x), cos, sin)
        cache.append((k[0].clone(), v[0].clone()))                 # [P, KV, hd]
        out = attention(q[0], k[0], v[0], causal=True)
        x = x + attn.o_proj(out.reshape(1, len(ids), -1))
        x = x + layer.mlp(layer.post_attention_layernorm(x))
    return cache


def attention(q, k, v, causal=False):
    """q [S, H, hd], k/v [L, KV, hd] -> [S, H, hd]; MPS's SDPA has no grouped-query mode, so K/V are repeated."""
    groups = q.shape[1] // k.shape[1]
    k, v = k.repeat_interleave(groups, 1), v.repeat_interleave(groups, 1)
    out = F.scaled_dot_product_attention(q.transpose(0, 1), k.transpose(0, 1), v.transpose(0, 1), is_causal=causal)
    return out.transpose(0, 1)


def drop_ar(model):
    """After the prompt caches: the AR half of every layer, the embeddings and the LM head are never used again."""
    for layer in model.model.layers:
        for name in ("input_layernorm", "self_attn", "post_attention_layernorm", "mlp"):
            setattr(layer, name, None)
    model.model.embed_tokens = None
    model.lm_head = None


def velocity(model, prompt, x_t, raw_t, offset, checkpoint=False):
    """The NAR forward of ``nar_velocity`` with gradients: latents at positions ``offset``.., attending to the
    prompt's cached keys/values and to each other (bidirectional). [T, 64] -> [T, 64]."""
    T = x_t.shape[0]
    n = T + 2                                                    # LATENT_START, content, LATENT_END
    dtype = model.vae2llm.weight.dtype
    device = x_t.device
    x_nar = torch.zeros(n, x_t.shape[1], device=device, dtype=dtype)
    x_nar[1:1 + T] = x_t.to(dtype)
    shifted = model._shift_t_value(raw_t, device, dtype)
    x = model.vae2llm(x_nar[None]) + model.time_embedder(shifted.expand(n))[None]
    x = x + model.latent_pos_embed(torch.arange(n, device=device).clamp(max=model.config.max_latent_frames - 1))[None]
    cos, sin = model.model.rotary_emb(torch.arange(offset, offset + n, device=device)[None])

    def block(x, layer, k_prompt, v_prompt):
        attn = layer.nar_self_attn
        q, k, v = attn.project_qkv(layer.nar_input_layernorm(x), cos, sin)
        out = attention(q[0], torch.cat([k_prompt, k[0]]), torch.cat([v_prompt, v[0]]))
        x = x + attn.o_proj(out.reshape(1, n, -1))
        return x + layer.nar_mlp(layer.nar_pre_mlp_layernorm(x))

    for layer, (k_prompt, v_prompt) in zip(model.model.layers, prompt):
        if checkpoint:
            x = torch.utils.checkpoint.checkpoint(block, x, layer, k_prompt, v_prompt, use_reentrant=False)
        else:
            x = block(x, layer, k_prompt, v_prompt)
    return model.llm2vae(model.model.norm(x))[0, 1:1 + T]


def release(device):
    gc.collect()
    if device.type == "mps":
        torch.mps.empty_cache()
    elif device.type == "cuda":
        torch.cuda.empty_cache()


# ── Training ──────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--data", required=True, help="folder of songs (mp3, wav, flac, m4a, ...), optional .txt captions")
    parser.add_argument("--output", required=True, help="the LoRA file to write (.safetensors)")
    parser.add_argument("--trigger", default="", help="word put in front of every caption, e.g. the style's name")
    parser.add_argument("--style", default="", help="caption for songs without a .txt of their own")
    parser.add_argument("--steps", type=int, default=1500)
    parser.add_argument("--rank", type=int, default=32)
    parser.add_argument("--alpha", type=float, default=32)
    parser.add_argument("--lr", type=float, default=1e-4)
    parser.add_argument("--warmup", type=int, default=50)
    parser.add_argument("--clip-seconds", type=float, default=20)
    parser.add_argument("--caption-dropout", type=float, default=0.1, help="share of steps with an empty style")
    parser.add_argument("--save-every", type=int, default=500)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--checkpointing", action="store_true", help="recompute activations: less memory, slower")
    parser.add_argument("--cache", help="latent cache (default: .lora-cache next to the output)")
    parser.add_argument("--model", default="m-a-p/YuE2-3B")
    parser.add_argument("--vae", default="m-a-p/YuE2-Vae")
    parser.add_argument("--device", default="auto")
    args = parser.parse_args()
    if not 1 <= args.clip_seconds <= 120:
        raise SystemExit("--clip-seconds must be between 1 and 120")
    if args.steps < 1 or args.rank < 1:
        raise SystemExit("--steps and --rank must be positive")

    device = torch.device(args.device if args.device != "auto" else
                          "cuda" if torch.cuda.is_available() else "mps" if torch.backends.mps.is_available() else "cpu")
    output = Path(args.output).expanduser().resolve()
    if output.suffix != ".safetensors":
        raise SystemExit("--output must end in .safetensors")
    cache = Path(args.cache).expanduser() if args.cache else output.parent / ".lora-cache"
    from yue2.storage import resolve_model
    from yue2.protocol import SongRequest, token_prefixes
    from yue2.tokenization_yue2 import YuE2TextTokenizer
    model_dir = resolve_model(args.model, local_files_only=True)
    vae_dir = resolve_model(args.vae, local_files_only=True)
    say(f"Training on {device}: {args.steps} steps, rank {args.rank}, {args.clip_seconds:g} s clips")

    songs = latents_for(scan(args.data), cache, vae_dir, device)
    frames = int(round(args.clip_seconds * 25))
    usable = [s for s in songs if len(s[2]) >= 25]
    if not usable:
        raise SystemExit("Every song is shorter than a second")
    weights = [min(len(s[2]), 10 * frames) for s in usable]     # longer songs more often, but not overwhelmingly

    event("stage", stage="loading")
    say("Loading the model")
    from yue2.lean import load_model
    model = load_model(model_dir, device, nar=True)
    tokenizer = YuE2TextTokenizer(model_dir / "qwen.tiktoken")

    def style_of(caption):
        caption = caption or args.style.strip()
        trigger = args.trigger.strip()
        # The trigger leads every caption, once, also when the caption already names it.
        return ", ".join(p for p in (trigger, caption) if p) if trigger.lower() not in caption.lower().split(", ") else caption

    # The prompt as generation without a score builds it; the NAR sees all of it (up to MUSIC_START).
    prompts = {}
    captions = {c for _, c, _ in usable}
    styles = {c: style_of(c) for c in captions}
    if args.caption_dropout > 0:
        styles[None] = ""
    for key, style in styles.items():
        ids = token_prefixes(SongRequest(style=style, lyrics="", cot="off"), tokenizer)
        prompts[key] = (len(ids), prompt_cache(model, ids, device))
        say(f"  prompt {style!r}: {len(ids)} tokens")
    drop_ar(model)
    release(device)

    wrapped = inject(model, args.rank, args.alpha)
    params = [p for m in wrapped.values() for p in (m.down, m.up)]
    say(f"LoRA on {len(wrapped)} projections, {sum(p.numel() for p in params) / 1e6:.1f} M trainable parameters")
    optimizer = torch.optim.AdamW(params, lr=args.lr, weight_decay=0.0)
    model.train()

    metadata = {"base_model": args.model, "trigger_word": args.trigger, "style": args.style, "rank": args.rank,
                "alpha": args.alpha, "learning_rate": args.lr, "clip_seconds": args.clip_seconds,
                "songs": len(usable), "minutes": round(sum(len(s[2]) for s in usable) / 25 / 60, 1),
                "regime": "codec-dropout", "trainer": "tonwerk"}

    def save(path, step):
        tensors = {}
        for prefix, module in wrapped.items():
            tensors[f"{prefix}.lora_down.weight"] = module.down
            tensors[f"{prefix}.lora_up.weight"] = module.up
        yueui_lora.save(path, tensors, {**metadata, "steps": step})
        event("saved", path=str(path), step=step)
        say(f"Saved {path} (step {step})")

    stop = []
    signal.signal(signal.SIGTERM, lambda *_: stop.append("terminated"))
    signal.signal(signal.SIGINT, lambda *_: stop.append("interrupted"))

    rng = random.Random(args.seed)
    generator = torch.Generator().manual_seed(args.seed)
    started, losses = time.perf_counter(), []
    event("stage", stage="training", steps=args.steps)
    done = 0
    for step in range(1, args.steps + 1):
        if stop:
            break
        path, caption, latents = rng.choices(usable, weights)[0]
        length = min(frames, len(latents))
        start = rng.randrange(len(latents) - length + 1)
        z = torch.from_numpy(np.asarray(latents[start:start + length], dtype=np.float32)).to(device)
        key = None if args.caption_dropout > 0 and rng.random() < args.caption_dropout else caption
        prompt_length, prompt = prompts[key]
        # Logit-normal t, as flow-matching models are trained; the model shifts the raw value itself.
        raw = torch.randn((), generator=generator).item()
        t = 1 / (1 + math.exp(-raw))
        noise = torch.randn(z.shape, generator=generator).to(device)
        x_t = (1 - t) * z + t * noise
        # The song tokens and MUSIC_END would sit between prompt and latents; invisible, they only move positions.
        v = velocity(model, prompt, x_t, raw, prompt_length + length + 1, args.checkpointing)
        loss = F.mse_loss(v.float(), noise - z)
        loss.backward()
        lr = args.lr * min(1.0, step / max(1, args.warmup))
        if step > args.warmup:
            lr = args.lr * 0.5 * (1 + math.cos(math.pi * (step - args.warmup) / max(1, args.steps - args.warmup)))
        for group in optimizer.param_groups:
            group["lr"] = lr
        torch.nn.utils.clip_grad_norm_(params, 1.0)
        optimizer.step()
        optimizer.zero_grad(set_to_none=True)
        losses.append(loss.item())
        done = step
        if step == 1 or step % 10 == 0 or step == args.steps:
            per_step = (time.perf_counter() - started) / step
            mean = sum(losses[-50:]) / len(losses[-50:])
            event("step", step=step, steps=args.steps, loss=round(mean, 5), seconds_per_step=round(per_step, 2),
                  remaining_seconds=round(per_step * (args.steps - step)))
            if step == 1 or step % 50 == 0:
                say(f"step {step}/{args.steps}  loss {mean:.4f}  {per_step:.1f} s/step  [{path.name}]")
        if args.save_every and step % args.save_every == 0 and step < args.steps:
            save(output.with_name(f"{output.stem}-step{step}.safetensors"), step)
    if stop:
        if done < 1:
            event("done", stopped=stop[0])
            return
        say(f"Stopped ({stop[0]}) after step {done}")
    save(output, done)
    event("done", path=str(output), steps=done, stopped=stop[0] if stop else None,
          seconds=round(time.perf_counter() - started))


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception as exc:
        event("failed", message=f"{type(exc).__name__}: {exc}")
        raise
