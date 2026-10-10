"""Fail on common private source artifacts; never print matched values.

Run from a checkout or an extracted source archive. This is a privacy gate,
not a complete secret detector. Scan Git history separately with Gitleaks.
"""

from pathlib import Path
import ipaddress
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
private_names = {"identity.json", "approved-peers.json", "desktop-settings.json", "audio-settings.json",
                 "topology.secure.json", "topology.local.json", "MacOSAgent.conf", "monitor-layouts.json"}
private_extensions = {".pfx", ".p12", ".pem", ".key", ".cer", ".crt", ".pdb", ".log"}
private_directories = {"Coordination", "Codex_Backups", "Logs", "Releases", "bin", "obj", ".build", "Build"}
patterns = {
    "personal-home-path": re.compile(r"(?:[A-Za-z]:[\\/]|/)[Uu]sers[\\/][^\\/\s\"'<>]+"),
    "private-key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----"),
    "email": re.compile(r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"),
}
private_networks = tuple(ipaddress.ip_network(value) for value in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"))
ip_pattern = re.compile(r"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])")
try:
    git_root = Path(subprocess.check_output(["git", "-C", str(root), "rev-parse", "--show-toplevel"],
                    stderr=subprocess.DEVNULL).decode().strip()).resolve()
    if git_root != root:
        raise FileNotFoundError("Source archive is inside another checkout")
    paths = [root / value for value in subprocess.check_output(
        ["git", "-C", str(root), "ls-files", "-z"], stderr=subprocess.DEVNULL).decode().split("\0") if value]
except (subprocess.CalledProcessError, FileNotFoundError):
    paths = [path for path in root.rglob("*") if path.is_file() and ".git" not in path.relative_to(root).parts]

findings = []
for path in paths:
    if not path.exists():
        continue
    relative = path.relative_to(root)
    if path.is_symlink():
        findings.append((relative, "symlink requires manual review"))
        continue
    if (path.name in private_names or path.suffix.lower() in private_extensions
            or path.name.endswith(".batcave-peer.json") or path.name.startswith(".env")
            or any(part in private_directories for part in relative.parts)):
        findings.append((relative, "local artifact"))
    data = path.read_bytes()
    if b"\0" in data:
        if path.suffix.lower() not in {".ico", ".icns"}:
            findings.append((relative, "unexpected binary"))
        texts = [data.decode("latin1"), data.decode("utf-16-le", errors="ignore")]
    else:
        texts = [data.decode("utf-8", errors="replace")]
    for text in texts:
        for category, pattern in patterns.items():
            for match in pattern.finditer(text):
                if category == "email" and match.group().endswith(("example.com", "example.invalid", "users.noreply.github.com")):
                    continue
                findings.append((relative, category))
                break
        # Regex literals in this gate are policy, not a device configuration.
        if relative.as_posix() == "Scripts/check_public_source.py":
            continue
        for match in ip_pattern.finditer(text):
            try:
                address = ipaddress.ip_address(match.group())
                if any(address in network for network in private_networks):
                    findings.append((relative, "private-network-address"))
                    break
            except ValueError:
                pass

for relative, category in sorted(set(findings), key=lambda value: (str(value[0]), value[1])):
    print(f"FAIL {relative.as_posix()}: {category}")
if findings:
    sys.exit(1)
print(f"PASS: {len(paths)} source files checked for common private artifacts and personal data.")
