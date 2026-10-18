from pathlib import Path
import math
import sqlite3
from datetime import datetime
import pycolmap
from PIL import Image


def is_video_sequence(images):
    descriptions = []
    for path in sorted(images.glob("*.jpg")):
        with Image.open(path) as image:
            descriptions.append(str(image.getexif().get(270, "")))
    sources = {d.split("; timestampUs=")[0] for d in descriptions}
    return (
        bool(descriptions)
        and len(sources) == 1
        and all(d.startswith("Video keyframe; source=") for d in descriptions)
    )


def capture_order(images):
    if is_video_sequence(images):
        return sorted(p.name for p in images.glob("*.jpg"))
    captured = []
    for path in images.glob("*.jpg"):
        with Image.open(path) as image:
            exif = image.getexif().get_ifd(34665)
            try:
                stamp = datetime.strptime(str(exif[36867]), "%Y:%m:%d %H:%M:%S").timestamp()
                fraction = str(exif.get(37521, "0")).strip()
                stamp += float("0." + fraction) if fraction.isdigit() else 0
            except (KeyError, ValueError):
                return None
        captured.append((stamp, path.name))
    captured.sort()
    gaps = [b[0] - a[0] for a, b in zip(captured, captured[1:])]
    # Only a compact, uninterrupted capture burst provides ordering evidence.
    if not gaps or min(gaps) <= 0 or max(gaps) > 30 or captured[-1][0] - captured[0][0] > 600:
        return None
    return [name for _, name in captured]


def calibrate(images: Path, masks: Path, work: Path, count: int, report, seed_name=None):
    database = work / "database.db"
    extraction = pycolmap.FeatureExtractionOptions()
    extraction.max_image_size = 2000
    extraction.num_threads = 6
    extraction.use_gpu = False
    extraction.sift.max_num_features = 12000
    reader = pycolmap.ImageReaderOptions()
    reader.camera_model = "SIMPLE_RADIAL"
    if masks is not None:
        reader.mask_path = masks
    report(26, "提取主体特征并校正镜头畸变")
    pycolmap.extract_features(
        database,
        images,
        camera_mode=pycolmap.CameraMode.SINGLE,
        reader_options=reader,
        extraction_options=extraction,
        device=pycolmap.Device.cpu,
    )
    matching = pycolmap.FeatureMatchingOptions()
    matching.num_threads = 4
    matching.use_gpu = False
    matching.guided_matching = False
    report(32, "匹配重叠视角与闭环")
    # Opposite faces can carry identical printing. Match capture neighbors and
    # the end/start overlap, rather than joining every repeated label globally.
    names = capture_order(images)
    if names:
        pairs = [
            (a, b)
            for i, a in enumerate(names)
            for j, b in enumerate(names)
            if i < j and min(j - i, len(names) - (j - i)) <= 6
        ]
        pair_file = work / "pairs.txt"
        pair_file.write_text("".join(f"{a} {b}\n" for a, b in pairs), encoding="utf-8")
        pairing = pycolmap.ImportedPairingOptions()
        pairing.match_list_path = pair_file
        pycolmap.match_image_pairs(
            database, matching_options=matching, pairing_options=pairing, device=pycolmap.Device.cpu
        )
    else:
        pycolmap.match_exhaustive(database, matching_options=matching, device=pycolmap.Device.cpu)
    options = pycolmap.IncrementalPipelineOptions()
    options.num_threads = 6
    options.random_seed = 0
    options.mapper.init_min_tri_angle = 8
    options.mapper.abs_pose_min_num_inliers = 30
    options.min_model_size = 6
    if seed_name:
        with sqlite3.connect(database) as connection:
            row = connection.execute(
                "SELECT image_id FROM images WHERE name = ?", (seed_name,)
            ).fetchone()
        if row:
            options.init_image_id1 = int(row[0])
    report(42, "联合优化相机位置、焦距与空间点")
    models = pycolmap.incremental_mapping(database, images, work / "sparse", options=options)
    if not models:
        raise ValueError("无法恢复一致的相机运动，原片已保留。")
    best = max(models.values(), key=lambda m: m.num_reg_images())
    error = best.compute_mean_reprojection_error()
    if (
        best.num_reg_images() < max(6, math.ceil(count * 0.85))
        or not math.isfinite(error)
        or error > 2
    ):
        raise ValueError(f"定位质量不足：{best.num_reg_images()}/{count} 个视角；原片已保留。")
    destination = work / "best"
    destination.mkdir(exist_ok=True)
    best.write(destination)
    undistort = pycolmap.UndistortCameraOptions()
    undistort.max_image_size = 2400
    pycolmap.undistort_images(
        work / "dense", destination, images, undistort_options=undistort, num_threads=6
    )
    return {
        "registeredCameras": best.num_reg_images(),
        "inputPhotos": count,
        "initialView": seed_name or "automatic",
        "reprojectionErrorPixels": error,
        "cameraCountIsNotQualityAcceptance": True,
    }
