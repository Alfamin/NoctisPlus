"""Export ONLY the pinned music CLAP audio tower; check float and INT8 parity on a real preview.
Research tooling, not part of model setup in the app. Apache-2.0 source checkpoint retained unchanged.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time

import numpy as np
import torch
from transformers import ClapModel
from onnxruntime.quantization import QuantType, quantize_dynamic
import onnxruntime as ort
from benchmark import features, pcm, normalize

p = argparse.ArgumentParser()
p.add_argument("--work", type=Path, required=True)
p.add_argument("--ffmpeg", required=True)
args = p.parse_args()
folder = args.work / "models/clap-music"
source = json.loads((folder / "source.json").read_text())
with (folder / "pytorch_model.bin").open("rb") as f:
    assert hashlib.file_digest(f, "sha256").hexdigest() == source["sha256"]
torch.set_num_threads(4)
model = ClapModel.from_pretrained(str(folder), local_files_only=True).eval()


class AudioOnly(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.audio = m.audio_model
        self.projection = m.audio_projection

    def forward(self, input_features):
        pooled = self.audio(input_features=input_features, return_dict=False)[1]
        projected = self.projection(pooled)
        return torch.nn.functional.normalize(projected, dim=-1)


audio = AudioOnly(model).eval()
manifest = json.loads((args.work / "manifest.json").read_text())
sample = next(t for t in manifest["tracks"] if "audio" in t)
x = features(pcm(args.ffmpeg, sample["audio"], 48000)[:480000])
with torch.inference_mode():
    reference = audio(torch.from_numpy(x)).numpy()[0]
target = folder / "audio-model-fp32.onnx"
start = time.perf_counter()
if not target.exists():
    torch.onnx.export(audio, (torch.from_numpy(x),), str(target), input_names=["input_features"], output_names=["audio_embeds"],
                      opset_version=17, dynamo=False)
quantized = folder / "audio-model-int8.onnx"
if not quantized.exists():
    quantize_dynamic(str(target), str(quantized), weight_type=QuantType.QInt8, op_types_to_quantize=["MatMul", "Gemm"])
opts = ort.SessionOptions()
opts.intra_op_num_threads = 4
report = dict(source=source, seconds=time.perf_counter()-start, variants=[])
for file in (target, quantized):
    session = ort.InferenceSession(str(file), opts, providers=["CPUExecutionProvider"])
    result = session.run(None, {"input_features": x})[0][0]
    cosine = float(normalize(reference) @ normalize(result))
    assert cosine > (.99 if "int8" in file.name else .9999), (file, cosine)
    with file.open("rb") as f:
        sha = hashlib.file_digest(f, "sha256").hexdigest()
    report["variants"].append(dict(file=file.name, bytes=file.stat().st_size, sha256=sha, cosine_to_torch=cosine))
(folder / "export.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
