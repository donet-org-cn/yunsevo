"""Interpolate small coplanar gaps; never cap an object's open rim or base."""

import numpy as np
import trimesh


def fill_planar_gaps(mesh, accept_patch=None):
    # Boundary half-edges retain face winding, so new triangles must reverse it.
    edges = mesh.edges.reshape(-1, 3, 2)
    unique, counts = np.unique(np.sort(mesh.edges, axis=1), axis=0, return_counts=True)
    boundary = {tuple(e) for e in unique[counts == 1]}
    outgoing, incoming = {}, {}
    for face, face_edges in enumerate(edges):
        for a, b in face_edges:
            if tuple(sorted((a, b))) in boundary:
                outgoing.setdefault(int(a), []).append((int(b), face))
                incoming.setdefault(int(b), []).append(int(a))
    visited, added, areas = set(), [], []
    span = np.linalg.norm(mesh.extents)
    for start in outgoing:
        if start in visited:
            continue
        loop, neighbors, current = [], [], start
        while (
            current not in visited
            and len(outgoing.get(current, [])) == 1
            and len(incoming.get(current, [])) == 1
        ):
            visited.add(current)
            loop.append(current)
            current, face = outgoing[current][0]
            neighbors.append(face)
        if current != start or not 3 <= len(loop) <= 2500:
            continue
        points = mesh.vertices[loop]
        center = points.mean(axis=0)
        _, _, basis = np.linalg.svd(points - center, full_matrices=False)
        normal = basis[2]
        residual = np.abs((points - center) @ normal)
        if residual.max() > span * 0.008:
            continue
        # An open box rim is planar, but the incident walls are perpendicular
        # to its plane. This gate preserves both the opening and unseen bottom.
        alignment = np.abs(mesh.face_normals[neighbors] @ normal)
        if np.quantile(alignment, 0.1) < 0.75 or np.median(alignment) < 0.9:
            continue
        xy = (points - center) @ basis[:2].T
        cross = xy[:, 0] * np.roll(xy[:, 1], -1) - xy[:, 1] * np.roll(xy[:, 0], -1)
        area = abs(cross.sum()) / 2
        if not span**2 * 1e-9 < area < mesh.area * 0.08:
            continue
        # Ear clipping respects concave boundaries (a convex fan does not).
        order = list(range(len(loop)))
        sign = 1 if cross.sum() > 0 else -1
        triangles = []

        def cross2(a, b):
            return a[0] * b[1] - a[1] * b[0]

        while len(order) > 3:
            found = False
            for position in range(len(order)):
                a, b, c = order[position - 1], order[position], order[(position + 1) % len(order)]
                pa, pb, pc = xy[[a, b, c]]
                if sign * cross2(pb - pa, pc - pb) <= span**2 * 1e-12:
                    continue
                rest = xy[[i for i in order if i not in (a, b, c)]]
                inside = np.ones(len(rest), dtype=bool)
                for p, q in ((pa, pb), (pb, pc), (pc, pa)):
                    delta = rest - p
                    inside &= (
                        sign * ((q[0] - p[0]) * delta[:, 1] - (q[1] - p[1]) * delta[:, 0])
                        >= -(span**2) * 1e-12
                    )
                if inside.any():
                    continue
                triangles.append([loop[c], loop[b], loop[a]])
                order.pop(position)
                found = True
                break
            if not found:
                break
        if len(order) == 3:
            triangles.append([loop[i] for i in reversed(order)])
            if accept_patch is not None and not accept_patch(
                mesh.vertices, np.array(triangles), normal
            ):
                continue
            added.extend(triangles)
            areas.append(float(area))
    if added:
        mesh = trimesh.Trimesh(
            vertices=mesh.vertices, faces=np.vstack([mesh.faces, added]), process=False
        )
    return mesh, {
        "planarInterpolatedGaps": len(areas),
        "planarInterpolatedAreaFraction": sum(areas) / mesh.area,
        "planarInterpolationIsMeasuredDepth": False,
    }


def photo_supported_patches(mesh, model, images):
    """Reject interpolation where photos disagree (e.g. a real hole/window)."""
    from PIL import Image
    from trimesh.ray.ray_pyembree import RayMeshIntersector

    ray = RayMeshIntersector(mesh)
    views = []
    for image in model.images.values():
        camera = model.cameras[image.camera_id]
        with Image.open(images / image.name) as original:
            original.thumbnail((1000, 1000))
            pixels = np.asarray(original.convert("RGB"), dtype=float) / 255
        views.append((image, camera, pixels))
    tolerance = np.linalg.norm(mesh.extents) * 0.002

    def accept(vertices, faces, normal):
        triangles = vertices[faces]
        # Area-weighted deterministic samples avoid oversampling tiny ears.
        areas = np.linalg.norm(
            np.cross(triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0]), axis=1
        )
        weights = np.cumsum(areas) / areas.sum()
        samples = triangles[np.searchsorted(weights, (np.arange(20) + 0.5) / 20)].mean(axis=1)
        observations, directions = [], []
        for image, camera, pixels in views:
            center = image.projection_center()
            delta = samples - center
            distance = np.linalg.norm(delta, axis=1)
            direction = delta / distance[:, None]
            if abs(direction.mean(axis=0) @ normal) < 0.3:
                continue
            mean_direction = direction.mean(axis=0)
            mean_direction /= np.linalg.norm(mean_direction)
            if any(mean_direction @ previous > np.cos(np.deg2rad(3)) for previous in directions):
                continue
            local = image.cam_from_world() * samples
            uv = camera.img_from_cam(local)
            uv *= [pixels.shape[1] / camera.width, pixels.shape[0] / camera.height]
            if (local[:, 2] <= 0).any() or not np.isfinite(uv).all():
                continue
            xy = np.rint(uv).astype(int)
            if (
                (xy < 0).any()
                or (xy[:, 0] >= pixels.shape[1]).any()
                or (xy[:, 1] >= pixels.shape[0]).any()
            ):
                continue
            origins = np.tile(center, (len(samples), 1))
            hits, indices, _ = ray.intersects_location(origins, direction, multiple_hits=False)
            visible = np.ones(len(samples), dtype=bool)
            visible[indices] = (
                np.linalg.norm(hits - center, axis=1) >= distance[indices] - tolerance
            )
            colors = pixels[xy[:, 1], xy[:, 0]].copy()
            colors[~visible] = np.nan
            if visible.mean() >= 0.8:
                observations.append(colors)
                directions.append(mean_direction)
        if len(observations) < 3 or np.min(np.array(directions) @ np.array(directions).T) > np.cos(
            np.deg2rad(12)
        ):
            return False
        colors = np.array(observations)
        reference = np.nanmedian(colors, axis=0)
        disagreement = np.mean(np.abs(colors - reference), axis=2)
        support = np.nansum(disagreement < 0.085, axis=0)
        # At least three views must agree on most of the proposed surface.
        return float(np.mean(support >= 3)) >= 0.8

    return accept
