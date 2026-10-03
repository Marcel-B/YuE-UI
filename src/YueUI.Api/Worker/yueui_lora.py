"""LoRA files for YuE2's acoustic (NAR) path: reading them and the changes they make to the weights.

Two layouts are read:

- this app's trainer (yueui_lora_train.py) and ComfyUI-YuE2-Trainer's own files: Hugging Face names,
  ``model.layers.N.nar_self_attn.q_proj.lora_down.weight`` / ``.lora_up.weight`` and so on, alpha in the
  metadata (``"format": "yue2-lora-v1"``);
- ComfyUI's native YuE2 layout, which the ComfyUI trainer publishes and most LoRAs on Hugging Face use:
  ``diffusion_model.model.layers.N.self_attn.qkv_proj`` with q, k and v fused row-wise (and ``mlp.gate_up_proj``
  for gate and up), an ``.alpha`` tensor per module. ComfyUI's diffusion model is the NAR path, so its
  ``self_attn`` is our ``nar_self_attn``.

A LoRA that also touches the AR path (the composer) is refused: the worker applies only acoustic changes, and
half a LoRA would sound like neither.
"""
from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

import torch

FORMAT = "yue2-lora-v1"

# The NAR projections, by the short names yue2.lean.nar_layer_state uses.
LAYER_TARGETS = {
    "q": "nar_self_attn.q_proj", "k": "nar_self_attn.k_proj", "v": "nar_self_attn.v_proj",
    "o": "nar_self_attn.o_proj", "gate": "nar_mlp.gate_proj", "up": "nar_mlp.up_proj", "down": "nar_mlp.down_proj",
}
# Outside the layers: the latent projections and the timestep embedding, which stay in the PyTorch model.
TOP_TARGETS = ("llm2vae", "vae2llm", "time_embedder.mlp.0", "time_embedder.mlp.2")

_BY_SUFFIX = {suffix: short for short, suffix in LAYER_TARGETS.items()}
# ComfyUI's native module names in a layer, and the short names their rows split into.
_NATIVE = {
    "self_attn.qkv_proj": ("q", "k", "v"), "self_attn.o_proj": ("o",),
    "mlp.gate_up_proj": ("gate", "up"), "mlp.down_proj": ("down",),
}
_LAYER = re.compile(r"^model\.layers\.(\d+)\.(.+)$")


@dataclass
class Part:
    """One low-rank pair; its product is split row-wise among ``targets`` (one target unless fused)."""
    down: torch.Tensor
    up: torch.Tensor
    scale: float
    targets: tuple[str, ...]

    def deltas(self, rows):
        full = (self.up.float() @ self.down.float()) * self.scale
        sizes = [rows[t] for t in self.targets]
        if sum(sizes) != full.shape[0]:
            raise ValueError(f"LoRA for {'/'.join(self.targets)} has {full.shape[0]} rows, the model {sum(sizes)}")
        return dict(zip(self.targets, full.split(sizes)))


class Lora:
    """A LoRA file, applied at ``strength`` (1 = as trained)."""

    def __init__(self, path, strength=1.0):
        from safetensors import safe_open
        from safetensors.torch import load_file
        self.path, self.strength = Path(path), float(strength)
        tensors = load_file(str(self.path))
        with safe_open(str(self.path), framework="pt") as handle:
            self.metadata = dict(handle.metadata() or {})
        self.layers: dict[int, list[Part]] = {}
        self.top: list[Part] = []
        unknown = []
        for key, down in tensors.items():
            if not key.endswith(".lora_down.weight"):
                continue
            prefix = key[: -len(".lora_down.weight")]
            up = tensors.get(prefix + ".lora_up.weight")
            if up is None:
                raise ValueError(f"{self.path.name}: {prefix} has no lora_up")
            rank = down.shape[0]
            alpha = tensors.get(prefix + ".alpha")
            alpha = float(alpha) if alpha is not None else float(self.metadata.get("alpha", rank))
            native = prefix.startswith("diffusion_model.")
            name = prefix[len("diffusion_model."):] if native else prefix
            part = lambda targets: Part(down, up, alpha / rank, targets)
            match = _LAYER.match(name)
            if name in TOP_TARGETS:
                self.top.append(part((name,)))
            elif match and native and match.group(2) in _NATIVE:
                self.layers.setdefault(int(match.group(1)), []).append(part(_NATIVE[match.group(2)]))
            elif match and not native and match.group(2) in _BY_SUFFIX:
                self.layers.setdefault(int(match.group(1)), []).append(part((_BY_SUFFIX[match.group(2)],)))
            else:
                unknown.append(prefix)
        if unknown:
            raise ValueError(f"{self.path.name} changes more than the acoustic path ({unknown[0]}"
                             + (f" and {len(unknown) - 1} more" if len(unknown) > 1 else "") + ")")
        if not self.layers and not self.top:
            raise ValueError(f"{self.path.name} contains no LoRA weights")

    @property
    def key(self):
        """What makes two songs' LoRAs the same: file (with its modification time) and strength."""
        return (str(self.path), self.path.stat().st_mtime_ns, self.strength)

    def layer_deltas(self, index, config):
        """{short name: delta} for layer ``index``, already multiplied by the strength."""
        rows = layer_rows(config)
        result = {}
        for part in self.layers.get(index, ()):
            for short, delta in part.deltas(rows).items():
                result[short] = result.get(short, 0) + delta * self.strength
        return result

    def top_deltas(self, model):
        rows = {name: model.get_submodule(name).weight.shape[0] for name in TOP_TARGETS}
        result = {}
        for part in self.top:
            for name, delta in part.deltas(rows).items():
                result[name] = result.get(name, 0) + delta * self.strength
        return result


def layer_rows(config):
    attention = config.num_attention_heads * config.head_dim
    kv = config.num_key_value_heads * config.head_dim
    return {"q": attention, "k": kv, "v": kv, "o": config.hidden_size,
            "gate": config.intermediate_size, "up": config.intermediate_size, "down": config.hidden_size}


def apply(state, deltas):
    """``state`` with the deltas added, each tensor keeping its dtype."""
    changed = dict(state)
    for short, delta in deltas.items():
        weight = state[short]
        changed[short] = (weight.float() + delta.to(weight.device)).to(weight.dtype)
    return changed


def save(path, tensors, metadata):
    """Writes a LoRA in this app's layout (Hugging Face names, alpha in the metadata)."""
    from safetensors.torch import save_file
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".part")
    save_file({k: v.detach().float().cpu().contiguous() for k, v in tensors.items()}, str(temporary),
              metadata={"format": FORMAT, **{k: str(v) for k, v in metadata.items()}})
    temporary.replace(path)                 # a reader never sees half a file
    return path
