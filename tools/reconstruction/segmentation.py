"""Foreground masks prevent a static room from dominating a turning object."""

from pathlib import Path
import hashlib
import cv2
import numpy as np
from PIL import Image, ImageOps
from imaging import mask_to_raster

WEIGHTS_SHA256 = "6dbb90523a35330fedd7f1d3dfc66f995213d81b29a5ca8108dbcdd4e37d6c2f"


def consistent_candidates(frames):
    """Viterbi selection keeps a video on the whole object, not a label/background."""
    costs, links = [], []
    for index, choices in enumerate(frames):
        unary = np.array([-score for score, _ in choices])
        if index == 0:
            costs.append(unary)
            links.append(None)
            continue
        transition = np.zeros((len(frames[index - 1]), len(choices)))
        for a, (_, prior) in enumerate(frames[index - 1]):
            for b, (_, current) in enumerate(choices):
                iou = np.count_nonzero(prior & current) / max(1, np.count_nonzero(prior | current))
                transition[a, b] = 1.5 * (1 - iou) + 0.25 * abs(
                    np.log((current.sum() + 1) / (prior.sum() + 1))
                )
        cumulative = costs[-1][:, None] + transition
        links.append(cumulative.argmin(axis=0))
        costs.append(unary + cumulative.min(axis=0))
    chosen = [int(costs[-1].argmin())]
    for index in range(len(frames) - 1, 0, -1):
        chosen.append(int(links[index][chosen[-1]]))
    return list(reversed(chosen))


def segment(images: Path, output: Path, checkpoint: Path, report):
    import torch
    from mobile_sam import sam_model_registry, SamPredictor

    if hashlib.sha256(checkpoint.read_bytes()).hexdigest() != WEIGHTS_SHA256:
        raise ValueError("主体分割模型校验失败。")
    torch.set_num_threads(3)
    model = sam_model_registry["vit_t"](checkpoint=str(checkpoint)).eval()
    predictor = SamPredictor(model)
    output.mkdir(parents=True, exist_ok=True)
    paths = sorted(images.glob("*.jpg"))
    from calibration import is_video_sequence

    sequential = is_video_sequence(images)
    frames, invalid = [], []
    for i, path in enumerate(paths):
        with Image.open(path) as photo:
            orientation = photo.getexif().get(274, 1)
            image = cv2.cvtColor(
                np.asarray(ImageOps.exif_transpose(photo).convert("RGB")), cv2.COLOR_RGB2BGR
            )
        h, w = image.shape[:2]
        scale = min(1, 1200 / max(h, w))
        small = cv2.resize(image, (round(w * scale), round(h * scale)))
        sh, sw = small.shape[:2]
        predictor.set_image(cv2.cvtColor(small, cv2.COLOR_BGR2RGB))
        masks, scores, _ = predictor.predict(
            point_coords=np.array([[sw * 0.5, sh * 0.5]]),
            point_labels=np.array([1]),
            box=np.array([sw * 0.15, sh * 0.05, sw * 0.85, sh * 0.93]),
            multimask_output=True,
        )
        candidates = [
            (float(score), (mask * 255).astype(np.uint8))
            for score, mask in zip(scores, masks)
            if 0.015 < mask.mean() < 0.65 and score >= 0.75
        ]
        # A narrow box plus a center point can segment only printed lettering.
        # Include an unambiguous broad-box proposal so folded flaps and objects
        # drifting toward the frame edge remain part of the foreground.
        whole_masks, whole_scores, _ = predictor.predict(
            box=np.array([sw * 0.05, sh * 0.05, sw * 0.95, sh * 0.93]), multimask_output=False
        )
        candidates.extend(
            (float(score), (mask * 255).astype(np.uint8))
            for score, mask in zip(whole_scores, whole_masks)
            if 0.015 < mask.mean() < 0.65 and score >= 0.75
        )
        if not candidates:
            invalid.append(path)
            continue
        frames.append((path, orientation, w, h, candidates))
        report(5 + int(20 * (i + 1) / len(paths)), f"识别主体 {i + 1}/{len(paths)}")
    if not frames:
        raise ValueError("未识别到有效主体。")
    tiny = [
        [
            (score, cv2.resize(mask, (128, 128), interpolation=cv2.INTER_NEAREST) > 0)
            for score, mask in frame[4]
        ]
        for frame in frames
    ]
    choices = (
        consistent_candidates(tiny)
        if sequential
        else [int(np.argmax([s for s, _ in f[4]])) for f in frames]
    )
    areas = []
    for (path, orientation, w, h, candidates), chosen in zip(frames, choices):
        mask = candidates[chosen][1]
        sh, sw = mask.shape
        # Fill only the 2D foreground contour. This is not a 3D hole-filling step.
        contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        filled = np.zeros_like(mask)
        for contour in contours:
            if cv2.pointPolygonTest(contour, (sw * 0.5, sh * 0.5), False) >= 0:
                cv2.drawContours(filled, [contour], -1, 255, -1)
        if filled.any():
            mask = filled
        areas.append(float((mask > 0).mean()))
        raster_mask = mask_to_raster(
            cv2.resize(mask, (w, h), interpolation=cv2.INTER_NEAREST), orientation
        )
        cv2.imencode(".png", raster_mask)[1].tofile(output / (path.name + ".png"))
    # Partial-label masks previously carved away entire bottles. Do not accept a
    # large unexplained area jump as an ordinary camera shake.
    median = float(np.median(areas))
    invalid.extend(
        frame[0] for frame, area in zip(frames, areas) if area < median * 0.3 or area > median * 3
    )
    if invalid and (
        not sequential
        or len(invalid) > max(1, int(len(paths) * 0.1))
        or len(paths) - len(invalid) < 6
    ):
        raise ValueError("主体轮廓大小变化过大，可能只识别了标签或混入背景；原片已保留。")
    # These are validated scratch copies from imaging.prepare, never originals.
    for path in invalid:
        if path.parent.resolve() != images.resolve():
            raise ValueError("Invalid scratch image path")
        path.unlink()
        (output / (path.name + ".png")).unlink(missing_ok=True)
    return {
        "segmentationExcludedFrames": [p.name for p in invalid],
        "originalInputPhotos": len(paths),
    }
