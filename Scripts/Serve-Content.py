"""Serve a published tree with revalidated mutable paths and immutable bundle URLs.

Use the same headers in the production reverse proxy/CDN. Directory listings are
disabled. This helper is also the local deployment smoke server.
"""
import argparse
import hashlib
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit


class ContentHandler(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def list_directory(self, path):
        self.send_error(403)
        return None

    def send_head(self):
        self._etag = None
        path = Path(self.translate_path(self.path))
        if not path.is_file():
            return super().send_head()
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                digest.update(chunk)
        etag = '"' + digest.hexdigest() + '"'
        self._etag = etag
        if self.headers.get('If-None-Match') == etag:
            self.send_response(304)
            self.end_headers()
            return None
        # ETag takes precedence over If-Modified-Since. Avoid same-second stale JSON.
        if 'If-Modified-Since' in self.headers:
            del self.headers['If-Modified-Since']
        return super().send_head()

    def end_headers(self):
        immutable = urlsplit(self.path).path.endswith('.bundle')
        self.send_header('Cache-Control', 'public, max-age=31536000, immutable' if immutable else 'no-cache, must-revalidate')
        if getattr(self, '_etag', None):
            self.send_header('ETag', self._etag)
        super().end_headers()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    parser.add_argument('--port', type=int, default=8081)
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    import functools
    ThreadingHTTPServer(('127.0.0.1', args.port), functools.partial(ContentHandler, directory=str(root))).serve_forever()


if __name__ == '__main__':
    main()
