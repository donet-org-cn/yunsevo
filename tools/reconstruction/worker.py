"""Bounded, CPU-only high-quality worker. The NativeAOT host owns authentication."""

from pathlib import Path
import importlib.util
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
os.environ.setdefault("OMP_NUM_THREADS", "6")
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent


def configuration():
    path = Path(
        os.environ.get("YUNSEVO_QUALITY_CONFIG", str(HERE / "worker.local.json"))
    )
    config = json.loads(path.read_text(encoding="utf-8-sig"))
    checkpoint = Path(config["checkpoint"]).resolve()
    binaries = Path(config["openMvsDirectory"]).resolve()
    suffix = ".exe" if os.name == "nt" else ""
    if not checkpoint.is_file() or any(
        not (binaries / (tool + suffix)).is_file()
        for tool in (
            "InterfaceCOLMAP",
            "DensifyPointCloud",
            "ReconstructMesh",
            "RefineMesh",
        )
    ):
        raise ValueError("高精度工作进程尚未安装完整。")
    for module in (
        "pycolmap",
        "mobile_sam",
        "torch",
        "cv2",
        "trimesh",
        "skimage",
        "embreex",
    ):
        if importlib.util.find_spec(module) is None:
            raise ValueError("高精度工作进程缺少依赖：" + module)
    return checkpoint, binaries


def video_configuration():
    path = Path(
        os.environ.get("YUNSEVO_QUALITY_CONFIG", str(HERE / "worker.local.json"))
    )
    config = json.loads(path.read_text(encoding="utf-8-sig"))
    ffmpeg, ffprobe = (
        Path(config.get("ffmpeg", "")).resolve(),
        Path(config.get("ffprobe", "")).resolve(),
    )
    if (
        not ffmpeg.is_file()
        or not ffprobe.is_file()
        or importlib.util.find_spec("piexif") is None
    ):
        raise ValueError("服务端尚未配置视频选帧工具。")
    return ffmpeg, ffprobe


def report(value, message):
    print(f"{value}\t{message}", flush=True)


def main():
    if sys.argv[1:] == ["--capabilities"]:
        try:
            configuration()
            try:
                video_configuration()
                video_available = True
            except Exception:
                video_available = False
            print(
                json.dumps(
                    {
                        "cpu": True,
                        "vulkan": False,
                        "experimental": True,
                        "highQuality": True,
                        "highQualityVideo": video_available,
                    }
                )
            )
        except Exception:
            print(
                json.dumps(
                    {
                        "cpu": False,
                        "vulkan": False,
                        "experimental": True,
                        "highQuality": False,
                    }
                )
            )
        return
    if len(sys.argv) != 6:
        raise ValueError("Expected input output backend quality subject")
    source, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    backend, quality, subject = sys.argv[3], int(sys.argv[4]), sys.argv[5]
    if backend not in ("cpu", "auto") or quality not in (2, 3) or subject != "object":
        raise ValueError("高精度重建目前支持物体／设备和 CPU；轮廓模式须明确选择。")
    checkpoint, binaries = configuration()
    algorithm_revision = hashlib.sha256(
        b"".join(
            p.name.encode() + b"\0" + p.read_bytes() for p in sorted(HERE.glob("*.py"))
        )
    ).hexdigest()
    output.parent.mkdir(parents=True, exist_ok=True)
    if shutil.disk_usage(output.parent).free < 8 * 1024**3:
        raise ValueError("高精度重建需要至少 8 GB 临时磁盘空间。")
    from imaging import prepare
    from segmentation import segment
    from calibration import calibrate, capture_order
    from surface import reconstruct

    # TemporaryDirectory stays below this job's output directory. Cancellation
    # kills the complete process tree; the server cleans the job's scratch folder.
    scratch = output.parent / "quality-work"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=scratch, prefix="run-") as temporary:
        work = Path(temporary).resolve()
        if work.parent != scratch.resolve():
            raise ValueError("Invalid reconstruction workspace")
        report(1, "统一原片方向，校验图片")
        videos = [
            p
            for p in source.iterdir()
            if p.suffix.lower() in {".mp4", ".mov", ".m4v", ".webm"}
        ]
        video_stats = {}
        if videos:
            if len(videos) != 1:
                raise ValueError("每次仅支持一个连续视频。")
            from video import extract

            video_stats = extract(
                videos[0], work / "frames", *video_configuration(), report
            )
            source = work / "frames"
        manifest = prepare(source, work / "images")
        segmentation_stats = segment(
            work / "images", work / "masks", checkpoint, report
        )
        manifest = [
            item for item in manifest if (work / "images" / item["name"]).is_file()
        ]

        def run(candidate, script, *args):
            with (candidate / (script + ".log")).open("wb") as log:
                result = subprocess.run(
                    [sys.executable, str(HERE / script), *map(str, args)],
                    stdout=log,
                    stderr=log,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                )
            if result.returncode:
                raise ValueError("表面或贴图生成未通过，原片已保留。")

        import pycolmap
        import trimesh
        from validation import camera_checks, silhouette_coverage

        # Stationary surroundings disambiguate repeated labels during an orbit.
        # A turning object instead needs foreground-only tracks. Neither model
        # is accepted merely because it registered more cameras.
        ordered = capture_order(work / "images") or sorted(
            p.name for p in (work / "images").glob("*.jpg")
        )
        attempts = [
            ("scene", False, None),
            ("scene-seed-start", False, ordered[0]),
            ("scene-seed-quarter", False, ordered[len(ordered) // 4]),
            ("foreground", True, None),
        ]
        for attempt, (label, foreground_only, seed_name) in enumerate(attempts):
            candidate = work / label
            candidate.mkdir()
            try:
                if seed_name and (work / "scene/database.db").is_file():
                    # Reuse verified correspondences; only retry initialization.
                    shutil.copyfile(
                        work / "scene/database.db", candidate / "database.db"
                    )
                    report(40, "定位结果未通过轮廓检查，改用另一初始视角复核")
                if foreground_only:
                    report(26, "完整场景未通过质量检查，排除背景重新定位主体")
                stats = calibrate(
                    work / "images",
                    work / "masks" if foreground_only else None,
                    candidate,
                    len(manifest),
                    report,
                    seed_name,
                )
                stats.update(video_stats)
                stats.update(segmentation_stats)
                model = pycolmap.Reconstruction(candidate / "best")
                stats.update(
                    camera_checks(model, work / "masks", capture_order(work / "images"))
                )
                stats.update(
                    {
                        "backend": "cpu",
                        "experimental": True,
                        "scale": "relative, not a survey measurement",
                        "calibrationScope": (
                            "foreground" if foreground_only else "scene"
                        ),
                        "independentGeometryAccuracyVerified": False,
                        "algorithmRevision": algorithm_revision,
                        "requestedQuality": quality,
                        "inputPhotoHashes": [
                            {"name": item["name"], "sha256": item["sha256"]}
                            for item in manifest
                        ],
                        "foregroundSegmentation": "Local MobileSAM vit_t; masks only, no generated photograph pixels",
                        "textureSource": "original photographs; no generative image replacement",
                    }
                )
                temporary_model = candidate / "model.glb"
                if quality == 3:
                    report(58, "由多视角轮廓估计外形（不恢复内凹）")
                    run(
                        candidate,
                        "silhouette.py",
                        candidate,
                        work / "masks",
                        candidate / "surface.ply",
                    )
                    stats.update(
                        json.loads(
                            (candidate / "surface.json").read_text(encoding="utf-8")
                        )
                    )
                    from support import constrain_support

                    hull, support = constrain_support(
                        trimesh.load_mesh(candidate / "surface.ply"),
                        model,
                        work / "images",
                        work / "masks",
                    )
                    stats.update(support)
                    stats.update(
                        {"triangles": len(hull.faces), "vertices": len(hull.vertices)}
                    )
                    hull.export(candidate / "surface.ply")
                    stats.update(
                        silhouette_coverage(
                            model,
                            work / "masks",
                            hull,
                            stats.get("excludedViews", []),
                            min_median=0.9,
                            min_p10=0.85,
                        )
                    )
                    stats["approximation"] = (
                        "Outer silhouette only; unseen concavities and bottom are not measured"
                    )
                    surface = candidate / "surface.ply"
                else:
                    stats.update(
                        reconstruct(
                            candidate, work / "masks", binaries, report, work / "images"
                        )
                    )
                    stats["textureBackFaces"] = not stats["watertight"]
                    surface = candidate / "subject.ply"
                surface.with_suffix(".json").write_text(
                    json.dumps(stats, ensure_ascii=False), encoding="utf-8"
                )
                report(91, "检查遮挡并生成连续原片贴图")
                run(
                    candidate,
                    "texture.py",
                    candidate,
                    work / "images",
                    work / "masks",
                    surface,
                    temporary_model,
                )
                break
            except ValueError as error:
                if attempt == len(attempts) - 1:
                    raise
                print(
                    label + " candidate rejected: " + str(error),
                    file=sys.stderr,
                    flush=True,
                )
        if (
            not temporary_model.is_file()
            or not 1024 <= temporary_model.stat().st_size <= 240 * 1024**2
        ):
            raise ValueError("模型文件无效或超过 240 MB。")
        report(98, "保存模型与质量报告")
        shutil.copyfile(temporary_model, output.with_suffix(".partial"))
        os.replace(output.with_suffix(".partial"), output)
        output.with_suffix(".quality.json").write_text(
            json.dumps(stats, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        report(
            100,
            (
                "轮廓外形已生成；未拍到的内凹和底面不属于实测结果"
                if quality == 3
                else "模型已生成；请旋转检查表面和贴图"
            ),
        )


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(str(error), file=sys.stderr, flush=True)
        sys.exit(1)
