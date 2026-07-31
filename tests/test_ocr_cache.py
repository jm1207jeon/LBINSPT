from labelsuite.core.ocr.cache import OcrCache, frame_cache_key, page_cache_key
from labelsuite.core.ocr.textract_client import OcrWord

WORDS = [OcrWord("hello", (1, 2, 3, 4), 99)]


class TestKeys:
    def test_key_stable(self):
        a = page_cache_key("/x/a.pdf", 123.0, 0, 4.0, "skew-v1")
        b = page_cache_key("/x/a.pdf", 123.0, 0, 4.0, "skew-v1")
        assert a == b

    def test_mtime_invalidates(self):
        a = page_cache_key("/x/a.pdf", 123.0, 0, 4.0, "skew-v1")
        b = page_cache_key("/x/a.pdf", 124.0, 0, 4.0, "skew-v1")
        assert a != b

    def test_page_and_zoom_in_key(self):
        base = page_cache_key("/x/a.pdf", 123.0, 0, 4.0, "skew-v1")
        assert page_cache_key("/x/a.pdf", 123.0, 1, 4.0, "skew-v1") != base
        assert page_cache_key("/x/a.pdf", 123.0, 0, 2.0, "skew-v1") != base

    def test_frame_key(self):
        assert frame_cache_key(b"abc", "s") == frame_cache_key(b"abc", "s")
        assert frame_cache_key(b"abc", "s") != frame_cache_key(b"abd", "s")


class TestCache:
    def test_memory_round_trip(self, tmp_path):
        cache = OcrCache(tmp_path / "cache")
        cache.put("k1", WORDS)
        assert cache.get("k1") == WORDS
        assert cache.get("missing") is None

    def test_disk_persistence(self, tmp_path):
        directory = tmp_path / "cache"
        OcrCache(directory).put("k1", WORDS)
        fresh = OcrCache(directory)
        got = fresh.get("k1")
        assert got is not None
        assert got[0].text == "hello"
        assert got[0].bbox == (1, 2, 3, 4)

    def test_lru_cap(self, tmp_path):
        cache = OcrCache(None, max_entries=2)
        cache.put("a", WORDS)
        cache.put("b", WORDS)
        cache.put("c", WORDS)
        assert cache.get("a") is None
        assert cache.get("c") is not None
