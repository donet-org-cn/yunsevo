"""Verify exported inference against the pinned local MobileSAM, not a mock."""

import ctypes
import os
from pathlib import Path
import sys
import numpy as np
import torch
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools/reconstruction"))
from export_mobile_masks import Decoder
from mobile_sam import sam_model_registry

photos, checkpoint, models, runtime = map(Path, sys.argv[1:])
torch.set_num_threads(2)
with os.add_dll_directory(str(runtime.resolve())):
    native = ctypes.CDLL(str((runtime / "yunsevo_reconstruction.dll").resolve()))
native.yv_mask_input.argtypes = [
    ctypes.c_char_p,
    ctypes.POINTER(ctypes.c_float),
    ctypes.c_int,
    ctypes.POINTER(ctypes.c_int),
]
model = sam_model_registry["vit_t"](checkpoint=str(checkpoint)).eval()
decoder = Decoder(model).eval()
options = ort.SessionOptions()
options.intra_op_num_threads = 2
encoder_ort = ort.InferenceSession(
    str(models / "encoder.onnx"), options, providers=["CPUExecutionProvider"]
)
decoder_ort = ort.InferenceSession(
    str(models / "decoder.onnx"), options, providers=["CPUExecutionProvider"]
)
paths = sorted(photos.glob("*.jpg"))
for file in [paths[0], paths[len(paths) // 2], paths[-1]]:
    pixels = np.zeros((1, 3, 1024, 1024), np.float32)
    size = (ctypes.c_int * 2)()
    assert (
        native.yv_mask_input(
            str(file).encode("utf-8"),
            pixels.ctypes.data_as(ctypes.POINTER(ctypes.c_float)),
            pixels.size,
            size,
        )
        == 0
    )
    dims = np.array(size, dtype=np.float32)
    with torch.no_grad():
        expected_embedding = model.image_encoder(torch.from_numpy(pixels))
        expected_masks, expected_scores = decoder(
            expected_embedding, torch.from_numpy(dims)
        )
    embedding = encoder_ort.run(None, {"pixels": pixels})[0]
    masks, scores = decoder_ort.run(None, {"embedding": embedding, "size": dims})
    reference = expected_masks.numpy() > 0
    actual = masks > 0
    agreement = (reference & actual).sum() / max(1, (reference | actual).sum())
    assert agreement > 0.999, agreement
    assert np.max(np.abs(expected_scores.numpy() - scores)) < 0.001
    print(
        f"PASS exported/native-preprocessed mask parity: {file.name}, IoU {agreement:.7f}",
        flush=True,
    )
