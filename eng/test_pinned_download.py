"""Download/cache failure boundaries using local byte streams, never live network."""
import hashlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

import pinned_download


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "tool.zip"
        self.data = b"pinned test bytes"
        self.digest = hashlib.sha512(self.data).hexdigest()
        self.url = "https://example.test/tool.zip"

    def fetch(self):
        return pinned_download.fetch_verified(self.url, self.path, self.digest)

    def error(self, code):
        return HTTPError(self.url, code, "fixture", {}, None)

    def test_transient_500_retries_and_only_verified_bytes_enter_cache(self):
        with patch("pinned_download.urllib.request.urlopen", side_effect=[self.error(500), io.BytesIO(self.data)]) as request, patch("pinned_download.time.sleep") as delay:
            self.assertEqual(self.path, self.fetch())
            self.assertEqual(2, request.call_count)
            delay.assert_called_once_with(2)
        self.assertEqual(self.data, self.path.read_bytes())
        self.assertFalse(self.path.with_suffix(".download").exists())

    def test_transient_failure_stops_after_three_attempts(self):
        with patch("pinned_download.urllib.request.urlopen", side_effect=self.error(503)) as request, patch("pinned_download.time.sleep") as delay:
            with self.assertRaises(HTTPError):
                self.fetch()
            self.assertEqual(3, request.call_count)
            self.assertEqual([2, 4], [call.args[0] for call in delay.call_args_list])
        self.assertFalse(self.path.exists())
        self.assertFalse(self.path.with_suffix(".download").exists())

    def test_permanent_404_is_not_retried(self):
        with patch("pinned_download.urllib.request.urlopen", side_effect=self.error(404)) as request, patch("pinned_download.time.sleep") as delay:
            with self.assertRaises(HTTPError):
                self.fetch()
            self.assertEqual(1, request.call_count)
            delay.assert_not_called()

    def test_hash_mismatch_is_not_retried_or_cached(self):
        with patch("pinned_download.urllib.request.urlopen", return_value=io.BytesIO(b"wrong bytes")) as request, patch("pinned_download.time.sleep") as delay:
            with self.assertRaisesRegex(ValueError, "SHA512"):
                self.fetch()
            self.assertEqual(1, request.call_count)
            delay.assert_not_called()
        self.assertFalse(self.path.exists())
        self.assertFalse(self.path.with_suffix(".download").exists())

    def test_valid_cache_avoids_network_and_corrupt_cache_fails_closed(self):
        self.path.write_bytes(self.data)
        with patch("pinned_download.urllib.request.urlopen") as request:
            self.assertEqual(self.path, self.fetch())
            self.path.write_bytes(b"corrupt cached bytes")
            with self.assertRaisesRegex(ValueError, "SHA512"):
                self.fetch()
            request.assert_not_called()
        self.assertEqual(b"corrupt cached bytes", self.path.read_bytes())

    def test_partial_timeout_cleans_up_before_retry(self):
        class Interrupted(io.BytesIO):
            def read(self, count=-1):
                if self.tell():
                    raise TimeoutError("interrupted fixture")
                return super().read(3)

        with patch("pinned_download.urllib.request.urlopen", side_effect=[Interrupted(self.data), io.BytesIO(self.data)]) as request, patch("pinned_download.time.sleep"):
            self.fetch()
            self.assertEqual(2, request.call_count)
        self.assertEqual(self.data, self.path.read_bytes())
        self.assertFalse(self.path.with_suffix(".download").exists())


if __name__ == "__main__":
    unittest.main()
