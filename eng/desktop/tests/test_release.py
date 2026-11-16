import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile


SPEC = importlib.util.spec_from_file_location("portable_release", Path(__file__).parents[1] / "release.py")
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)
SHA = "a" * 40
REPOSITORY = "owner/repository"


class ReleaseTests(unittest.TestCase):
    def run_record(self, identifier=1, **changes):
        result = {"id": identifier, "head_sha": SHA, "head_branch": "master", "event": "push",
                  "head_repository": {"full_name": REPOSITORY}, "status": "completed", "conclusion": "success"}
        return result | changes

    def test_a_newer_pending_or_failed_run_blocks_an_older_success(self):
        for state in ({"status": "in_progress", "conclusion": None}, {"conclusion": "failure"}):
            with self.subTest(state=state):
                self.assertIsNone(release.validated_run([self.run_record(), self.run_record(2, **state)], REPOSITORY, SHA))

    def test_only_successful_pushes_to_master_in_the_same_repository_and_commit_qualify(self):
        self.assertEqual(release.validated_run([self.run_record()], REPOSITORY, SHA)["id"], 1)
        for change in ({"head_sha": "b" * 40}, {"head_branch": "feature"}, {"event": "pull_request"},
                       {"head_repository": {"full_name": "fork/repository"}}, {"conclusion": "cancelled"}):
            with self.subTest(change=change):
                self.assertIsNone(release.validated_run([self.run_record(**change)], REPOSITORY, SHA))

    def native_delivery(self, sha=SHA):
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w") as archive:
            archive.writestr("delivery-manifest.json", json.dumps({"source_commit": sha, "runtime": "osx-arm64"}))
        return output.getvalue()

    def extract(self, directory, entries, digest=None, sha=SHA, delivery=1):
        archive = directory / "artifact.zip"
        with zipfile.ZipFile(archive, "w") as outer:
            for name, value in entries:
                outer.writestr(name, value)
        artifact = {"digest": digest or "sha256:" + hashlib.sha256(archive.read_bytes()).hexdigest()}
        return release.extract_delivery(archive, artifact, directory, release.DELIVERIES[delivery][3],
                                        release.DELIVERIES[delivery][2], sha)

    def test_the_original_delivery_bytes_are_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            data = self.native_delivery()
            result = self.extract(Path(temporary), [("gitextensions_7.2.1-alpha_osx-arm64.zip", data)])
            self.assertEqual(result.read_bytes(), data)

    def test_debian_versions_have_stable_download_names_without_changing_package_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            data = b"original Debian package"
            result = self.extract(Path(temporary), [("gitextensions_7.2.1~linux-alpha+test_amd64.deb", data)], delivery=0)
            self.assertEqual(result.name, "gitextensions_7.2.1.linux-alpha.test_amd64.deb")
            self.assertEqual(result.read_bytes(), data)

    def test_drafts_are_found_before_their_tag_exists(self):
        draft = {"id": 42, "tag_name": "alpha", "draft": True}
        missing_tag = subprocess.CompletedProcess([], 1, stdout="", stderr="gh: Not Found (HTTP 404)")
        pages = subprocess.CompletedProcess([], 0, stdout=json.dumps([[{"tag_name": "other"}], [draft]]))
        with patch.object(release.subprocess, "run", return_value=missing_tag), patch.object(release, "gh", return_value=pages):
            self.assertEqual(release.release_by_tag(REPOSITORY, "alpha"), draft)

    def test_an_unknown_tag_and_draft_are_absent(self):
        missing_tag = subprocess.CompletedProcess([], 1, stdout="", stderr="gh: Not Found (HTTP 404)")
        pages = subprocess.CompletedProcess([], 0, stdout=json.dumps([[]]))
        with patch.object(release.subprocess, "run", return_value=missing_tag), patch.object(release, "gh", return_value=pages):
            self.assertIsNone(release.release_by_tag(REPOSITORY, "alpha"))

    def test_release_lookup_does_not_hide_permission_or_server_errors(self):
        for status in (403, 500):
            response = subprocess.CompletedProcess([], 1, stdout="", stderr=f"gh: API error (HTTP {status})")
            with self.subTest(status=status), patch.object(release.subprocess, "run", return_value=response), patch.object(release, "gh") as gh:
                with self.assertRaises(subprocess.CalledProcessError):
                    release.release_by_tag(REPOSITORY, "alpha")
                gh.assert_not_called()

    def test_archive_paths_extra_files_and_corrupt_downloads_are_rejected(self):
        name = "gitextensions_7.2.1-alpha_osx-arm64.zip"
        cases = [([(name, self.native_delivery())], "sha256:wrong"),
                 ([(name, b"one"), ("unexpected", b"two")], None),
                 ([(f"../{name}", b"outside")], None)]
        for entries, digest in cases:
            with self.subTest(entries=[name for name, _ in entries]), tempfile.TemporaryDirectory() as temporary:
                with self.assertRaises(ValueError):
                    self.extract(Path(temporary), entries, digest)

    def test_native_packages_from_another_commit_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary, self.assertRaises(ValueError):
            self.extract(Path(temporary), [("gitextensions_7.2.1-alpha_osx-arm64.zip", self.native_delivery("b" * 40))])

    def test_an_incomplete_upload_stays_a_draft(self):
        with tempfile.TemporaryDirectory() as temporary:
            asset = Path(temporary) / "package.deb"
            asset.write_bytes(b"verified package")
            draft = {"target_commitish": SHA, "draft": True, "assets": []}
            with patch.object(release, "release_by_tag", return_value=draft), patch.object(release, "gh") as gh:
                with self.assertRaises(ValueError):
                    release.publish(REPOSITORY, SHA, [asset], Path(temporary) / "notes.md")
                self.assertFalse(any(call.args[0:2] == ("release", "edit") for call in gh.call_args_list))

    def test_a_published_release_survives_a_rerun_with_different_archive_metadata(self):
        with tempfile.TemporaryDirectory() as temporary:
            asset = Path(temporary) / "package.deb"
            asset.write_bytes(b"newly verified package from a later CI rerun")
            existing = {"target_commitish": SHA, "draft": False, "prerelease": True, "html_url": "https://example.invalid/release",
                        "assets": [{"name": asset.name, "digest": "sha256:" + hashlib.sha256(b"originally verified package").hexdigest()}]}
            with patch.object(release, "release_by_tag", return_value=existing), patch.object(release, "gh") as gh:
                self.assertEqual(release.publish(REPOSITORY, SHA, [asset], Path(temporary) / "notes.md"), existing["html_url"])
                gh.assert_not_called()

    def test_a_release_for_another_commit_is_never_replaced(self):
        with patch.object(release, "release_by_tag", return_value={"target_commitish": "b" * 40}), patch.object(release, "gh") as gh:
            with self.assertRaises(ValueError):
                release.publish(REPOSITORY, SHA, [], Path("notes.md"))
            gh.assert_not_called()

    def test_a_draft_is_published_only_after_all_assets_are_verified(self):
        with tempfile.TemporaryDirectory() as temporary:
            asset = Path(temporary) / "package.deb"
            asset.write_bytes(b"verified package")
            draft = {"target_commitish": SHA, "draft": True, "prerelease": True, "html_url": "https://example.invalid/release",
                     "assets": [{"name": asset.name, "digest": "sha256:" + hashlib.sha256(asset.read_bytes()).hexdigest()}]}
            with patch.object(release, "release_by_tag", side_effect=[None, draft, draft | {"draft": False}]), patch.object(release, "gh") as gh:
                release.publish(REPOSITORY, SHA, [asset], Path(temporary) / "notes.md")
                self.assertEqual([call.args[:2] for call in gh.call_args_list],
                                 [("release", "create"), ("release", "upload"), ("release", "edit")])
                self.assertIn("--draft", gh.call_args_list[0].args)
                self.assertIn("--draft=false", gh.call_args_list[2].args)


if __name__ == "__main__":
    unittest.main()
