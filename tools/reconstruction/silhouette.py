"""Opt-in outer silhouette approximation; never a replacement for measured concavities."""

import os

os.environ["OPENBLAS_NUM_THREADS"] = "1"
from pathlib import Path
import sys, json, cv2, numpy as np, pycolmap, trimesh
from skimage.measure import marching_cubes
from scipy.ndimage import gaussian_filter

root = Path(sys.argv[1])
masks = Path(sys.argv[2])
out = Path(sys.argv[3])
model = pycolmap.Reconstruction(root / "best")
views = []
A = np.zeros((3, 3))
b = np.zeros(3)
for im in sorted(model.images.values(), key=lambda i: i.name):
    mask = cv2.imdecode(np.fromfile(masks / (im.name + ".png"), dtype=np.uint8), 0)
    if mask is None:
        continue
    cam = model.cameras[im.camera_id]
    pose = im.cam_from_world()
    R = pose.rotation.matrix()
    t = pose.translation
    C = -R.T @ t
    if mask.shape != (cam.height, cam.width):
        raise RuntimeError("Mask orientation does not match calibrated image: " + im.name)
    size = (
        round(cam.width * min(1, 800 / max(cam.width, cam.height))),
        round(cam.height * min(1, 800 / max(cam.width, cam.height))),
    )
    mask = cv2.resize(mask, size, interpolation=cv2.INTER_NEAREST) > 127
    ys, xs = np.nonzero(mask)
    if len(xs) < 100:
        continue
    centroid = np.array([xs.mean() * cam.width / size[0], ys.mean() * cam.height / size[1]])
    ray = R.T @ np.r_[cam.cam_from_img(centroid), 1.0]
    ray /= np.linalg.norm(ray)
    P = np.eye(3) - np.outer(ray, ray)
    A += P
    b += P @ C
    sdf = cv2.distanceTransform(mask.astype(np.uint8), cv2.DIST_L2, 5) - cv2.distanceTransform(
        (~mask).astype(np.uint8), cv2.DIST_L2, 5
    )
    views.append((im, cam, R, t, C, sdf, size, max(xs.max() - xs.min(), ys.max() - ys.min()) * 0.5))
center = np.linalg.solve(A, b)
excluded = []
# A few falsely registered views must not carve away the entire subject. Only
# reject gross disagreement with the foreground bounding box, not concavities.
for iteration in range(2):
    kept = []
    A = np.zeros((3, 3))
    b = np.zeros(3)
    for v in views:
        im, cam, R, t, C, sdf, size, r = v
        q = R @ center + t
        uv = cam.img_from_cam(q)
        ys, xs = np.nonzero(sdf > 0)
        if uv is None:
            outside = True
        else:
            uv = uv * np.array(size) / [cam.width, cam.height]
            margin = max(xs.max() - xs.min(), ys.max() - ys.min()) * 0.15
            outside = (
                uv[0] < xs.min() - margin
                or uv[0] > xs.max() + margin
                or uv[1] < ys.min() - margin
                or uv[1] > ys.max() + margin
            )
        if outside:
            excluded.append(im.name)
            continue
        kept.append(v)
        centroid = np.array([xs.mean() * cam.width / size[0], ys.mean() * cam.height / size[1]])
        ray = R.T @ np.r_[cam.cam_from_img(centroid), 1.0]
        ray /= np.linalg.norm(ray)
        P = np.eye(3) - np.outer(ray, ray)
        A += P
        b += P @ C
    if len(kept) < max(6, len(model.images) * 0.85):
        raise RuntimeError("Too many subject-inconsistent camera poses")
    views = kept
    center = np.linalg.solve(A, b)
radii = []
for im, cam, R, t, C, sdf, size, r in views:
    radii.append(r * (R @ center + t)[2] / cam.focal_length * cam.width / size[0])
radius = float(np.median(radii) * 1.3)
N = 192
step = 2 * radius / (N - 1)
lo = center - radius
field = np.empty((N, N, N), np.float32)
print(json.dumps({"views": len(views), "center": center.tolist(), "radius": radius}), flush=True)
for start in range(0, N, 8):
    z, y, x = np.meshgrid(
        np.arange(start, min(N, start + 8)), np.arange(N), np.arange(N), indexing="ij"
    )
    points = np.stack([x.ravel(), y.ravel(), z.ravel()], axis=1) * step + lo
    first = np.full(len(points), 1e6, np.float32)
    second = first.copy()
    for im, cam, R, t, C, sdf, size, r in views:
        q = points @ R.T + t
        uv = cam.img_from_cam(q)
        uv *= np.array(size) / [cam.width, cam.height]
        distances = (
            cv2.remap(
                sdf,
                uv[:, 0].astype(np.float32).reshape(-1, N),
                uv[:, 1].astype(np.float32).reshape(-1, N),
                cv2.INTER_LINEAR,
                borderMode=cv2.BORDER_CONSTANT,
                borderValue=-1000,
            ).ravel()
            * q[:, 2]
            / (cam.focal_length * size[0] / cam.width)
        )
        old = first
        first = np.minimum(first, distances)
        second = np.minimum(second, np.maximum(old, distances))
    field[start : start + len(z)] = second.reshape(z.shape)
    print("Carved " + str(min(N, start + 8)) + "/" + str(N), flush=True)
field = gaussian_filter(field, 0.65)
verts, faces, _, _ = marching_cubes(field, 0, spacing=(step, step, step))
verts = verts[:, ::-1] + lo
mesh = trimesh.Trimesh(verts, faces, process=True)
mesh = max(mesh.split(only_watertight=False), key=lambda m: m.area)
trimesh.repair.fix_normals(mesh)
mesh.export(out)
(out.with_suffix(".json")).write_text(
    json.dumps(
        {
            "method": "multi-view silhouette intersection",
            "views": len(views),
            "excludedViews": excluded,
            "vertices": len(mesh.vertices),
            "triangles": len(mesh.faces),
            "watertight": bool(mesh.is_watertight),
            "unobservedConcavitiesRecovered": False,
            "center": center.tolist(),
            "radius": radius,
        },
        indent=2,
    )
)
print("Saved " + str(out), flush=True)
