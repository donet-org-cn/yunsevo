"""Create a public synthetic multi-view object fixture; requires numpy and opencv-python.
All camera poses are deliberately withheld from the reconstruction engine.
"""
import argparse, math
from pathlib import Path
import cv2
import numpy as np

parser = argparse.ArgumentParser()
parser.add_argument("--output", default=".test-data/reconstruction/synthetic48")
args = parser.parse_args()
root = Path(args.output)
root.mkdir(parents=True, exist_ok=True)
rng = np.random.default_rng(322)
side = 768
intrinsics = np.array([[side * 1.1, 0, side / 2], [0, side * 1.1, side / 2], [0, 0, 1.]])
faces = [np.array(v, float) for v in [
    [[-1,-1,-1],[1,-1,-1],[1,1,-1],[-1,1,-1]],
    [[-1,-1,1],[-1,1,1],[1,1,1],[1,-1,1]],
    [[-1,-1,-1],[-1,-1,1],[1,-1,1],[1,-1,-1]],
    [[1,-1,-1],[1,-1,1],[1,1,1],[1,1,-1]],
    [[-1,1,-1],[1,1,-1],[1,1,1],[-1,1,1]],
    [[-1,-1,-1],[-1,1,-1],[-1,1,1],[-1,-1,1]]]]
textures = []
for n in range(6):
    texture = np.full((512, 512, 3), 180, np.uint8)
    for _ in range(1000):
        x, y = rng.integers(8, 504, 2)
        color = tuple(int(c) for c in rng.integers(10, 240, 3))
        cv2.circle(texture, (int(x), int(y)), int(rng.integers(3, 15)), color, -1)
    cv2.putText(texture, "YUNSEVO " + str(n), (25, 260), cv2.FONT_HERSHEY_SIMPLEX, 1.8, (0, 0, 0), 4)
    textures.append(texture)
for i in range(48):
    theta = 2 * math.pi * (i % 24) / 24
    elevation = math.radians(15 if i < 24 else 38)
    center = np.array([5 * math.cos(elevation) * math.cos(theta), -5 * math.sin(elevation), 5 * math.cos(elevation) * math.sin(theta)])
    z = -center / np.linalg.norm(center)
    x = np.cross(z, np.array([0., -1, 0])); x /= np.linalg.norm(x)
    rotation = np.array([x, np.cross(z, x), z]); translation = -rotation @ center
    image = np.full((side, side, 3), 245, np.uint8)
    order = sorted(range(6), key=lambda n: np.mean((rotation @ faces[n].T + translation[:, None])[2]), reverse=True)
    for n in order:
        q = rotation @ faces[n].T + translation[:, None]
        uv = (intrinsics @ q).T; uv = uv[:, :2] / uv[:, 2, None]
        homography = cv2.getPerspectiveTransform(np.array([[0,0],[511,0],[511,511],[0,511]], np.float32), uv.astype(np.float32))
        projected = cv2.warpPerspective(textures[n], homography, (side, side))
        mask = cv2.warpPerspective(np.full((512, 512), 255, np.uint8), homography, (side, side))
        image[mask > 0] = projected[mask > 0]
    cv2.imwrite(str(root / f"view-{i:03}.jpg"), image, [cv2.IMWRITE_JPEG_QUALITY, 96])
print("Generated 48 original views:", root)
