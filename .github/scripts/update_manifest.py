#!/usr/bin/env python3
"""Adds a newly released version to manifest.json. Run by the release workflow."""
import json, os
from datetime import datetime, timezone

REPO = os.environ.get("GITHUB_REPOSITORY", "P1zzm1ssile/Jellyfin-Medic")
RAW = f"https://raw.githubusercontent.com/{REPO}/main"
GUID = "8d1f5c2e-6b4a-4f7e-9c3d-2a7b5e1f0c94"
IMAGE = f"{RAW}/src/JellyfinMedic/thumb.png"
TARGET_ABI = "12.1.0.0"

def main():
    with open("manifest.json", encoding="utf-8-sig") as f:
        manifest = json.load(f)

    entry = next((p for p in manifest if str(p.get("guid", "")).lower() == GUID), None)
    if entry is None:
        entry = {
            "guid": GUID,
            "name": "Jellyfin Medic",
            "description": "Health checks, performance tests and usage-aware task scheduling for your Jellyfin server.",
            "overview": "Keeps a busy Jellyfin server healthy: checks, tests and smart scheduling.",
            "owner": "P1zzm1ssile",
            "category": "General",
            "imageUrl": IMAGE,
            "thumbImage": IMAGE,
            "versions": [],
        }
        manifest.append(entry)

    version = os.environ["VERSION"]
    versions = [v for v in entry.get("versions", []) if v.get("version") != version]
    versions.insert(0, {
        "version": version,
        "changelog": os.environ.get("CHANGELOG") or f"Jellyfin Medic {version}",
        "targetAbi": TARGET_ABI,
        "sourceUrl": os.environ["SOURCE_URL"],
        "checksum": os.environ["CHECKSUM"],
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
    entry["versions"] = versions

    with open("manifest.json", "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"manifest.json: Jellyfin Medic {version} added ({len(versions)} version(s))")

if __name__ == "__main__":
    main()
