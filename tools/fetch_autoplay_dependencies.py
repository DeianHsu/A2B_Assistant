"""Fetch pinned official runtime distributions into the local build cache."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import urllib.request


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache-directory", type=Path, required=True)
    args = parser.parse_args()
    cache = args.cache_directory
    cache.mkdir(parents=True, exist_ok=True)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def fetch(name: str, url: str) -> None:
        destination = cache / name
        if not destination.exists():
            with opener.open(url, timeout=60) as response:
                destination.write_bytes(response.read())
        print(f"{name}: {destination.stat().st_size} bytes", flush=True)

    fetch("BepInEx_win_x64_5.4.23.5.zip", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip")
    fetch("python-3.14.0-embed-amd64.zip", "https://www.python.org/ftp/python/3.14.0/python-3.14.0-embed-amd64.zip")
    with opener.open("https://pypi.org/pypi/PyYAML/6.0.3/json", timeout=60) as response:
        package = json.load(response)
    wheel = next(item for item in package["urls"] if item["filename"].endswith("cp314-cp314-win_amd64.whl"))
    fetch("PyYAML-6.0.3-win64.whl", wheel["url"])
    fetch("BepInEx-LICENSE.txt", "https://raw.githubusercontent.com/BepInEx/BepInEx/v5-lts/LICENSE")


if __name__ == "__main__":
    main()
