"""Download only compile references from exact upstream releases. No third-party binaries are published."""
import argparse
import io
import json
import os
from pathlib import Path
import re
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def manifest_versions(text):
    # This project's manifest uses a single module and explicit Name/Version pairs.
    pairs = re.findall(r"(?m)^\s*- Name: ([A-Za-z0-9_]+)\s*\n\s+Version: ([0-9]+(?:\.[0-9]+){1,3})\s*$", text)
    result = dict(pairs)
    if len(result) != len(pairs) or set(result) != {"CNGoldenLink", "Everest", "ConsistencyTracker"}:
        raise ValueError("Expected exactly CNGoldenLink, Everest and ConsistencyTracker in everest.yaml")
    return result


def request(url, api=False):
    headers = {"User-Agent": "CNGoldenLink-CI"}
    if api and os.environ.get("GH_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60) as response:
        return response.read()


def release_asset(repo, tag, asset):
    metadata = json.loads(request(f"https://api.github.com/repos/{repo}/releases/tags/{tag}", api=True))
    matches = [a for a in metadata["assets"] if a["name"] == asset]
    if len(matches) != 1:
        raise ValueError(f"{repo} {tag}: expected one {asset} asset")
    url = matches[0]["browser_download_url"]
    if not url.startswith(f"https://github.com/{repo}/releases/download/"):
        raise ValueError("Unexpected asset origin")
    print(f"Downloading {repo} {tag} / {asset}", flush=True)
    data = request(url)
    import hashlib
    digest = hashlib.sha256(data).hexdigest()
    if matches[0].get("digest") and matches[0]["digest"] != "sha256:" + digest:
        raise ValueError("Release asset digest mismatch")
    return zipfile.ZipFile(io.BytesIO(data)), {"repository": repo, "tag": tag, "asset": asset, "sha256": digest}


def prepare(tag=None, download=True):
    versions = manifest_versions((ROOT / "everest.yaml").read_text(encoding="utf-8-sig"))
    version = ET.parse(ROOT / "CNGoldenLink.csproj").findtext("PropertyGroup/Version")
    if not re.fullmatch(r"\d+\.\d+\.\d+", version or "") or versions["CNGoldenLink"] != version:
        raise ValueError("Project and manifest versions must match (major.minor.patch)")
    if tag is not None and tag not in (version, "v" + version):
        raise ValueError(f"Tag {tag!r} must be {version} or v{version}")
    if not download:
        return versions
    output = ROOT / "artifacts" / "ci"
    refs = output / "references"
    refs.mkdir(parents=True, exist_ok=True)
    everest, ev_info = release_asset("EverestAPI/Everest", "stable-" + versions["Everest"], "lib-stripped.zip")
    with everest:
        for name in ("Celeste.dll", "MMHOOK_Celeste.dll", "FNA.dll"):
            matches = [n for n in everest.namelist() if n.rsplit("/", 1)[-1] == name]
            if len(matches) != 1:
                raise ValueError(f"Expected one {name} reference")
            (refs / name).write_bytes(everest.read(matches[0]))
    cct, cct_info = release_asset("viddie/ConsistencyTrackerMod", versions["ConsistencyTracker"], "ConsistencyTrackerMod.zip")
    with cct:
        manifest = cct.read("everest.yaml").decode("utf-8-sig")
        if not re.search(r"(?m)^\s*Version:\s*" + re.escape(versions["ConsistencyTracker"]) + r"\s*$", manifest):
            raise ValueError("Downloaded CCT version differs from requested version")
        (refs / "ConsistencyTracker.dll").write_bytes(cct.read("bin/ConsistencyTracker.dll"))
    (output / "dependencies.json").write_text(json.dumps([ev_info, cct_info], indent=2) + "\n", encoding="utf-8")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write(f"version={version}\n")
    return versions


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("--tag")
    args = parser.parse_args()
    prepare(args.tag, not args.check_only)
