"""Regression for current-path HTTP cache revalidation (including same-second writes)."""
import functools
import importlib.util
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

spec = importlib.util.spec_from_file_location('serve_content', Path(__file__).with_name('Serve-Content.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class CacheHeaders(unittest.TestCase):
    def test_current_revalidation_and_immutable_bundles(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'npc').mkdir()
            index = root / 'npc/index.json'
            index.write_text('old')
            (root / 'hash.bundle').write_bytes(b'bundle')
            server = ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(module.ContentHandler, directory=directory))
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            base = f'http://127.0.0.1:{server.server_port}'
            try:
                with urllib.request.urlopen(base + '/npc/index.json') as response:
                    self.assertEqual(response.headers['Cache-Control'], 'no-cache, must-revalidate')
                    etag, modified = response.headers['ETag'], response.headers['Last-Modified']
                request = urllib.request.Request(base + '/npc/index.json', headers={'If-None-Match': etag})
                with self.assertRaises(urllib.error.HTTPError) as result:
                    urllib.request.urlopen(request)
                self.assertEqual(result.exception.code, 304)
                index.write_text('new')
                request = urllib.request.Request(base + '/npc/index.json', headers={'If-None-Match': etag, 'If-Modified-Since': modified})
                with urllib.request.urlopen(request) as response:
                    self.assertEqual(response.read(), b'new')
                    self.assertNotEqual(response.headers['ETag'], etag)
                with urllib.request.urlopen(base + '/hash.bundle') as response:
                    self.assertIn('immutable', response.headers['Cache-Control'])
            finally:
                server.shutdown(); server.server_close(); thread.join()


if __name__ == '__main__':
    unittest.main()
