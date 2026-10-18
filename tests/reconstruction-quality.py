"""No private inputs or phone access. Regression checks for the quality worker."""

from pathlib import Path
import sys
import tempfile
import unittest
import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools/reconstruction"))
from imaging import (
    prepare, mask_to_raster, require_mask_size, supported_surface_fraction,
    subdivide_texture_surface,
)
from validation import rotation_jumps, orbit_gap, silhouette_coverage
from planar import fill_planar_gaps
from segmentation import consistent_candidates
from calibration import capture_order
from support import fit_horizontal_plane


class QualityTests(unittest.TestCase):
    def test_texture_refinement_preserves_open_box_and_surface_area(self):
        import trimesh

        box = trimesh.creation.box(extents=[2, 1, 3])
        box.update_faces(np.abs(box.face_normals[:, 2]) < 0.1)
        result = subdivide_texture_surface(box)
        self.assertAlmostEqual(result.area, box.area)
        self.assertFalse(result.is_watertight)
        self.assertTrue(result.is_winding_consistent)
        self.assertLess(result.edges_unique_length.max(), np.linalg.norm(box.extents) / 79)
        # No triangle may cover either genuine opening during texture refinement.
        self.assertTrue(np.all(np.abs(result.face_normals[:, 2]) < 1e-8))

    def test_support_plane_requires_a_broad_horizontal_consensus(self):
        rng = np.random.default_rng(7)
        points = np.c_[rng.uniform(-2, 2, (200, 2)), rng.normal(0, 0.002, 200)]
        fitted = fit_horizontal_plane(points, np.array([0.0, 0.0, 1.0]), 4.0)
        self.assertIsNotNone(fitted)
        self.assertGreater(fitted[1][2], 0.99)
        wall = points[:, [2, 0, 1]]
        self.assertIsNone(fit_horizontal_plane(wall, np.array([0.0, 0.0, 1.0]), 4.0))
        self.assertIsNone(fit_horizontal_plane(points[:10], np.array([0.0, 0.0, 1.0]), 4.0))

    def test_sparse_sampling_cannot_overweight_three_mask_outliers(self):
        import trimesh
        from types import SimpleNamespace

        class Camera:
            width = height = 20

            def img_from_cam(self, points):
                return points[:, :2] + 10

        pose = SimpleNamespace(
            rotation=SimpleNamespace(matrix=lambda: np.eye(3)), translation=np.zeros(3)
        )
        images = {
            i: SimpleNamespace(name=f"{i:03}.jpg", camera_id=1, cam_from_world=lambda: pose)
            for i in range(60)
        }
        model = SimpleNamespace(images=images, cameras={1: Camera()})
        mesh = trimesh.Trimesh(
            vertices=[[-5, -5, 1], [5, -5, 1], [5, 5, 1], [-5, 5, 1]],
            faces=[[0, 1, 2], [0, 2, 3]],
            process=False,
        )
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            for i in range(60):
                mask = np.zeros((20, 20), np.uint8)
                mask[5:16, 5:16] = 255
                if i in (0, 3, 7):
                    mask[:] = 255
                Image.fromarray(mask).save(root / f"{i:03}.jpg.png")
            report = silhouette_coverage(model, root, mesh, min_median=0.8, min_p10=0.8)
            self.assertEqual(report["silhouetteViewsChecked"], 60)
            self.assertGreater(report["p10SilhouetteIoU"], 0.8)

    def test_many_nearby_views_do_not_count_as_a_complete_orbit(self):
        angles = np.linspace(0, 2 * np.pi, 48, endpoint=False)
        full = np.c_[np.cos(angles), np.sin(angles), np.full(48, 0.5)]
        self.assertLess(orbit_gap(full, np.zeros(3)), 8)
        angles = np.linspace(0, np.pi / 2, 48)
        partial = np.c_[np.cos(angles), np.sin(angles), np.full(48, 0.5)]
        self.assertGreater(orbit_gap(partial, np.zeros(3)), 260)

    def test_capture_burst_uses_exif_time_not_random_file_names(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            for name, second in [("z.jpg", 1), ("a.jpg", 2), ("m.jpg", 3)]:
                exif = Image.Exif()
                exif[34665] = {36867: f"2026:10:18 18:00:{second:02}", 37521: "1"}
                Image.new("RGB", (20, 20)).save(root / name, exif=exif)
            self.assertEqual(capture_order(root), ["z.jpg", "a.jpg", "m.jpg"])
            Image.new("RGB", (20, 20)).save(root / "unknown.jpg")
            self.assertIsNone(capture_order(root))

    def test_one_video_frame_does_not_switch_from_object_to_label(self):
        whole = np.zeros((32, 32), bool)
        whole[3:29, 8:24] = True
        label = np.zeros_like(whole)
        label[13:20, 8:24] = True
        frames = [[(0.90, whole), (0.80, label)] for _ in range(5)]
        frames[2] = [(0.85, whole), (0.99, label)]
        self.assertEqual(consistent_candidates(frames), [0] * 5)

    def test_planar_gap_requires_photo_approval(self):
        import trimesh

        vertices = [
            [-1, -1, 0],
            [1, -1, 0],
            [1, 1, 0],
            [-1, 1, 0],
            [-0.1, -0.1, 0],
            [0.1, -0.1, 0],
            [0.1, 0.1, 0],
            [-0.1, 0.1, 0],
        ]
        faces = []
        for i in range(4):
            j = (i + 1) % 4
            faces += [[i, j, j + 4], [i, j + 4, i + 4]]
        mesh = trimesh.Trimesh(vertices=vertices, faces=faces, process=False)
        rejected, report = fill_planar_gaps(mesh, lambda *args: False)
        self.assertEqual(len(rejected.faces), len(faces))
        accepted, report = fill_planar_gaps(mesh, lambda *args: True)
        self.assertEqual(report["planarInterpolatedGaps"], 1)
        self.assertAlmostEqual(accepted.area, 4.0)

    def test_box_opening_and_unobserved_bottom_remain_open(self):
        import trimesh

        box = trimesh.creation.box(extents=[1, 1, 20])
        box.update_faces(np.abs(box.face_normals[:, 2]) < 0.1)
        result, report = fill_planar_gaps(box, lambda *args: True)
        self.assertEqual(report["planarInterpolatedGaps"], 0)
        self.assertFalse(result.is_watertight)

    def test_original_pixels_and_rotated_mask_share_one_coordinate_system(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            source = root / "source"
            source.mkdir()
            image = Image.new("RGB", (60, 40), "red")
            image.paste("blue", (0, 0, 30, 40))
            exif = Image.Exif()
            exif[274] = 6
            for i in range(6):
                image.save(source / f"{i}.jpg", exif=exif, quality=100, subsampling=0)
            manifest = prepare(source, root / "images")
            normalized = Image.open(root / "images/view-000.jpg")
            self.assertEqual(normalized.size, (60, 40))
            self.assertEqual(
                (root / "images/view-000.jpg").read_bytes(), (source / "0.jpg").read_bytes()
            )
            upright_mask = np.zeros((60, 40), np.uint8)
            upright_mask[:30] = 255
            raster_mask = mask_to_raster(upright_mask, 6)
            self.assertEqual(raster_mask.shape, (40, 60))
            self.assertTrue(np.all(raster_mask[:, :30] == 255))
            self.assertTrue(np.all(raster_mask[:, 30:] == 0))
            self.assertEqual(manifest[0]["orientation"], 6)
            self.assertEqual(len(manifest[0]["sha256"]), 64)
            normalized.close()

    def test_mismatched_mask_fails_before_projection(self):
        class Camera:
            width, height = 40, 60

        require_mask_size(np.zeros((60, 40), np.uint8), Camera())
        with self.assertRaises(ValueError):
            require_mask_size(np.zeros((40, 60), np.uint8), Camera())

    def test_coverage_is_area_weighted(self):
        class Mesh:
            area_faces = np.r_[100.0, np.full(100, 0.01)]
            area = 101.0

        labels = np.r_[-1, np.zeros(100)]
        self.assertLess(supported_surface_fraction(Mesh(), labels), 0.01)

    def test_handheld_roll_is_distinguished_from_opposite_face_alias(self):
        from scipy.spatial.transform import Rotation

        handheld = Rotation.from_euler(
            "zyx", [[0, 0, 0], [9, 12, 2], [-4, 24, -3]], degrees=True
        ).as_matrix()
        self.assertLess(rotation_jumps(handheld).max(), 25)
        aliased = Rotation.from_euler("y", [[0], [10], [180], [30]], degrees=True).as_matrix()
        self.assertGreater(rotation_jumps(aliased).max(), 160)

    def test_mixed_sizes_are_not_given_one_false_calibration(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            source = root / "source"
            source.mkdir()
            for i in range(6):
                Image.new("RGB", (40 if i else 60, 40)).save(source / f"{i}.jpg")
            with self.assertRaises(ValueError):
                prepare(source, root / "images")


if __name__ == "__main__":
    unittest.main()
