"""Adapter to an independently installed OpenMVS CLI; no native library linking."""

from pathlib import Path
import subprocess
import os
import cv2
import numpy as np
import pycolmap
import trimesh
from imaging import require_mask_size


def command(executable: Path, work: Path, *arguments):
    log = work / (executable.stem + ".stdout.log")
    with log.open("wb") as stream:
        result = subprocess.run(
            [str(executable), "-w", str(work), "--max-threads", "6", *arguments],
            cwd=work,
            stdout=stream,
            stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
    if result.returncode:
        raise ValueError(f"{executable.stem} 失败（{result.returncode}），未发布模型。")


def undistort_masks(work: Path, masks: Path):
    original = pycolmap.Reconstruction(work / "best")
    undistorted = pycolmap.Reconstruction(work / "dense/sparse")
    names = {i.name: i for i in original.images.values()}
    for image in undistorted.images.values():
        camera = undistorted.cameras[image.camera_id]
        source = original.cameras[names[image.name].camera_id]
        mask = cv2.imdecode(np.fromfile(masks / (image.name + ".png"), dtype=np.uint8), 0)
        require_mask_size(mask, source)
        yy, xx = np.mgrid[: camera.height, : camera.width]
        xy = np.c_[xx.ravel(), yy.ravel()].astype(float)
        rays = camera.cam_from_img(xy)
        uv = (
            source.img_from_cam(np.c_[rays, np.ones(len(rays))])
            .reshape(camera.height, camera.width, 2)
            .astype(np.float32)
        )
        transformed = cv2.remap(mask, uv[:, :, 0], uv[:, :, 1], cv2.INTER_NEAREST)
        path = work / "dense/images" / image.name
        encoded = cv2.imencode(".png", transformed)[1]
        encoded.tofile(path.with_suffix(".mask.png"))
        encoded.tofile(Path(str(path) + ".mask.png"))


def reconstruct(work: Path, masks: Path, binaries: Path, report, images: Path):
    def run(name, *arguments):
        suffix = ".exe" if os.name == "nt" else ""
        command(binaries / (name + suffix), work, *arguments)

    undistort_masks(work, masks)
    report(56, "计算主体多视角深度 · CPU")
    run("InterfaceCOLMAP", "-i", str(work / "dense"), "-o", "scene.mvs")
    run(
        "DensifyPointCloud",
        "-i",
        "scene.mvs",
        "-o",
        "dense.mvs",
        "--resolution-level",
        "1",
        "--max-resolution",
        "2400",
        "--number-views",
        "8",
        "--postprocess-dmaps",
        "7",
        "--tower-mode",
        "0",
        "--ignore-mask-label",
        "0",
    )
    report(72, "融合实测表面，保留开口")
    run("ReconstructMesh", "-i", "dense.mvs", "-o", "mesh.mvs", "--close-holes", "0")
    report(80, "按原片细化表面")
    run(
        "RefineMesh",
        "-i",
        "dense.mvs",
        "-m",
        "mesh.ply",
        "-o",
        "refined.mvs",
        "--resolution-level",
        "0",
        "--close-holes",
        "0",
    )
    mesh = trimesh.load_mesh(work / "refined.ply", process=True)
    parts = mesh.split(only_watertight=False)
    if not parts or not np.isfinite(mesh.vertices).all():
        raise ValueError("未得到有效表面，原片已保留。")
    largest = max(parts, key=lambda p: p.area)
    fraction = largest.area / max(mesh.area, 1e-20)
    if len(largest.faces) < 1000 or fraction < 0.65:
        raise ValueError("表面碎片过多，未发布不完整模型。")
    from planar import fill_planar_gaps, photo_supported_patches

    model = pycolmap.Reconstruction(work / "best")
    largest, interpolation = fill_planar_gaps(
        largest, photo_supported_patches(largest, model, images)
    )
    largest.export(work / "subject.ply")
    from validation import silhouette_coverage

    coverage = silhouette_coverage(model, masks, largest)
    return {
        **coverage,
        **interpolation,
        "method": "multi-view depth with photo-supported planar gap interpolation",
        "triangles": len(largest.faces),
        "largestComponentAreaFraction": fraction,
        "watertight": bool(largest.is_watertight),
        "unobservedConcavitiesRecovered": False,
    }
