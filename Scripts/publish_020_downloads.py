#!/usr/bin/env python3
"""Verify frozen 0.2.0 assets; publish only with --publish and repository CI access."""

import argparse
import hashlib
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request


REPOSITORY = "RideFair/BatCave-Public"
TAG = "v0.2.0"
SOURCE = "c5580a992e202a1f7a68c8e94074f32063551046"
EXPECTED = {
    "BatCave-0.2.0-Windows-x64-Setup.exe": "eaf584b43c05b905b59d3d6b9cd965ba7f2d0442b06e7fa570a74b53540d00d7",
    "BatCave-0.2.0-arm64.dmg": "d4307f18e668dddf12bc41eed98344794ccfd49adb031abc72fdfa3c2444c3a7",
    "BatCave-Linux-0.2.0-amd64.deb": "f7160460f8059f98a430c046a1786bcb74ca2494da5b9614d7482b9a76314223",
    "BatCave-0.2.0-Source.zip": "b1b6fbdbf50fd5017b3da801bee43b1ec4351339f850a46bbcc35a0234579f12",
    "BatCave-0.2.0-build-manifests.json": "add273d907d95cdda9cc4168b1ab87b220eae53ebdc9d372b22b9b59b32670e7",
    "BatCave-0.2.0-verification.txt": "bdff84efb9804fce3c3659a4278f569a33c7796c1af0cbb030c46cb09cf47c71",
    "INSTALL.md": "41e5d9876aba32844f94c82ed3fb0c5a5af1df9e32344d461b92d24dffa7605f",
    "SHA256SUMS": "8ba0817aa01a3d18337ca327521ab3743cf8c77e3f293d3980393e6813ca7474",
}
NOTES_HASHES = {
    "61bc622c1ac784fc8a299693c03479211cf7030cb2a393f4ed9f3a021a4dbb3e",
    "810c6556cd561f42e402dc8db692135a323f478ef51ac3e1dfb48d326c3e717f",
}
DRAFT_ACCESS = ("These downloads are staged for the maintainer's installation and device tests.\n"
                "Sign into GitHub with repository write access to see this draft and its assets.")
PUBLIC_ACCESS = ("Downloads are public and do not require a GitHub login.\n"
                 "These installers are available for installation and device testing.")


class AssetRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        parsed = urllib.parse.urlparse(url)
        if parsed.scheme != "https" or parsed.hostname not in (
            "api.github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"
        ):
            raise RuntimeError("Unexpected asset redirect; publication refused.")
        redirected = super().redirect_request(request, response, code, message, headers, url)
        if parsed.hostname != "api.github.com":
            redirected.remove_header("Authorization")
        return redirected


def require(condition, description):
    if not condition:
        raise RuntimeError(description)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", action="store_true")
    options = parser.parse_args()
    token = os.environ.get("GH_TOKEN")
    require(token and os.environ.get("GITHUB_REPOSITORY") == REPOSITORY,
            "Use the scoped repository publishing workflow.")
    opener = urllib.request.build_opener(AssetRedirect())

    def request(path, *, payload=None, binary=False):
        headers = {"Authorization": "Bearer " + token,
                   "Accept": "application/octet-stream" if binary else "application/vnd.github+json",
                   "User-Agent": "BatCave-release-verifier"}
        body = json.dumps(payload).encode() if payload is not None else None
        if body is not None:
            headers["Content-Type"] = "application/json"
        return opener.open(urllib.request.Request(
            "https://api.github.com/repos/" + REPOSITORY + "/" + path,
            headers=headers, data=body, method="PATCH" if body is not None else "GET"), timeout=60)

    def api(path, payload=None):
        with request(path, payload=payload) as response:
            data = response.read(2 * 1024 * 1024 + 1)
            require(len(data) <= 2 * 1024 * 1024, "Unexpected API response size.")
            return json.loads(data)

    def verify_tag(allow_missing):
        try:
            reference = api("git/ref/tags/" + TAG)
        except urllib.error.HTTPError as error:
            if error.code == 404 and allow_missing:
                return
            raise
        target = reference["object"]
        for _ in range(4):
            if target["type"] == "commit":
                require(target["sha"] == SOURCE, "Existing release tag points to different source.")
                return
            require(target["type"] == "tag", "Unexpected release tag object.")
            target = api("git/tags/" + target["sha"])["object"]
        raise RuntimeError("Release tag nesting exceeds its bound.")

    def inventory(values):
        return sorted((asset["id"], asset["name"], asset["size"], asset.get("digest"), asset["state"])
                      for asset in values)

    releases = api("releases?per_page=100")
    matches = [release for release in releases if release["tag_name"] == TAG]
    require(len(matches) == 1, "Exactly one existing 0.2.0 release must be staged.")
    release = matches[0]
    require(release["target_commitish"] == SOURCE,
            "Staged release source differs from the reviewed commit.")
    require(isinstance(release.get("body"), str) and release["body"].strip(),
            "Existing release verification notes must be preserved.")
    require(hashlib.sha256(release["body"].strip().encode()).hexdigest() in NOTES_HASHES,
            "Release notes differ from the reviewed notes.")
    verify_tag(allow_missing=release["draft"])
    assets = api(f"releases/{release['id']}/assets?per_page=100")
    names = [asset["name"] for asset in assets]
    require(len(assets) == 8 and len(set(names)) == 8 and EXPECTED.keys() == set(names),
            "Frozen release inventory differs from the reviewed eight assets.")
    require(all(asset["state"] == "uploaded" and 0 < asset["size"] <= 128 * 1024 * 1024 for asset in assets),
            "Unexpected asset size.")

    for asset in assets:
        name = asset["name"]
        require(re.fullmatch(r"[A-Za-z0-9._-]{1,160}", name), "Unexpected release filename.")
        digest = asset.get("digest", "")
        require(isinstance(digest, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", digest),
                "An asset is missing its stored SHA-256.")
        expected = EXPECTED[name]
        require(digest == "sha256:" + expected, "Frozen asset metadata hash changed: " + name)
        sha = hashlib.sha256()
        size = 0
        with request(f"releases/assets/{asset['id']}", binary=True) as response:
            while chunk := response.read(65536):
                size += len(chunk)
                require(size <= asset["size"], "Asset exceeded its reviewed size: " + name)
                sha.update(chunk)
        require(size == asset["size"] and sha.hexdigest() == expected,
                "Downloaded asset differs from reviewed bytes: " + name)
        print(f"Verified {name}: {expected}", flush=True)

    print("Reviewed release notes (only draft-access wording will change):\n" + release["body"], flush=True)
    if not options.publish:
        print("Verification complete. No release or asset was changed.", flush=True)
        return
    # Check again after downloads; never overwrite concurrent release/asset edits.
    current = api(f"releases/{release['id']}")
    require(current["tag_name"] == TAG and current["target_commitish"] == SOURCE
            and current["body"] == release["body"], "Release changed during verification.")
    require(inventory(api(f"releases/{release['id']}/assets?per_page=100")) == inventory(assets),
            "Asset inventory changed during verification.")
    verify_tag(allow_missing=current["draft"])
    public_body = current["body"].replace(DRAFT_ACCESS, PUBLIC_ACCESS)
    payload = {}
    if current["draft"]:
        payload.update({"draft": False, "prerelease": True, "make_latest": "false"})
    if public_body != current["body"]:
        payload["body"] = public_body
    if payload:
        api(f"releases/{release['id']}", payload)
    published = api("releases/tags/" + TAG)
    require(not published["draft"] and published["id"] == release["id"],
            "The verified release publication did not complete.")
    require(published["body"] == public_body and published["prerelease"],
            "Published verification notes or release classification differ from the reviewed result.")
    verify_tag(allow_missing=False)
    print("Public release: " + published["html_url"], flush=True)


if __name__ == "__main__":
    main()
