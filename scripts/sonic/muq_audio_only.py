"""Official MuQ-MuLan AUDIO weights only. No text checkpoint, tokenizer or automatic HF downloads.

Requires the pinned original checkpoint, base-config.json, and muq==0.1.0 / torch+torchaudio==2.9.1.
The base config initializes a skeleton, then EVERY audio tensor loads strictly from the MuLan checkpoint.
MuQ code MIT, checkpoint CC-BY-NC-4.0. This does not remove or work around the model's use restrictions.
"""
import json
import sys
import types


def load_audio_only(folder):
    import numpy as np
    import torch
    # muq.__init__ eagerly imports its unused text tower, whose x_clip tokenizer eagerly imports torchvision.
    # No text tower is constructed or used here. Avoid that unrelated vision/tokenizer dependency chain.
    unused = types.ModuleType("x_clip.tokenizer")
    unused.tokenizer = types.SimpleNamespace(vocab_size=49408)
    sys.modules.setdefault("x_clip", types.ModuleType("x_clip"))
    sys.modules["x_clip.tokenizer"] = unused
    from muq import MuQ
    from muq.muq_mulan.models.audio import AudioSpectrogramTransformerPretrained
    torch.set_num_threads(4)
    config = json.loads((folder / "config.json").read_text())
    base = json.loads((folder / "base-config.json").read_text())
    original = AudioSpectrogramTransformerPretrained._init_pretrained_model
    def skeleton(self, name):
        if name != config["audio_model"]["name"]:
            raise ValueError("unexpected backbone")
        self.model = MuQ(base)
    AudioSpectrogramTransformerPretrained._init_pretrained_model = skeleton
    try:
        audio = AudioSpectrogramTransformerPretrained(model_name=config["audio_model"]["name"],
            model_dim=config["audio_model"]["model_dim"], use_layer_idx=config["audio_model"]["use_layer_idx"],
            **config["audio_transformer"], frozen_pretrained=False)
    finally:
        AudioSpectrogramTransformerPretrained._init_pretrained_model = original
    state = torch.load(folder / "pytorch_model.bin", map_location="cpu", weights_only=True)
    audio.load_state_dict({k[len("mulan.audio."):]: v for k,v in state.items() if k.startswith("mulan.audio.")}, strict=True)
    projection = torch.nn.Linear(config["audio_transformer"]["dim"], config["mulan"]["dim_latent"])
    projection.load_state_dict({k[len("mulan.audio_to_latents."):]: v for k,v in state.items() if k.startswith("mulan.audio_to_latents.")}, strict=True)
    del state
    audio.eval(); projection.eval()
    def infer(wave):
        if len(wave) < 240000:
            wave = np.tile(wave, int(np.ceil(240000 / len(wave))))[:240000]
        with torch.inference_mode():
            value = projection(audio(torch.from_numpy(wave[:240000]).unsqueeze(0)))
            return torch.nn.functional.normalize(value, dim=-1).numpy()[0]
    return infer, 24000
