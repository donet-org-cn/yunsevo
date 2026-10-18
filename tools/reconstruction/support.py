"""Constrain an approximate visual hull to a photographed support surface."""

import cv2
import numpy as np
from PIL import Image


def fit_horizontal_plane(points, up, height):
    if len(points) < 40:
        return None
    best = []
    rng = np.random.default_rng(0)
    for _ in range(1000):
        selected = points[rng.choice(len(points), 3, replace=False)]
        normal = np.cross(selected[1] - selected[0], selected[2] - selected[0])
        normal /= max(np.linalg.norm(normal), 1e-20)
        if abs(normal @ up) < 0.95:
            continue
        indices = np.flatnonzero(np.abs((points - selected[0]) @ normal) < height * 0.005)
        if len(indices) > len(best):
            best = indices
    if len(best) < max(40, len(points) * 0.5):
        return None
    selected = points[best]
    origin = selected.mean(axis=0)
    _, _, axes = np.linalg.svd(selected - origin, full_matrices=False)
    normal = axes[2]
    if normal @ up < 0:
        normal = -normal
    error = float(np.sqrt(np.mean(((selected - origin) @ normal) ** 2)) / height)
    return (origin, normal, selected, error) if error < 0.004 else None


def constrain_support(mesh, model, images, masks):
    center = mesh.vertices.mean(axis=0)
    _, singular, axes = np.linalg.svd(mesh.vertices - center, full_matrices=False)
    ups = []
    for image in model.images.values():
        with Image.open(images / image.name) as photo:
            orientation = photo.getexif().get(274, 1)
        axis = {1: [0, -1, 0], 3: [0, 1, 0], 6: [-1, 0, 0], 8: [1, 0, 0]}.get(
            orientation, [0, -1, 0]
        )
        ups.append(image.cam_from_world().rotation.matrix().T @ axis)
    up = np.mean(ups, axis=0)
    up /= np.linalg.norm(up)
    if singular[0] > singular[1] * 2 and abs(axes[0] @ up) > 0.85:
        up = axes[0] * np.sign(axes[0] @ up)
    heights = (mesh.vertices - center) @ up
    height = np.ptp(heights)
    radius = np.percentile(
        np.linalg.norm(mesh.vertices - center - heights[:, None] * up, axis=1), 95
    )
    candidates = []
    for point in model.points3D.values():
        delta = point.xyz - center
        y = delta @ up
        if (
            heights.min() - 0.05 * height < y < heights.min() + 0.3 * height
            and np.linalg.norm(delta - y * up) < radius * 3
        ):
            candidates.append(point)
    if len(candidates) < 40:
        return mesh, {}
    foreground = {}
    for image in model.images.values():
        mask = cv2.imdecode(np.fromfile(masks / (image.name + ".png"), dtype=np.uint8), 0)
        foreground[image.image_id] = (
            cv2.resize(mask, (400, 300), interpolation=cv2.INTER_NEAREST) > 0
        )
    points = []
    for point in candidates[:30000]:
        outside = []
        for track in point.track.elements:
            image = model.images[track.image_id]
            camera = model.cameras[image.camera_id]
            x, y = (
                image.points2D[track.point2D_idx].xy * [400 / camera.width, 300 / camera.height]
            ).astype(int)
            if 0 <= x < 400 and 0 <= y < 300:
                outside.append(not foreground[image.image_id][y, x])
        if len(outside) >= 3 and np.mean(outside) > 0.7:
            points.append(point.xyz)
    fitted = fit_horizontal_plane(np.asarray(points), up, height)
    if fitted is None:
        return mesh, {}
    origin, normal, inliers, error = fitted
    cut_height = ((origin - center) @ normal) / (up @ normal)
    clipped_height = (cut_height - heights.min()) / height
    _, spread, _ = np.linalg.svd(inliers - origin, full_matrices=False)
    if not 0.005 < clipped_height < 0.15 or spread[1] / np.sqrt(len(inliers)) < radius * 0.5:
        return mesh, {}
    distance = (mesh.vertices - origin) @ normal
    if np.mean(distance < 0) > 0.25:
        return mesh, {}
    # A flat contact underside is inferred, never claimed as measured detail.
    mesh = mesh.copy()
    mesh.vertices[distance < 0] -= distance[distance < 0, None] * normal
    mesh.merge_vertices()
    mesh.update_faces(mesh.nondegenerate_faces())
    mesh.remove_unreferenced_vertices()
    return mesh, {
        "supportPlaneInliers": len(inliers),
        "supportPlaneRmsRelative": error,
        "supportPlaneNormal": normal.tolist(),
        "bottomClippedAtObservedSupportPlane": True,
        "bottomDetailIsMeasured": False,
        "watertight": bool(mesh.is_watertight),
    }
