"""Exercise destructive bundle destinations with a fake compiler in isolation."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile

source = Path(__file__).resolve().parents[1] / "Operating_System_Specific/MacOS/Scripts/build_app_bundle.sh"
bash = os.environ.get("BATCAVE_TEST_BASH", "bash")

def shell_path(path):
    value = path.as_posix()
    if os.name == "nt":
        return "/" + value[0].lower() + value[2:]
    return value
with tempfile.TemporaryDirectory(prefix="batcave-bundle-test-") as temporary:
    sandbox = Path(temporary).resolve()
    assert sandbox.parent == Path(tempfile.gettempdir()).resolve()
    tools = sandbox / "tools"
    tools.mkdir()
    for name, text in {
        "swift": "#!/usr/bin/env bash\nmkdir -p .build/release\nprintf 'test binary' > .build/release/BatCaveMacOSAgent\n",
        "codesign": "#!/usr/bin/env bash\nexit 0\n",
    }.items():
        path = tools / name
        path.write_text(text, encoding="utf-8")
        path.chmod(0o755)
    outside = sandbox / "outside.app"
    outside.mkdir()
    marker = outside / "preserve"
    marker.write_text("keep", encoding="utf-8")

    def fixture(name):
        root = sandbox / name
        (root / "Scripts").mkdir(parents=True)
        script = root / "Scripts/build_app_bundle.sh"
        shutil.copyfile(source, script)
        return root, script

    def run(script, destination, *, stable=False):
        env = {key: value for key, value in os.environ.items() if not key.startswith("BATCAVE_")}
        env.update(PATH=str(tools) + os.pathsep + env.get("PATH", ""),
                   BATCAVE_APP_DIR=shell_path(destination), BATCAVE_UPDATE_STABLE_APP="1" if stable else "0")
        result = subprocess.run([bash, shell_path(script)], env=env, capture_output=True, text=True)
        assert marker.read_text(encoding="utf-8") == "keep", "outside destination changed"
        return result

    root, script = fixture("ordinary")
    for destination in (outside, root / "Build", root / "Build/../outside.app",
                        root / "BuildEvil/destination.app", root / "Build/file"):
        assert run(script, destination).returncode == 2, "unsafe output path accepted"
    for preserved in (root / "Build/BatCave.app", root / "Build/Development/BatCave.app"):
        preserved.mkdir(parents=True, exist_ok=True)
        existing = preserved / "preserve"
        existing.write_text("working app", encoding="utf-8")
        assert run(script, preserved, stable=True).returncode == 2, "working app output accepted"
        assert existing.read_text(encoding="utf-8") == "working app"
        alternate_case = preserved.with_name("batcave.app")
        if (alternate_case / "preserve").exists():
            assert run(script, alternate_case, stable=True).returncode == 2, "case alias overwrote working app"
    for destination, opt_in in ((root / "Build/Preview/BatCave Preview.app", False),):
        destination.mkdir(parents=True, exist_ok=True)
        stale = destination / "old"
        stale.write_text("replace", encoding="utf-8")
        assert run(script, destination, stable=opt_in).returncode == 0, "valid bundle build rejected"
        assert not stale.exists() and (destination / "Contents/MacOS/BatCaveMacOSAgent").is_file()
    for kind in ("build", "parent", "app"):
        root, script = fixture("symlink-" + kind)
        link = root / "Build" if kind == "build" else root / "Build/redirect.app"
        link.parent.mkdir(parents=True, exist_ok=True)
        try:
            link.symlink_to(outside, target_is_directory=True)
        except OSError:
            print("SKIP: symlink creation unavailable on this host")
            continue
        destination = link / "nested.app" if kind != "app" else link
        assert run(script, destination).returncode == 2, "symlink escaped build destination"
print("PASS: preview bundle destinations stay inside Build; working apps and outside files remain intact.")
