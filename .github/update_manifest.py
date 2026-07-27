#!/usr/bin/env python3
"""Met a jour manifest.json (catalogue de plugins consomme par Jellyfin) apres une release."""
import argparse
import json
import os
from datetime import datetime, timezone

GUID = "6b3d2c1a-9e4f-4b2a-8c7d-1f0a2b3c4d5e"
TARGET_ABI = "10.11.0.0"
MANIFEST = "manifest.json"


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--version", required=True)
    p.add_argument("--checksum", required=True)
    p.add_argument("--repo", required=True)   # owner/repo
    p.add_argument("--tag", required=True)
    p.add_argument("--zip", required=True)
    args = p.parse_args()

    source_url = f"https://github.com/{args.repo}/releases/download/{args.tag}/{args.zip}"
    # Pas de logo pour l'instant : une URL invalide provoque un 404 a l'installation.
    image_url = ""

    if os.path.exists(MANIFEST):
        with open(MANIFEST, encoding="utf-8") as fh:
            data = json.load(fh)
    else:
        data = []

    if not data:
        data = [{
            "guid": GUID,
            "name": "Chat en Direct",
            "description": "Messagerie temps reel entre utilisateurs Jellyfin : salon public, "
                           "messages prives, amis, blocage et moderation.",
            "overview": "Chat entre les utilisateurs du serveur.",
            "owner": args.repo.split("/")[0],
            "category": "General",
            "imageUrl": image_url,
            "versions": [],
        }]

    plugin = data[0]
    # Rafraichit toujours les champs derives du depot.
    plugin["guid"] = GUID
    plugin["owner"] = args.repo.split("/")[0]
    plugin["imageUrl"] = image_url

    version_entry = {
        "version": args.version,
        "changelog": f"Version {args.version}",
        "targetAbi": TARGET_ABI,
        "sourceUrl": source_url,
        "checksum": args.checksum,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    # Remplace une eventuelle entree de meme version, puis trie (plus recent en tete).
    plugin["versions"] = [v for v in plugin["versions"] if v["version"] != args.version]
    plugin["versions"].insert(0, version_entry)

    with open(MANIFEST, "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2, ensure_ascii=False)
        fh.write("\n")

    print(f"manifest.json mis a jour : {args.version} -> {source_url}")


if __name__ == "__main__":
    main()
