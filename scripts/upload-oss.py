"""Publish a versioned package; credentials are supplied only through the environment."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import sys


def upload(package, sdk):
    package = Path(package)
    if not re.fullmatch(r"CNGoldenLink-\d+\.\d+\.\d+\.zip", package.name):
        raise ValueError("Expected a versioned CNGoldenLink ZIP filename")
    if not package.is_file():
        raise ValueError("Package does not exist")
    for name in ("OSS_ACCESS_KEY_ID", "OSS_ACCESS_KEY_SECRET"):
        if not os.environ.get(name):
            raise ValueError(f"Missing required secret: {name}")
    auth = sdk.Auth(os.environ["OSS_ACCESS_KEY_ID"], os.environ["OSS_ACCESS_KEY_SECRET"])
    bucket = sdk.Bucket(auth, "https://oss-cn-shanghai.aliyuncs.com",
                        "aliyun-static-diving-fish", connect_timeout=60)
    key = "cngist/" + package.name
    with package.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    try:
        bucket.put_object_from_file(key, str(package), headers={
            "Content-Type": "application/zip",
            "Cache-Control": "public, max-age=31536000, immutable",
            "x-oss-forbid-overwrite": "true",
            "x-oss-meta-sha256": digest,
        })
    except sdk.exceptions.ServerError as error:
        if error.status != 409 or error.code != "FileAlreadyExists":
            raise
        # Allow retry after OSS succeeded but GitHub Release creation failed.
        existing = bucket.head_object(key)
        if (existing.headers.get("x-oss-meta-sha256") != digest
                or existing.content_length != package.stat().st_size):
            raise ValueError("A different package already exists; publish a new version") from None
    url = "https://aliyun-static.diving-fish.com/" + key
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write(f"url={url}\n")
    print(f"CDN download: {url}")
    return url


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package")
    args = parser.parse_args()
    try:
        import oss2
        upload(args.package, oss2)
    except ValueError as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
    except Exception:
        # SDK exception bodies can contain request details. Do not log credentials.
        print("OSS upload failed; check credentials, bucket permissions and connectivity.", file=sys.stderr)
        sys.exit(1)
