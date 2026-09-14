#!/usr/bin/env python3
"""
Fixed-rate load against the public delivery API, reporting p50, p95 and errors per endpoint.

    python3 scripts/delivery-load.py --base-url https://api.example.com --type post --slug hello-world

The API allows 100 requests a minute per client address by default (RateLimiting:Global, see
"Rate limits" in docs/deploy-in-production.md). From one address, a rate above about 1.6
requests a second measures that limiter, answered as 429 or queued, rather than delivery. The
default rate stays under it; a higher rate is useful only to see where the limiter starts.

Three requests take turns: the list (GET /api/public/{type}), one entry by slug
(GET /api/public/{type}/{slug}) and the site settings (GET /api/public/site). Each target is checked
once before the run and the script stops if any of them is not a 200, because a load run against a
404 measures the not-found path, not delivery.

The rate is held open loop: request N is due at N / rate seconds, whether or not earlier ones have
answered. Latency is measured from that due time, so a server that falls behind shows it as latency
instead of quietly lowering the rate it is sent (coordinated omission).

Python standard library only, so it runs on any host that has python3, the VM included. Exits 0 when
every request answered 2xx, 1 when any did not, 2 when a target failed its check or the arguments
are wrong.
"""

import argparse
import http.client
import sys
import threading
import time
import urllib.parse
from concurrent.futures import ThreadPoolExecutor


def parse_args():
    p = argparse.ArgumentParser(description="Fixed-rate load against the public delivery API.")
    p.add_argument("--base-url", required=True, help="API origin, for example http://localhost:5005")
    p.add_argument("--type", required=True, help="a publicly deliverable content type with a slug field")
    p.add_argument("--slug", required=True, help="the slug of one published entry of --type")
    p.add_argument("--site-type", default="site", help="the site settings type (default: site)")
    p.add_argument("--rate", type=float, default=1.5,
                   help="requests per second across all targets (default: 1.5, under the per-address limit)")
    p.add_argument("--duration", type=float, default=60, help="seconds to run (default: 60)")
    p.add_argument("--workers", type=int, default=16, help="concurrent connections (default: 16)")
    p.add_argument("--timeout", type=float, default=10, help="per-request timeout in seconds (default: 10)")
    p.add_argument("--tenant", help="send this X-Tenant header, for a deployment routed by header")
    a = p.parse_args()
    if a.rate <= 0 or a.duration <= 0 or a.workers <= 0:
        p.error("--rate, --duration and --workers must be positive")
    return a


class Client:
    """One keep-alive connection per worker thread, reopened after any failure."""

    def __init__(self, base_url, timeout, headers):
        u = urllib.parse.urlsplit(base_url)
        if u.scheme not in ("http", "https") or not u.hostname:
            raise ValueError(f"--base-url must be an http or https origin, got {base_url!r}")
        self.scheme, self.host, self.port = u.scheme, u.hostname, u.port
        self.prefix = u.path.rstrip("/")
        self.timeout, self.headers = timeout, headers
        self.local = threading.local()

    def _conn(self):
        c = getattr(self.local, "conn", None)
        if c is None:
            cls = http.client.HTTPSConnection if self.scheme == "https" else http.client.HTTPConnection
            c = cls(self.host, self.port, timeout=self.timeout)
            self.local.conn = c
        return c

    def get(self, path):
        c = self._conn()
        try:
            c.request("GET", self.prefix + path, headers=self.headers)
            r = c.getresponse()
            r.read()
            return r.status, None
        except Exception as e:  # noqa: BLE001, any failure is an error sample
            c.close()
            self.local.conn = None
            return None, type(e).__name__


def percentile(sorted_values, pct):
    """Nearest rank, so the value reported is one that was actually observed."""
    if not sorted_values:
        return float("nan")
    rank = max(1, -(-len(sorted_values) * pct // 100))
    return sorted_values[int(rank) - 1]


def main():
    a = parse_args()
    headers = {"Accept": "application/json", "User-Agent": "barako-delivery-load"}
    if a.tenant:
        headers["X-Tenant"] = a.tenant
    try:
        client = Client(a.base_url, a.timeout, headers)
    except ValueError as e:
        print(f"delivery-load: {e}", file=sys.stderr)
        return 2

    q = urllib.parse.quote
    targets = [
        ("list", f"/api/public/{q(a.type)}"),
        ("slug", f"/api/public/{q(a.type)}/{q(a.slug)}"),
        ("site", f"/api/public/{q(a.site_type)}"),
    ]

    for name, path in targets:
        status, err = client.get(path)
        if status != 200:
            print(f"delivery-load: {name} GET {path} answered {status or err}, expected 200. "
                  "Check the type is publicly deliverable and the entry is published.", file=sys.stderr)
            return 2

    total = int(a.rate * a.duration)
    lock = threading.Lock()
    samples = {name: [] for name, _ in targets}
    errors = {name: {} for name, _ in targets}

    def one(i, due):
        name, path = targets[i % len(targets)]
        status, err = client.get(path)
        elapsed_ms = (time.perf_counter() - due) * 1000
        with lock:
            if status is not None and 200 <= status < 300:
                samples[name].append(elapsed_ms)
            else:
                key = str(status) if status is not None else err
                errors[name][key] = errors[name].get(key, 0) + 1

    print(f"{a.base_url}: {a.rate:g} req/s for {a.duration:g}s ({total} requests), {a.workers} connections")
    start = time.perf_counter()
    with ThreadPoolExecutor(max_workers=a.workers) as pool:
        for i in range(total):
            due = start + i / a.rate
            wait = due - time.perf_counter()
            if wait > 0:
                time.sleep(wait)
            pool.submit(one, i, due)
    wall = time.perf_counter() - start

    print(f"\n{'endpoint':<10}{'ok':>7}{'errors':>8}{'p50 ms':>10}{'p95 ms':>10}{'max ms':>10}")
    all_ok, all_errors = [], 0
    for name, path in targets:
        s = sorted(samples[name])
        n_err = sum(errors[name].values())
        all_ok += s
        all_errors += n_err
        mx = s[-1] if s else float("nan")
        print(f"{name:<10}{len(s):>7}{n_err:>8}{percentile(s, 50):>10.1f}{percentile(s, 95):>10.1f}{mx:>10.1f}")
    all_ok.sort()
    mx = all_ok[-1] if all_ok else float("nan")
    print(f"{'all':<10}{len(all_ok):>7}{all_errors:>8}{percentile(all_ok, 50):>10.1f}"
          f"{percentile(all_ok, 95):>10.1f}{mx:>10.1f}")
    print(f"\nachieved {(len(all_ok) + all_errors) / wall:.1f} req/s over {wall:.1f}s")
    for name, _ in targets:
        for key, count in sorted(errors[name].items()):
            print(f"  {name}: {count} x {key}")
    if any("429" in errors[name] for name, _ in targets):
        print("\n429 is the API's limit of 100 requests a minute per client address. Latency above that "
              "rate includes the limiter's queue, so it is not a delivery number.")
    return 1 if all_errors else 0


if __name__ == "__main__":
    sys.exit(main())
