"""Inspect an extracted application payload; print findings, never matched data."""
from pathlib import Path
import re
import sys

root = Path(sys.argv[1]).resolve(strict=True)
private_names = {"identity.json", "approved-peers.json", "desktop-settings.json",
                 "setup.json", "topology.secure.json", "topology.local.json", "monitor-layouts.json"}
private_extensions = {".pfx", ".p12", ".pem", ".key", ".pdb", ".log"}
patterns = {
    "personal-home-path": re.compile(r"(?:[A-Za-z]:[\\/]|/)[Uu]sers[\\/][^\\/\s\"'<>\x00]+"),
    "private-key-material": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----\s+[A-Za-z0-9+/=]{40,}"),
}
findings = set()
count = 0
for file in root.rglob("*"):
    relative = file.relative_to(root).as_posix()
    if file.is_symlink():
        findings.add((relative, "unexpected payload symlink"))
        continue
    if not file.is_file():
        continue
    count += 1
    if file.name in private_names or file.suffix.lower() in private_extensions or file.name.endswith(".batcave-peer.json"):
        findings.add((relative, "private artifact"))
    data = file.read_bytes()
    for text in (data.decode("latin1"), data.decode("utf-16-le", errors="ignore")):
        for category, pattern in patterns.items():
            if pattern.search(text):
                findings.add((relative, category))
for file, category in sorted(findings):
    print(f"FAIL {file}: {category}")
if not count or findings:
    sys.exit(1)
print(f"PASS: {count} packaged files checked for private artifacts and personal home paths.")
