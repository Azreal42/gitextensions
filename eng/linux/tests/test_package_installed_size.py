import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


script = Path(__file__).resolve().parents[1] / "package-installed-size.py"
spec = importlib.util.spec_from_file_location("package_installed_size", script)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class InstalledSizeTests(unittest.TestCase):
    def test_rounds_each_file_separately_and_counts_directories(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "empty-directory").mkdir()
            for name, size in [("empty", 0), ("one-byte", 1), ("one-kib", 1024), ("two-kib", 1025)]:
                (root / name).write_bytes(b"x" * size)
            self.assertEqual(module.installed_size(root), 6)

    def test_sparse_and_dense_identical_payloads_have_the_same_size(self):
        with tempfile.TemporaryDirectory() as temp:
            dense, sparse = Path(temp) / "dense", Path(temp) / "sparse"
            dense.mkdir(); sparse.mkdir()
            (dense / "data").write_bytes(bytes(8 * 1024 * 1024))
            with (sparse / "data").open("wb") as stream:
                stream.seek(8 * 1024 * 1024 - 1); stream.write(b"\0")
            self.assertEqual((dense / "data").read_bytes(), (sparse / "data").read_bytes())
            self.assertEqual(module.installed_size(dense), 8193)
            self.assertEqual(module.installed_size(sparse), 8193)

    def test_removed_entries_and_directory_allocation_do_not_change_size(self):
        with tempfile.TemporaryDirectory() as temp:
            fresh, churned = Path(temp) / "fresh", Path(temp) / "churned"
            fresh.mkdir(); churned.mkdir()
            for root in [fresh, churned]:
                (root / "directory").mkdir(); (root / "directory" / "kept").write_bytes(b"x")
            directory = churned / "directory"
            for index in range(256): (directory / (str(index) + "x" * 128)).touch()
            for path in directory.iterdir():
                if path.name != "kept": path.unlink()
            self.assertEqual(module.installed_size(fresh), 3)
            self.assertEqual(module.installed_size(churned), 3)

    def test_symlinks_are_sized_without_following_their_targets(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "payload"; root.mkdir()
            outside = Path(temp) / "outside"; outside.mkdir()
            (outside / "large").write_bytes(bytes(32 * 1024))
            (root / "link").symlink_to(outside, target_is_directory=True)
            self.assertEqual(module.installed_size(root), 2)

    def test_cli_emits_the_control_field_value(self):
        with tempfile.TemporaryDirectory() as temp:
            (Path(temp) / "data").write_bytes(bytes(1025))
            self.assertEqual(subprocess.check_output([sys.executable, str(script), temp], text=True), "3\n")


if __name__ == "__main__":
    unittest.main()
