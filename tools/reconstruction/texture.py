"""Visibility-tested coherent photo patches, with bounded exposure correction."""

import os

os.environ["OPENBLAS_NUM_THREADS"] = "1"
from pathlib import Path
import sys, json, subprocess, cv2, numpy as np, pycolmap, trimesh
from PIL import Image, ImageOps

root = Path(sys.argv[1])
photos = Path(sys.argv[2])
maskroot = Path(sys.argv[3])
mesh = trimesh.load_mesh(sys.argv[4], process=False)
# Long triangles inserted across planar gaps cannot be visibility-tested at a
# single center or mapped affinely from a perspective photo. Subdivide before
# selecting photos so occlusion boundaries do not smear across the whole patch.
from imaging import subdivide_texture_surface

mesh = subdivide_texture_surface(mesh)
output = Path(sys.argv[5])
model = pycolmap.Reconstruction(root / "best")
metadata_path = Path(sys.argv[4]).with_suffix(".json")
meta = json.loads(metadata_path.read_text(encoding="utf-8")) if metadata_path.is_file() else {}
excluded = meta.get("excludedViews", [])
backside = "--backside" in sys.argv
two_sided = bool(
    meta.get("textureBackFaces", meta.get("watertight") is False and "approximation" not in meta)
)
if backside:
    mesh.invert()
normal_mesh = mesh.copy()
trimesh.smoothing.filter_taubin(normal_mesh, lamb=0.5, nu=0.53, iterations=60)
from trimesh.ray.ray_pyembree import RayMeshIntersector

raycaster = RayMeshIntersector(mesh)
images = sorted((i for i in model.images.values() if i.name not in excluded), key=lambda i: i.name)
points = mesh.triangles_center
normals = normal_mesh.face_normals
scores = []
quality = []
up = []
samples = []
for im in images:
    cam = model.cameras[im.camera_id]
    pose = im.cam_from_world()
    R = pose.rotation.matrix()
    t = pose.translation
    C = -R.T @ t
    orientation = Image.open(photos / im.name).getexif().get(274, 1)
    image_up = {1: [0, -1, 0], 3: [0, 1, 0], 6: [-1, 0, 0], 8: [1, 0, 0]}.get(
        orientation, [0, -1, 0]
    )
    up.append(R.T @ image_up)
    q = points @ R.T + t
    uv = cam.img_from_cam(q)
    view = C - points
    distance = np.linalg.norm(view, axis=1)
    cosine = np.sum(normals * view, axis=1) / distance
    geometric_cosine = np.sum(mesh.face_normals * view, axis=1) / distance
    mask = cv2.imdecode(np.fromfile(maskroot / (im.name + ".png"), dtype=np.uint8), 0)
    w, h = cam.width, cam.height
    valid = (
        (q[:, 2] > 0)
        & (uv[:, 0] > 2)
        & (uv[:, 1] > 2)
        & (uv[:, 0] < w - 3)
        & (uv[:, 1] < h - 3)
        & (cosine > 0.05)
        & (geometric_cosine > 0.02)
    )
    if mask is not None:
        u = np.clip(np.nan_to_num(uv[:, 0]).astype(int), 0, w - 1)
        v = np.clip(np.nan_to_num(uv[:, 1]).astype(int), 0, h - 1)
        valid &= mask[v, u] > 127
    ids = np.flatnonzero(valid)
    if len(ids):
        hits = raycaster.intersects_first(np.broadcast_to(C, (len(ids), 3)), points[ids] - C)
        valid[ids] &= hits == ids
    photo = cv2.imdecode(
        np.fromfile(photos / im.name, dtype=np.uint8),
        cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION,
    )
    photo = cv2.cvtColor(photo, cv2.COLOR_BGR2RGB)
    side = 1000 / max(photo.shape[:2])
    small = cv2.resize(photo, None, fx=side, fy=side)
    sample = small[
        np.clip(np.nan_to_num(uv[:, 1] * side).astype(int), 0, small.shape[0] - 1),
        np.clip(np.nan_to_num(uv[:, 0] * side).astype(int), 0, small.shape[1] - 1),
    ]
    samples.append(sample)
    score = np.where(valid, np.maximum(cosine, 0) ** 3 / (distance**2), 0)
    scores.append(score.astype(np.float32))
scores = np.array(scores)
samples = np.array(samples, dtype=float)
equations = []
differences = []
for_i = range(len(images))
for i in for_i:
    for j in range(i):
        good = (
            (scores[i] > scores[i].max() * 0.25)
            & (scores[j] > scores[j].max() * 0.25)
            & (samples[i].min(axis=1) > 15)
            & (samples[j].min(axis=1) > 15)
        )
        if good.sum() < 100:
            continue
        delta = np.median(np.log(samples[j, good] + 8) - np.log(samples[i, good] + 8), axis=0)
        row = np.zeros(len(images))
        row[i] = 1
        row[j] = -1
        equations.append(row)
        differences.append(delta)
equations.append(np.ones(len(images)))
differences.append(np.zeros(3))
exposure_gains = np.exp(np.clip(np.linalg.lstsq(equations, differences, rcond=None)[0], -0.3, 0.3))
print("Exposure gain range", float(exposure_gains.min()), float(exposure_gains.max()), flush=True)
best = scores.max(axis=0)
selected = []
covered = np.zeros(len(points), bool)
for k in range(min(8, len(images))):
    candidates = (scores >= best * 0.5) & (best > 0)
    gains = np.sum(candidates[:, ~covered], axis=1)
    gains[selected] = 0
    chosen = int(gains.argmax())
    if gains[chosen] == 0:
        break
    selected.append(chosen)
    covered |= candidates[chosen]
    if covered.sum() / max(1, (best > 0).sum()) > 0.995:
        break
if not selected and not backside:
    raise RuntimeError("No visible textured faces")
if not selected:
    selected = [0]
labels = np.array(selected)[scores[selected].argmax(axis=0)]
missing = scores[selected].max(axis=0) <= 0
labels[missing] = scores[:, missing].argmax(axis=0)
adj = mesh.face_adjacency
neighbors = np.full((len(points), 3), -1, int)
counts = np.zeros(len(points), int)
for a, b in adj:
    if counts[a] < 3:
        neighbors[a, counts[a]] = b
        counts[a] += 1
    if counts[b] < 3:
        neighbors[b, counts[b]] = a
        counts[b] += 1
# Grow connected surface regions, then choose one source for the whole region.
seen = np.zeros(len(points), bool)
for seed in np.argsort(-mesh.area_faces):
    if seen[seed]:
        continue
    seen[seed] = True
    patch = [int(seed)]
    for current in patch:
        for n in neighbors[current]:
            if n < 0 or seen[n]:
                continue
            if np.dot(normals[seed], normals[n]) > 0.82:
                seen[n] = True
                patch.append(int(n))
        if len(patch) > 8000:
            break
    region = np.array(patch)
    validscores = scores[np.ix_(selected, region)]
    relative = validscores / np.maximum(best[region], 1e-20)
    votes = np.sum(np.minimum(relative, 1) * mesh.area_faces[region], axis=1)
    pick = int(votes.argmax())
    chosen = selected[pick]
    good = (relative[pick] > 0.18) & (validscores[pick] > 0)
    labels[region[good]] = chosen
selected = list(np.unique(labels[best > 0]))
labels[best <= 0] = -1
from imaging import supported_surface_fraction

texture_coverage = supported_surface_fraction(mesh, labels)
if texture_coverage < 0.9 and not backside:
    raise ValueError("原片覆盖的表面积不足 90%，未发布缺少贴图的模型。")
center = (mesh.bounds[0] + mesh.bounds[1]) * 0.5
up = np.asarray(meta.get("supportPlaneNormal", np.mean(up, axis=0)))
rotation = trimesh.geometry.align_vectors(up, [0, 1, 0])
scene = trimesh.Scene()
for index in selected:
    faces = mesh.faces[labels == index]
    if not len(faces):
        continue
    ids, inverse = np.unique(faces.ravel(), return_inverse=True)
    vertices = mesh.vertices[ids]
    im = images[index]
    cam = model.cameras[im.camera_id]
    pose = im.cam_from_world()
    uv = cam.img_from_cam(vertices @ pose.rotation.matrix().T + pose.translation)
    uv /= np.array([cam.width, cam.height])
    uv[:, 1] = 1 - uv[:, 1]
    image = Image.open(photos / im.name)
    if image.size != (cam.width, cam.height):
        image = ImageOps.exif_transpose(image)
    if image.size != (cam.width, cam.height):
        raise RuntimeError("Texture orientation differs from camera")
    image = Image.fromarray(
        np.clip(
            np.asarray(image.convert("RGB"), dtype=np.float32) * exposure_gains[index], 0, 255
        ).astype(np.uint8)
    )
    limit = min(
        3072,
        round(
            max(image.size)
            * min(
                1,
                np.sqrt(
                    ((64 if two_sided else 128) * 1024 * 1024 / max(1, len(selected)))
                    / (image.width * image.height)
                ),
            )
        ),
    )
    image.thumbnail((limit, limit))
    image.format = "JPEG"
    material = trimesh.visual.material.PBRMaterial(
        baseColorTexture=image, metallicFactor=0, roughnessFactor=1, doubleSided=not two_sided
    )
    visual = trimesh.visual.texture.TextureVisuals(uv=uv, material=material)
    part = trimesh.Trimesh(vertices - center, inverse.reshape(-1, 3), visual=visual, process=False)
    part.apply_transform(rotation)
    scene.add_geometry(part, geom_name="photo-" + str(index))
if np.any(labels < 0):
    part = mesh.submesh([np.flatnonzero(labels < 0)], append=True)
    part.vertices -= center
    part.apply_transform(rotation)
    part.visual = trimesh.visual.ColorVisuals(mesh=part, vertex_colors=[150, 155, 160, 255])
    scene.add_geometry(part, geom_name="unobserved-surface")


def extras(tree):
    tree["extensionsUsed"] = list(set(tree.get("extensionsUsed", []) + ["KHR_materials_unlit"]))
    for material in tree.get("materials", []):
        material.setdefault("extensions", {})["KHR_materials_unlit"] = {}
        material["doubleSided"] = not two_sided
    tree["extras"] = {
        **meta,
        "registeredCameras": model.num_reg_images(),
        "textureViews": len(selected),
        "textureAreaCoverage": texture_coverage,
        "unobservedBackFaces": "neutral gray; not duplicated exterior printing",
    }


if two_sided and not backside:
    back_output = output.with_name(output.stem + "-back.glb")
    subprocess.run(
        [
            sys.executable,
            str(Path(__file__).resolve()),
            *sys.argv[1:5],
            str(back_output),
            "--backside",
        ],
        check=True,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    scene = trimesh.scene.scene.append_scenes([scene, trimesh.load_scene(back_output)])
    back_output.unlink()
output.write_bytes(trimesh.exchange.gltf.export_glb(scene, tree_postprocessor=extras))
print(
    json.dumps(
        {
            "output": str(output),
            "bytes": output.stat().st_size,
            "triangles": len(mesh.faces),
            "textures": len(selected),
            "facesWithImage": float((best > 0).mean()),
        }
    ),
    flush=True,
)
