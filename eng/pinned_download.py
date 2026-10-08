"""Bounded retries for transient download failures; only verified bytes enter the cache."""
import hashlib
from pathlib import Path
import shutil
import time
import urllib.error
import urllib.request


def verify(path, expected):
    with path.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha512").hexdigest()
    if actual != expected:
        raise ValueError(f"SHA512 mismatch: {path}")


def fetch_verified(url, path, expected):
    path = Path(path)
    if path.exists():
        verify(path, expected)
        return path
    temporary = path.with_suffix(".download")
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=60) as source, temporary.open("wb") as target:
                shutil.copyfileobj(source, target)
            verify(temporary, expected)
            temporary.replace(path)
            return path
        except urllib.error.HTTPError as error:
            if error.code not in (408, 429, 500, 502, 503, 504) or attempt == 2:
                raise
        except (urllib.error.URLError, TimeoutError):
            if attempt == 2:
                raise
        finally:
            temporary.unlink(missing_ok=True)
        time.sleep(2 ** (attempt + 1))
