"""CPU room-video stitching test; outputs an equirectangular panorama, not a 3D mesh."""

from pathlib import Path
import argparse
import json
import subprocess
import tempfile
import cv2 as cv
import numpy as np


def stitch(paths, output):
    cv.ocl.setUseOpenCL(False)
    cv.setNumThreads(4)
    if not 6 <= len(paths) <= 72:
        raise ValueError("全景需要 6–72 张环绕画面。")
    photos = [cv.imdecode(np.fromfile(p, np.uint8), 1) for p in paths]
    if any(im is None or im.size > 24_000_000 for im in photos):
        raise ValueError("全景画面不可读或过大。")
    finder = cv.SIFT_create(nfeatures=5000, contrastThreshold=0.02)
    features = [cv.detail.computeImageFeatures2(finder, im) for im in photos]
    matcher = cv.detail_BestOf2NearestMatcher(False, 0.4)
    matches = matcher.apply2(features)
    matcher.collectGarbage()
    indices = cv.detail.leaveBiggestComponent(features, matches, 0.7)
    if len(indices) < max(6, len(paths) * 0.8):
        raise ValueError("视频画面缺少足够重叠，无法连成完整房间。")
    photos = [photos[i] for i in indices]
    paths = [paths[i] for i in indices]
    ok, cameras = cv.detail_HomographyBasedEstimator().apply(features, matches, None)
    if not ok:
        raise ValueError("全景相机方向估计失败。")
    for camera in cameras:
        camera.R = camera.R.astype(np.float32)
    adjust = cv.detail_BundleAdjusterRay()
    adjust.setConfThresh(0.7)
    adjust.setTermCriteria((cv.TERM_CRITERIA_COUNT + cv.TERM_CRITERIA_EPS, 60, 1e-5))
    ok, cameras = adjust.apply(features, matches, cameras)
    if not ok:
        raise ValueError("全景方向优化失败。")
    rotations = cv.detail.waveCorrect(
        [c.R.copy() for c in cameras], cv.detail.WAVE_CORRECT_HORIZ
    )
    for camera, rotation in zip(cameras, rotations):
        camera.R = rotation
    focal = float(np.median([c.focal for c in cameras]))
    if not 200 < focal < 4000:
        raise ValueError("焦距估计无效。")
    scale = min(focal * 0.65, 4096 / (2 * np.pi))
    warper = cv.PyRotationWarper("spherical", scale)
    warped, masks, corners, excluded = [], [], [], []
    for i, (photo, camera) in enumerate(zip(photos, cameras)):
        # Near-blank walls can acquire an unstable orientation. Keep them in
        # the correspondence graph but do not overwrite stronger image evidence.
        if len(features[i].keypoints) < 250:
            excluded.append(paths[i].name)
            continue
        K = camera.K().astype(np.float32)
        corner, image = warper.warp(
            photo, K, camera.R, cv.INTER_LINEAR, cv.BORDER_REFLECT
        )
        _, mask = warper.warp(
            np.full(photo.shape[:2], 255, np.uint8),
            K,
            camera.R,
            cv.INTER_NEAREST,
            cv.BORDER_CONSTANT,
        )
        warped.append(image)
        masks.append(mask)
        corners.append(corner)
    if len(warped) < 6:
        raise ValueError("有效纹理画面不足。")
    exposure = cv.detail.ExposureCompensator_createDefault(
        cv.detail.ExposureCompensator_GAIN
    )
    exposure.feed(corners, warped, masks)
    seams = cv.detail_DpSeamFinder("COLOR_GRAD").find(
        [im.astype(np.float32) for im in warped], corners, masks
    )
    roi = cv.detail.resultRoi(
        corners=corners, sizes=[(im.shape[1], im.shape[0]) for im in warped]
    )
    if roi[2] * roi[3] > 20_000_000:
        raise ValueError("拼接画布超出预算。")
    blend = cv.detail_MultiBandBlender(0, 5)
    blend.prepare(roi)
    for i, (im, mask, corner, seam) in enumerate(zip(warped, masks, corners, seams)):
        exposure.apply(i, corner, im, mask)
        seam = seam.get() if hasattr(seam, "get") else seam
        blend.feed(
            im.astype(np.int16),
            cv.bitwise_and(mask, cv.dilate(seam, np.ones((3, 3), np.uint8))),
            corner,
        )
    panorama, coverage = blend.blend(None, None)
    panorama = np.clip(panorama, 0, 255).astype(np.uint8)
    height = int(round(2 * np.pi * scale)) // 2
    width = height * 2
    full = np.full((height, width, 3), 24, np.uint8)
    yy, xx = np.where(coverage > 0)
    dy, dx = yy + roi[1], (xx + roi[0] + width // 2) % width
    valid = (dy >= 0) & (dy < height)
    full[dy[valid], dx[valid]] = panorama[yy[valid], xx[valid]]
    output.parent.mkdir(parents=True, exist_ok=True)
    cv.imencode(".jpg", full, [cv.IMWRITE_JPEG_QUALITY, 94])[1].tofile(output)
    report = {
        "method": "SIFT rotation estimation, spherical warp, exposure compensation, seam selection and multiband blending",
        "inputFrames": len(paths),
        "compositedFrames": len(warped),
        "weakTextureFramesExcluded": excluded,
        "width": width,
        "height": height,
        "is3dMesh": False,
        "unobservedAreas": "dark; not synthesized",
        "limitations": "handheld parallax and moving subjects may leave seams",
    }
    output.with_suffix(".json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--ffmpeg", type=Path)
    parser.add_argument("--stride", type=int, default=1)
    args = parser.parse_args()
    if args.source.is_dir():
        if not 1 <= args.stride <= 10:
            parser.error("stride must be 1–10")
        result = stitch(sorted(args.source.glob("*.jpg"))[:: args.stride], args.output)
    else:
        if (
            not args.ffmpeg
            or not args.source.is_file()
            or args.source.stat().st_size > 512 * 1024**2
        ):
            parser.error("a video up to 512 MB and --ffmpeg are required")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(
            dir=args.output.parent, prefix="panorama-"
        ) as temporary:
            frames = Path(temporary)
            subprocess.run(
                [
                    str(args.ffmpeg),
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-i",
                    str(args.source),
                    "-an",
                    "-t",
                    "144",
                    "-vf",
                    "fps=1/2,scale=1280:1280:force_original_aspect_ratio=decrease",
                    "-frames:v",
                    "72",
                    "-q:v",
                    "2",
                    str(frames / "frame-%03d.jpg"),
                ],
                check=True,
                timeout=240,
            )
            result = stitch(sorted(frames.glob("*.jpg")), args.output)
    print(json.dumps(result, ensure_ascii=False))
