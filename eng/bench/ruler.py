"""cl100k_base acquisition that survives sandboxed hosts.

tiktoken downloads its ranks file from openaipublic.blob.core.windows.net at
first use. Sandboxed hosts (Cowork cloud, proxied CI) often 403 that domain
while allowing registry.npmjs.org — so when the normal path fails, seed
tiktoken's cache from the npm package `gpt-tokenizer`, which ships the genuine
file (verified 2026-08-15: sha256 of package/data/cl100k_base.tiktoken in
3.4.0 equals tiktoken's own expected_hash). The hash is checked before the
file enters the cache, and tiktoken re-checks it on every cached read, so this
fallback cannot silently substitute the ruler — it either yields the identical
encoder or raises.
"""
from __future__ import annotations

import hashlib
import io
import os
import tarfile
import tempfile
import urllib.request
from pathlib import Path

BLOB_URL = "https://openaipublic.blob.core.windows.net/encodings/cl100k_base.tiktoken"
EXPECTED_SHA256 = "223921b76ee99bde995b7ff738513eef100fb51d18c93597a113bcffe865b2a7"
NPM_TARBALL = "https://registry.npmjs.org/gpt-tokenizer/-/gpt-tokenizer-3.4.0.tgz"
IN_TARBALL = "package/data/cl100k_base.tiktoken"


def get_cl100k():
    import tiktoken

    try:
        return tiktoken.get_encoding("cl100k_base")
    except Exception:
        _seed_cache_from_npm()
        return tiktoken.get_encoding("cl100k_base")


def _seed_cache_from_npm() -> None:
    # tiktoken resolves its cache as TIKTOKEN_CACHE_DIR, else DATA_GYM_CACHE_DIR,
    # else <tmp>/data-gym-cache, and looks up files by sha1(url). When no env var
    # is set, pin TIKTOKEN_CACHE_DIR to the default so the retry reads the dir
    # this seeds.
    cache = os.environ.get("TIKTOKEN_CACHE_DIR") or os.environ.get("DATA_GYM_CACHE_DIR")
    if not cache:
        cache = os.path.join(tempfile.gettempdir(), "data-gym-cache")
        os.environ["TIKTOKEN_CACHE_DIR"] = cache
    Path(cache).mkdir(parents=True, exist_ok=True)
    target = Path(cache) / hashlib.sha1(BLOB_URL.encode()).hexdigest()
    if target.is_file():
        return  # cache already populated: the failure wasn't the download

    with urllib.request.urlopen(NPM_TARBALL) as resp:
        tgz = resp.read()
    with tarfile.open(fileobj=io.BytesIO(tgz), mode="r:gz") as tar:
        member = tar.extractfile(IN_TARBALL)
        if member is None:
            raise RuntimeError(f"{IN_TARBALL} missing from {NPM_TARBALL}")
        data = member.read()

    got = hashlib.sha256(data).hexdigest()
    if got != EXPECTED_SHA256:
        raise RuntimeError(
            f"npm-shipped cl100k_base is not the genuine file: sha256 {got}, "
            f"expected {EXPECTED_SHA256} — refusing to seed the ruler")
    target.write_bytes(data)
