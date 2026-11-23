#!/usr/bin/env python3
"""Add (or replace) one version entry in the Jellyfin plugin repository manifest.

Used by the release workflow:
  update_manifest.py manifest.json --version 1.0.1.0 --zip wizarr-invites_1.0.1.0.zip \
      --url https://github.com/OWNER/REPO/releases/download/v1.0.1/wizarr-invites_1.0.1.0.zip \
      --target-abi 10.11.0.0 --changelog "See https://github.com/OWNER/REPO/releases/tag/v1.0.1"
"""
import argparse
import datetime
import hashlib
import json
import re

GUID = "0cd6f4b8-dc11-478f-b7e9-0d10cd1aeee9"


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("manifest")
    p.add_argument("--version", required=True)
    p.add_argument("--zip", required=True)
    p.add_argument("--url", required=True)
    p.add_argument("--target-abi", required=True)
    p.add_argument("--changelog", required=True)
    a = p.parse_args()

    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", a.version):
        raise SystemExit(f"version must look like 1.2.3.0, got {a.version!r}")
    if not a.url.startswith("https://"):
        raise SystemExit("url must be https")

    # Jellyfin checks the downloaded zip against this MD5 before installing it.
    with open(a.zip, "rb") as f:
        checksum = hashlib.md5(f.read()).hexdigest()

    with open(a.manifest, encoding="utf-8") as f:
        manifest = json.load(f)

    package = next((pkg for pkg in manifest if pkg.get("guid") == GUID), None)
    if package is None:
        raise SystemExit(f"no package with guid {GUID} in {a.manifest}")

    entry = {
        "version": a.version,
        "changelog": a.changelog,
        "targetAbi": a.target_abi,
        "sourceUrl": a.url,
        "checksum": checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    versions = [v for v in package.get("versions", []) if v.get("version") != a.version]
    versions.append(entry)
    versions.sort(key=lambda v: tuple(int(x) for x in v["version"].split(".")), reverse=True)
    package["versions"] = versions

    with open(a.manifest, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    print(f"Added {a.version} ({checksum}) to {a.manifest}")


if __name__ == "__main__":
    main()
