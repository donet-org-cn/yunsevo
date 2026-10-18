"""Quality gates are failure detectors, never proof of geometric accuracy."""

import math
import cv2
import numpy as np
from imaging import require_mask_size


def rotation_jumps(rotations):
    matrices = np.asarray(rotations)
    relative = matrices[1:] @ np.transpose(matrices[:-1], (0, 2, 1))
    return np.degrees(np.arccos(np.clip((np.trace(relative, axis1=1, axis2=2) - 1) / 2, -1, 1)))


def orbit_gap(centers, subject):
    directions = np.asarray(centers) - subject
    directions /= np.linalg.norm(directions, axis=1)[:, None]
    _, _, axes = np.linalg.svd(directions - directions.mean(axis=0), full_matrices=False)
    projected = directions @ axes[:2].T
    angles = np.sort(np.arctan2(projected[:, 1], projected[:, 0]))
    return float(np.degrees(np.diff(np.r_[angles, angles[0] + 2 * np.pi])).max())


def camera_checks(model, masks, sequential=False):
    order = {name: i for i, name in enumerate(sequential)} if isinstance(sequential, list) else None
    images = sorted(model.images.values(), key=lambda i: order[i.name] if order else i.name)
    rotations = [i.cam_from_world().rotation.matrix() for i in images]
    jumps = rotation_jumps(rotations)
    # Capture order matters. Large repeated jumps commonly indicate identical
    # printing on opposite sides being joined into one false surface.
    if sequential and (np.mean(jumps > 60) > 0.05 or np.max(jumps) > 150):
        raise ValueError("相邻视角发生不合理跳变，可能将重复图案认成同一面；未发布错位模型。")
    A, b = np.zeros((3, 3)), np.zeros(3)
    views = []
    for image in images:
        cam = model.cameras[image.camera_id]
        mask = cv2.imdecode(np.fromfile(masks / (image.name + ".png"), dtype=np.uint8), 0)
        require_mask_size(mask, cam)
        scale = min(1, 600 / max(cam.width, cam.height))
        mask = cv2.resize(
            mask,
            (round(cam.width * scale), round(cam.height * scale)),
            interpolation=cv2.INTER_NEAREST,
        )
        ys, xs = np.nonzero(mask)
        if len(xs) < 100:
            raise ValueError("主体轮廓为空。")
        pose = image.cam_from_world()
        R, t = pose.rotation.matrix(), pose.translation
        center = -R.T @ t
        xs = xs / scale
        ys = ys / scale
        ray = R.T @ np.r_[cam.cam_from_img([xs.mean(), ys.mean()]), 1.0]
        ray /= np.linalg.norm(ray)
        P = np.eye(3) - np.outer(ray, ray)
        A += P
        b += P @ center
        views.append((cam, R, t, center, (xs.min(), ys.min(), xs.max(), ys.max())))
    if np.linalg.cond(A) > 1e6:
        raise ValueError("缺少足够视差，无法确认主体空间位置。")
    center = np.linalg.solve(A, b)
    outside = 0
    for cam, R, t, _, (x0, y0, x1, y1) in views:
        q = R @ center + t
        uv = cam.img_from_cam(q)
        margin = max(x1 - x0, y1 - y0) * 0.15
        if (
            q[2] <= 0
            or uv is None
            or not (x0 - margin <= uv[0] <= x1 + margin and y0 - margin <= uv[1] <= y1 + margin)
        ):
            outside += 1
    if outside > max(1, math.floor(len(views) * 0.05)):
        raise ValueError("相机定位与主体位置不一致，未发布模型。")
    gap = orbit_gap([v[3] for v in views], center)
    if gap > 120:
        raise ValueError("主体存在超过 120° 的未覆盖方向，无法生成完整环绕模型；原片已保留。")
    return {
        "maxAdjacentRotationDegrees": float(jumps.max()),
        "subjectInconsistentViews": outside,
        "maxUnobservedOrbitDegrees": gap,
    }


def silhouette_coverage(model, masks, mesh, excluded=(), min_median=0.8, min_p10=0.6):
    rows = []
    images = sorted(
        (i for i in model.images.values() if i.name not in excluded), key=lambda i: i.name
    )
    # Sampling 16 regularly spaced frames overrepresented a few segmentation
    # outliers in a 60-view capture. Evaluate every calibrated view for the gate.
    for image in images:
        camera = model.cameras[image.camera_id]
        target = cv2.imdecode(np.fromfile(masks / (image.name + ".png"), dtype=np.uint8), 0)
        require_mask_size(target, camera)
        scale = 320 / max(camera.width, camera.height)
        size = (round(camera.width * scale), round(camera.height * scale))
        target = cv2.resize(target, size, interpolation=cv2.INTER_NEAREST) > 0
        pose = image.cam_from_world()
        q = mesh.vertices @ pose.rotation.matrix().T + pose.translation
        uv = camera.img_from_cam(q) * scale
        finite = np.isfinite(uv).all(axis=1) & (q[:, 2] > 0)
        uv = np.clip(np.nan_to_num(uv), -10000, 10000).astype(np.int32)
        rendered = np.zeros(target.shape, np.uint8)
        for face in mesh.faces[np.all(finite[mesh.faces], axis=1)]:
            cv2.fillConvexPoly(rendered, uv[face], 1)
        rendered = rendered > 0
        intersection = np.count_nonzero(rendered & target)
        rows.append(intersection / max(1, np.count_nonzero(rendered | target)))
    result = {
        "medianSilhouetteIoU": float(np.median(rows)),
        "p10SilhouetteIoU": float(np.percentile(rows, 10)),
        "silhouetteViewsChecked": len(rows),
        "silhouetteAgreementIsNotIndependentGeometryAccuracy": True,
    }
    if result["medianSilhouetteIoU"] < min_median or result["p10SilhouetteIoU"] < min_p10:
        raise ValueError("生成表面与原片轮廓不一致或缺面过多，已保留原片，未发布残缺模型。")
    return result
