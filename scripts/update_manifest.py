#!/usr/bin/env python3
"""Add (or replace) one plugin version in the Jellyfin repository manifest.

Used by .github/workflows/release.yml, but you can run it by hand too:

  python3 scripts/update_manifest.py --guid ... --name "Medic Picks" --version 0.0.1.0 \
      --abi 12.0.0.0 --url https://.../medic-picks_0.0.1.0.zip --checksum <md5> \
      --changelog "First beta" --owner p1zzm1ssile --description "..." --overview "..."
"""
import argparse
import datetime
import json
import pathlib


def version_key(v: str):
    parts = [int(p) for p in v.split(".") if p.isdigit()]
    return tuple(parts + [0] * (4 - len(parts)))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", default="manifest.json")
    ap.add_argument("--guid", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--abi", required=True)
    ap.add_argument("--url", required=True)
    ap.add_argument("--checksum", required=True)
    ap.add_argument("--changelog", default="")
    ap.add_argument("--changelog-file")
    ap.add_argument("--owner", default="")
    ap.add_argument("--description", default="")
    ap.add_argument("--overview", default="")
    ap.add_argument("--category", default="General")
    ap.add_argument("--image-url", default="")
    args = ap.parse_args()

    path = pathlib.Path(args.manifest)
    manifest = json.loads(path.read_text(encoding="utf-8")) if path.exists() and path.read_text().strip() else []

    changelog = args.changelog
    if args.changelog_file:
        changelog = pathlib.Path(args.changelog_file).read_text(encoding="utf-8").strip() or changelog

    entry = next((p for p in manifest if p.get("guid", "").lower() == args.guid.lower()), None)
    if entry is None:
        entry = {
            "guid": args.guid,
            "name": args.name,
            "description": args.description,
            "overview": args.overview,
            "owner": args.owner,
            "category": args.category,
            "imageUrl": args.image_url,
            "thumbImage": args.image_url,
            "versions": [],
        }
        manifest.append(entry)
    else:
        # Keep the catalogue text current without touching anything else.
        for key in ("name", "description", "overview", "owner", "category"):
            value = getattr(args, key)
            if value:
                entry[key] = value
        if args.image_url:
            entry["imageUrl"] = args.image_url
            entry["thumbImage"] = args.image_url

    versions = [v for v in entry.get("versions", []) if v.get("version") != args.version]
    versions.append({
        "version": args.version,
        "changelog": changelog,
        "targetAbi": args.abi,
        "sourceUrl": args.url,
        "checksum": args.checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
    entry["versions"] = sorted(versions, key=lambda v: version_key(v["version"]), reverse=True)

    path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"manifest.json: {args.name} {args.version} (targetAbi {args.abi})")


if __name__ == "__main__":
    main()
