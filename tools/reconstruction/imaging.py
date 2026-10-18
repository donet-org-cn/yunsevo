"""One pixel coordinate system for segmentation, calibration and texture."""

from pathlib import Path
import hashlib
import json
import shutil
import numpy as np
from PIL import Image, ImageOps

Image.MAX_IMAGE_PIXELS = 80_000_000


def prepare(source: Path, destination: Path):
    paths = sorted(p for p in source.iterdir() if p.suffix.lower() in {".jpg", ".jpeg", ".png"})
    if not 6 <= len(paths) <= 120:
        raise ValueError("需要 6–120 张原始照片。")
    if sum(p.stat().st_size for p in paths) > 512 * 1024 * 1024:
        raise ValueError("原片总大小超过 512 MB。")
    destination.mkdir(parents=True, exist_ok=True)
    manifest = []
    dimensions = set()
    for i, path in enumerate(paths):
        if path.stat().st_size > 40 * 1024 * 1024:
            raise ValueError("单张原片超过 40 MB。")
        with Image.open(path) as original:
            if original.width * original.height > 80_000_000 or max(original.size) > 16000:
                raise ValueError("原片像素超出限制。")
            orientation = original.getexif().get(274, 1)
            description = str(original.getexif().get(270, ""))
            focal35 = float(original.getexif().get_ifd(34665).get(41989, 0) or 0)
            # COLMAP uses stored raster coordinates, regardless of EXIF display
            # orientation. Preserve original JPEG pixels rather than introducing
            # another lossy resample before feature matching and texture baking.
            dimensions.add(original.size)
            name = f"view-{i:03}.jpg"
            if original.format == "JPEG":
                shutil.copyfile(path, destination / name)
            else:
                image = original.convert("RGB")
                image.save(destination / name, quality=98, subsampling=0, exif=original.getexif())
            manifest.append(
                {
                    "name": name,
                    "source": path.name,
                    "orientation": orientation,
                    "width": original.width,
                    "height": original.height,
                    "focal35mm": focal35,
                    "videoKeyframe": description.startswith("Video keyframe; source="),
                    "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                }
            )
    if len(dimensions) != 1:
        raise ValueError("本次重建需使用同一镜头、同一分辨率的照片；请分开提交。")
    (destination.parent / "inputs.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return manifest


def mask_to_raster(mask, orientation):
    inverse = {
        2: Image.Transpose.FLIP_LEFT_RIGHT,
        3: Image.Transpose.ROTATE_180,
        4: Image.Transpose.FLIP_TOP_BOTTOM,
        5: Image.Transpose.TRANSPOSE,
        6: Image.Transpose.ROTATE_90,
        7: Image.Transpose.TRANSVERSE,
        8: Image.Transpose.ROTATE_270,
    }
    image = Image.fromarray(mask)
    return np.asarray(image.transpose(inverse[orientation]) if orientation in inverse else image)


def require_mask_size(mask, camera):
    if mask is None or mask.shape != (camera.height, camera.width):
        raise ValueError("主体轮廓与相机坐标不一致，已停止生成。")


def supported_surface_fraction(mesh, labels):
    # Area, not triangle count: subdivision must not inflate coverage.
    return float(np.sum(mesh.area_faces[np.asarray(labels) >= 0]) / max(mesh.area, 1e-20))


def subdivide_texture_surface(mesh):
    """Bound the footprint of a visibility sample without filling any opening."""
    import trimesh

    span = float(np.linalg.norm(mesh.extents))
    if not np.isfinite(span) or span <= 0 or len(mesh.faces) > 500_000:
        raise ValueError("表面无效或超出贴图处理范围。")
    vertices, faces = trimesh.remesh.subdivide_to_size(
        mesh.vertices, mesh.faces, max_edge=span / 80, max_iter=8
    )
    if len(faces) > 600_000:
        raise ValueError("表面细分过多，已停止贴图以限制内存使用。")
    return trimesh.Trimesh(vertices=vertices, faces=faces, process=False)
