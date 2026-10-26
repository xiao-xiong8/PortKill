#!/usr/bin/env python3
"""Check the updated SSE first frame through the notifier's Docker bridge address."""
import argparse
import http.client
import json
import sys
import time
from urllib.parse import urlsplit


def wait_for_first_frame(address, port, timeout):
    deadline = time.monotonic() + timeout
    last_error = "notification service not ready"
    while time.monotonic() < deadline:
        connection = http.client.HTTPConnection(
            address, port, timeout=min(5, max(0.1, deadline - time.monotonic()))
        )
        response = None
        try:
            started = time.monotonic()
            connection.request("GET", "/stable/events", headers={"Accept": "text/event-stream", "Accept-Encoding": "identity"})
            response = connection.getresponse()
            if response.status != 200 or response.getheader("Content-Type", "").split(";", 1)[0] != "text/event-stream":
                raise ValueError(f"Unexpected SSE response: HTTP {response.status}")
            if response.readline(4096).rstrip(b"\r\n") != b": connected":
                raise ValueError("The updated SSE initial frame was not received")
            if response.readline(4096) not in (b"\n", b"\r\n"):
                raise ValueError("Incomplete SSE initial frame")
            elapsed_ms = (time.monotonic() - started) * 1000
            print(f"SSE first frame verified: {elapsed_ms:.0f} ms; no published release is required.")
            return
        except (OSError, http.client.HTTPException, ValueError) as error:
            last_error = str(error)
        finally:
            if response is not None:
                response.close()
            connection.close()
        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(0.5, remaining))
    raise RuntimeError(f"SSE verification failed: {last_error}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", type=float, default=30)
    args = parser.parse_args()
    containers = json.load(sys.stdin)
    if len(containers) != 1:
        raise ValueError("Expected one notifier container")
    container = containers[0]
    if not container.get("State", {}).get("Running"):
        raise ValueError("Notifier container is not running")
    addresses = [network.get("IPAddress") for network in container["NetworkSettings"]["Networks"].values() if network.get("IPAddress")]
    if not addresses:
        raise ValueError("Notifier has no Docker bridge IPv4 address")
    listen_urls = [urlsplit(arg) for arg in container["Config"].get("Cmd", []) if isinstance(arg, str) and arg.startswith("http://")]
    port = listen_urls[-1].port or 80 if listen_urls else 8091
    wait_for_first_frame(addresses[0], port, args.timeout)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, RuntimeError, OSError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
