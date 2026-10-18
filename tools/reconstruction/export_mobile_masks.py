"""Export fixed MobileSAM foreground inference. No photograph synthesis or editing.

Uses the same pinned weights as the server. The two prompt proposals mirror
segmentation.py; output is four 256px mask logits and confidence scores.
"""

import argparse
import hashlib
import json
import shutil
from pathlib import Path

import torch
from mobile_sam import sam_model_registry
from segmentation import WEIGHTS_SHA256


class Decoder(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.prompt = model.prompt_encoder
        self.decoder = model.mask_decoder

    def forward(self, embedding, size):
        # size is the unpadded resized image [width, height], longest side 1024.
        point = size.reshape(1, 1, 2) * 0.5
        narrow = size.repeat(2) * size.new_tensor([0.15, 0.05, 0.85, 0.93])
        broad = size.repeat(2) * size.new_tensor([0.05, 0.05, 0.95, 0.93])
        sparse, dense = self.prompt(
            (point, torch.ones((1, 1), dtype=torch.int64)), narrow[None], None
        )
        masks, scores = self.decoder(
            embedding, self.prompt.get_dense_pe(), sparse, dense, True
        )
        sparse, dense = self.prompt(None, broad[None], None)
        whole, whole_score = self.decoder(
            embedding, self.prompt.get_dense_pe(), sparse, dense, False
        )
        return torch.cat([masks[0], whole[0]]), torch.cat([scores[0], whole_score[0]])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--license",
        type=Path,
        required=True,
        help="MobileSAM Apache-2.0 LICENSE from the pinned source checkout",
    )
    args = parser.parse_args()
    if hashlib.sha256(args.checkpoint.read_bytes()).hexdigest() != WEIGHTS_SHA256:
        raise ValueError("Unexpected MobileSAM checkpoint")
    torch.set_num_threads(3)
    model = sam_model_registry["vit_t"](checkpoint=str(args.checkpoint)).eval()
    args.output.mkdir(parents=True, exist_ok=True)
    with torch.no_grad():
        # Warm up cached attention biases before tracing TinyViT.
        pixels = torch.zeros(1, 3, 1024, 1024)
        embedding = model.image_encoder(pixels)
        torch.onnx.export(
            model.image_encoder,
            pixels,
            args.output / "encoder.onnx",
            input_names=["pixels"],
            output_names=["embedding"],
            opset_version=17,
            dynamo=False,
        )
        torch.onnx.export(
            Decoder(model).eval(),
            (embedding, torch.tensor([768.0, 1024.0])),
            args.output / "decoder.onnx",
            input_names=["embedding", "size"],
            output_names=["masks", "scores"],
            opset_version=17,
            dynamo=False,
        )
    manifest = {
        "model": "MobileSAM vit_t",
        "checkpointSha256": WEIGHTS_SHA256,
        "purpose": "foreground masks only; original pixels unchanged",
        "files": {
            p.name: hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(args.output.glob("*.onnx"))
        },
    }
    (args.output / "manifest.json").write_text(
        json.dumps(manifest, indent=2), encoding="utf-8"
    )
    shutil.copyfile(args.license, args.output / "LICENSE-MobileSAM.txt")
    print(json.dumps(manifest), flush=True)


if __name__ == "__main__":
    main()
