#!/usr/bin/env python3
"""Manage Majo source addons in a separate Godot game repository (Python 3.10+)."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tempfile
import time
import zipfile
import xml.etree.ElementTree as ET

ADDONS = ("majo_runtime", "YarnSpinner-Godot")
IGNORED = {"bin", "obj", ".godot", ".git", "__pycache__"}
LOCK = "majo.lock.json"
IMPORTS = [f"addons/{name}/{props}" for name, props in
           [("YarnSpinner-Godot", "YarnSpinner-Godot.props"), ("majo_runtime", "MajoRuntime.props")]]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def files(root):
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(root.rglob("*"))
            if p.is_file() and not IGNORED.intersection(p.relative_to(root).parts)
            and p.name != ".DS_Store" and p.suffix not in {".tmp", ".log", ".pyc"}}


def version(data):
    match = re.search(rb'^version\s*=\s*"([^"]+)"', data, re.M)
    if not match:
        raise ValueError("Invalid plugin.cfg")
    return match[1].decode()


def read_source(source):
    source = source.resolve()
    content = {}
    for addon in ADDONS:
        directory = source / "addons" / addon
        if directory.is_symlink() or not (directory / "plugin.cfg").is_file():
            raise ValueError(f"Expected system checkout with addons/{addon}: {source}")
        content.update({f"addons/{addon}/{p}": b for p, b in files(directory).items()})
    provenance = {"kind": "checkout", "path": str(source)}
    result = subprocess.run(["git", "-C", str(source), "rev-parse", "HEAD"], capture_output=True, text=True)
    if result.returncode == 0:
        provenance["commit"] = result.stdout.strip()
        changed = subprocess.run(["git", "-C", str(source), "status", "--porcelain", "--", "addons"], capture_output=True, text=True)
        provenance["dirty"] = bool(changed.stdout)
    return content, provenance


def read_archive(archive, expected_sha):
    raw = archive.read_bytes()
    checksum = digest(raw)
    if expected_sha and checksum.lower() != expected_sha.lower():
        raise ValueError("Archive SHA-256 does not match")
    content = {}
    with zipfile.ZipFile(archive) as z:
        for entry in z.infolist():
            path = PurePosixPath(entry.filename)
            if path.is_absolute() or ".." in path.parts or "\\" in entry.filename:
                raise ValueError("Unsafe archive path")
            if entry.is_dir():
                continue
            if (entry.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError("Archive symlinks are not supported")
            if len(path.parts) < 3 or path.parts[:2] not in [("addons", a) for a in ADDONS]:
                raise ValueError("Use a bundled addon ZIP, not a game/example archive")
            if entry.filename in content:
                raise ValueError("Duplicate archive path")
            content[entry.filename] = z.read(entry)
    return content, {"kind": "archive", "sha256": checksum, "file": archive.name}


def validate(content):
    for p in [f"addons/{a}/plugin.cfg" for a in ADDONS] + IMPORTS:
        if p not in content:
            raise ValueError(f"Missing dependency file: {p}; use the with-yarn package")
    if version(content["addons/YarnSpinner-Godot/plugin.cfg"]) != "0.3.22":
        raise ValueError("This manager supports YarnSpinner-Godot 0.3.22")
    props = content[IMPORTS[0]].decode()
    if 'Include="YarnSpinner" Version="3.2.0"' not in props:
        raise ValueError("Expected Yarn Spinner 3.2.0")


def safe_path(path):
    p = PurePosixPath(path)
    return (not p.is_absolute() and ".." not in p.parts and "\\" not in path
            and len(p.parts) >= 3 and p.parts[:2] in [("addons", a) for a in ADDONS])


def load_lock(project):
    path = project / LOCK
    if not path.exists():
        return None
    record = json.loads(path.read_text())
    if record.get("format") != 1:
        raise ValueError("Unsupported majo.lock.json format")
    if not isinstance(record.get("files"), dict) or any(not safe_path(p) for p in record["files"]):
        raise ValueError("Invalid paths in majo.lock.json")
    return record


def ensure_clean(project, previous, incoming=None):
    if (project / "addons").is_symlink():
        raise ValueError("The game's addons directory must not be a symlink")
    expected = previous["files"] if previous else {}
    for addon in ADDONS:
        directory = project / "addons" / addon
        if directory.is_symlink():
            raise ValueError(f"Refusing to replace linked addon: {directory}")
        if not directory.exists():
            if any(p.startswith(f"addons/{addon}/") for p in expected):
                raise ValueError(f"Managed addon is missing: {directory}")
            continue
        for relative, data in files(directory).items():
            path = f"addons/{addon}/{relative}"
            checksum = expected.get(path)
            if checksum is not None and digest(data) == checksum:
                continue
            # Godot creates import/UID metadata during first import; these aren't authored patches.
            if checksum is None and relative.endswith((".import", ".uid")):
                continue
            if not previous and incoming is not None and incoming.get(path) == data:
                continue
            raise ValueError(f"Local addon file differs: {path}. Move the fix to the system checkout before syncing.")
    for path in expected:
        if not (project / path).is_file():
            raise ValueError(f"Managed addon file was deleted: {path}")


def configured(project, csproj):
    text = csproj.read_text()
    xml = ET.fromstring(text)
    if xml.tag != "Project" or xml.get("Sdk") != "Godot.NET.Sdk/4.7.2":
        raise ValueError("Expected Godot.NET.Sdk/4.7.2; upgrade the host explicitly first")
    if not any((entry.text or "").strip() == "net8.0" for entry in xml.iter("TargetFramework")):
        raise ValueError("Expected a net8.0 Godot C# project; configure its target framework first")
    existing = {entry.get("Project", "").replace("\\", "/") for entry in xml.iter("Import")}
    missing = [p for p in IMPORTS if p not in existing]
    if missing:
        text = text.replace("</Project>", "".join(f'  <Import Project="{p}" />\n' for p in missing) + "</Project>")
    if not re.search(r'<EnableDynamicLoading>\s*true\s*</EnableDynamicLoading>', text):
        text = text.replace("</Project>", "  <PropertyGroup><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup>\n</Project>")
    # Leave host warning policy intact; suppress only known pinned-adapter diagnostics.
    text = text.replace("</Project>", "  <PropertyGroup><NoWarn>$(NoWarn);CS1998;SYSLIB1220;CS0618</NoWarn></PropertyGroup>\n</Project>") if "CS1998" not in text else text
    settings = project / "project.godot"
    config = settings.read_text()
    section = re.search(r'^\[editor_plugins\]\s*\n(.*?)(?=^\[|\Z)', config, re.M | re.S)
    plugins = [f"res://addons/{a}/plugin.cfg" for a in reversed(ADDONS)]
    if section:
        body = section[1]
        enabled = re.search(r'^enabled\s*=\s*PackedStringArray\((.*?)\)', body, re.M | re.S)
        if not enabled and re.search(r'^enabled\s*=', body, re.M):
            raise ValueError("Unsupported editor_plugins/enabled syntax; configure manually")
        values = re.findall(r'"([^"\n]+)"', enabled[1]) if enabled else []
        values += [p for p in plugins if p not in values]
        entry = 'enabled=PackedStringArray(' + ', '.join(json.dumps(v) for v in values) + ')'
        body = body[:enabled.start()] + entry + body[enabled.end():] if enabled else body + entry + "\n"
        config = config[:section.start(1)] + body + config[section.end(1):]
    else:
        config += '\n[editor_plugins]\nenabled=PackedStringArray(' + ', '.join(json.dumps(p) for p in plugins) + ')\n'
    # Godot scans the main assembly. Register library scripts before any other autoload or plugin.
    loader = 'MajoAssemblies="*res://addons/majo_runtime/Build/MajoAssemblyLoader.cs"\n'
    section = re.search(r'^\[autoload\]\s*\n(.*?)(?=^\[|\Z)', config, re.M | re.S)
    if section:
        body = section[1]
        existing_loader = re.search(r'^MajoAssemblies\s*=.*$', body, re.M)
        if existing_loader and existing_loader[0].strip() != loader.strip():
            raise ValueError("autoload/MajoAssemblies is already used; resolve the host name conflict first")
        body = re.sub(r'^MajoAssemblies\s*=.*(?:\n|$)', '', body, flags=re.M)
        config = config[:section.start(1)] + loader + body.lstrip('\n') + config[section.end(1):]
    else:
        config += '\n[autoload]\n' + loader
    attributes = project / ".gitattributes"
    attrs = attributes.read_text() if attributes.exists() else ""
    for addon in ADDONS:
        rule = f"/addons/{addon}/** -text"
        if rule not in attrs.splitlines():
            attrs = attrs.rstrip() + "\n" + rule + "\n"
    return {csproj: text.encode(), settings: config.encode(), attributes: attrs.encode()}


def find_project(project):
    candidates = list(project.glob("*.csproj"))
    if len(candidates) != 1 or not (project / "project.godot").is_file():
        raise ValueError("Expected project.godot and exactly one root .csproj")
    return candidates[0]


def install(project, content, provenance, report=True, restore_config=None, restore_lock=None):
    validate(content)
    csproj = find_project(project)
    previous = load_lock(project)
    ensure_clean(project, previous, content)
    config = configured(project, csproj)
    if not list(project.glob("*.sln")):
        solution = content.get("addons/majo_runtime/Tools/Template/Example.sln.template")
        if solution is None:
            raise ValueError("Project has no .sln and the package has no solution template")
        config[project / (csproj.stem + ".sln")] = solution.decode("utf-8-sig").replace("Example", csproj.stem).encode()
    if restore_config is not None:
        config.update({project / p: data for p, data in restore_config.items()})
    if any(p.is_symlink() for p in [*config, project / LOCK]):
        raise ValueError("Host configuration and lock files must not be symlinks")
    compatibility = json.loads(content.get("addons/majo_runtime/compatibility.json", b"{}"))
    checksums = {p: digest(b) for p, b in sorted(content.items())}
    record = {"format": 1, "version": version(content["addons/majo_runtime/plugin.cfg"]),
              "source": provenance, "files": checksums,
              "contentHash": digest(json.dumps(checksums, sort_keys=True).encode()),
              "compatibility": compatibility}
    # Stage everything before replacing folders; keep a rollback copy until configs and lock commit.
    with tempfile.TemporaryDirectory(prefix="majo-install-", dir=project) as temp:
        staging = Path(temp).resolve()
        for path, data in content.items():
            target = staging / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        backups = {}
        old_config = {p: p.read_bytes() if p.exists() else None for p in [*config, project / LOCK]}
        try:
            (project / "addons").mkdir(exist_ok=True)
            for addon in ADDONS:
                target = project / "addons" / addon
                if target.exists():
                    backup = staging / (addon + ".backup")
                    target.rename(backup)
                    backups[target] = backup
                else:
                    backups[target] = None
                (staging / "addons" / addon).rename(target)
            for path, data in config.items():
                if data is None: path.unlink(missing_ok=True)
                else: path.write_bytes(data)
            (project / LOCK).write_bytes(restore_lock if restore_lock is not None else
                                        (json.dumps(record, indent=2, sort_keys=True) + "\n").encode())
        except BaseException:
            for target, backup in backups.items():
                if target.exists(): shutil.rmtree(target)
                if backup: backup.rename(target)
            for path, data in old_config.items():
                if data is None: path.unlink(missing_ok=True)
                else: path.write_bytes(data)
            raise
    if report:
        print(f"Installed Majo {record['version']} into {project}", flush=True)
    return record


def game_checks(project, explicit=None):
    if explicit:
        import shlex
        return [{"argv": shlex.split(explicit), "timeoutSeconds": 240}]
    path = project / "majo-check.json"
    if not path.exists():
        print("No majo-check.json: checking build/import only; register game regressions before release.", flush=True)
        return []
    profile = json.loads(path.read_text())
    if profile.get("format") != 1 or not isinstance(profile.get("commands"), list) or not profile["commands"]:
        raise ValueError("Unsupported or empty majo-check.json profile")
    commands = []
    import sys
    for entry in profile["commands"]:
        argv = entry.get("argv")
        timeout = entry.get("timeoutSeconds", 240)
        marker = entry.get("requiredOutput")
        if not isinstance(argv, list) or not argv or any(not isinstance(arg, str) or not arg for arg in argv):
            raise ValueError("Game check argv must be a non-empty array of strings")
        if not isinstance(timeout, int) or not 1 <= timeout <= 600 or marker is not None and (not isinstance(marker, str) or not marker):
            raise ValueError("Invalid game check timeout or requiredOutput")
        commands.append({"argv": [sys.executable if arg == "{python}" else arg for arg in argv],
                         "timeoutSeconds": timeout, "requiredOutput": marker})
    return commands


def run_checks(project, godot=None, check_command=None):
    """Validate a disposable candidate copy; never execute checks in the live game."""
    executable = godot or os.environ.get("GODOT_BIN") or shutil.which("godot")
    if not executable or not Path(executable).is_file():
        raise ValueError("--check requires --godot /path/to/Godot .NET")
    commands = [
        {"argv": ["dotnet", "build", str(find_project(project)), "--nologo"]},
        {"argv": [executable, "--headless", "--path", str(project), "--editor", "--quit"]},
        {"argv": [executable, "--headless", "--path", str(project), "--editor", "--quit", "--", "--majo-validate-export"],
         "requiredOutput": "MAJO_EXPORT_VALIDATED"},
        *game_checks(project, check_command),
    ]
    for entry in commands:
        print("Checking candidate:", " ".join(entry["argv"]), flush=True)
        result = subprocess.run(entry["argv"], cwd=project, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True, timeout=entry.get("timeoutSeconds", 240))
        print(result.stdout, end="", flush=True)
        marker = entry.get("requiredOutput")
        if result.returncode or marker and marker not in result.stdout:
            raise RuntimeError("Candidate validation failed; the installed game was not changed")


def host_paths(project):
    return list(dict.fromkeys([find_project(project), project / "project.godot", project / ".gitattributes",
                              project / (find_project(project).stem + ".sln"), *project.glob("*.sln")]))


def save_snapshot(project):
    import uuid
    previous = load_lock(project)
    if previous is None:
        return None
    identifier = time.strftime("%Y%m%dT%H%M%S", time.gmtime()) + "-" + uuid.uuid4().hex[:8]
    directory = project / ".majo/history" / identifier
    directory.mkdir(parents=True)
    config = {p.relative_to(project).as_posix(): p.read_bytes() if p.exists() else None for p in host_paths(project)}
    with zipfile.ZipFile(directory / "snapshot.zip", "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in previous["files"]:
            archive.writestr(path, (project / path).read_bytes())
        archive.writestr(LOCK, (project / LOCK).read_bytes())
        for path, data in config.items():
            if data is not None: archive.writestr(path, data)
    metadata = {"format": 1, "id": identifier, "version": previous["version"], "status": "pending",
                "config": {p: b is not None for p, b in config.items()}}
    (directory / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")
    return directory


def mark_snapshot(directory, status):
    if directory:
        metadata = json.loads((directory / "metadata.json").read_text())
        metadata["status"] = status
        (directory / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")


def checked_install(project, content, provenance, check=False, godot=None, check_command=None, allow_breaking=False):
    previous = load_lock(project)
    ensure_clean(project, previous, content)
    validate(content)
    new_contract = json.loads(content.get("addons/majo_runtime/compatibility.json", b"{}"))
    old_contract = (previous or {}).get("compatibility", {})
    current_contract = project / "addons/majo_runtime/compatibility.json"
    if not old_contract and current_contract.exists(): old_contract = json.loads(current_contract.read_text())
    changed = [key for key in ("apiVersion", "godot", "targetFramework", "yarnGodot", "yarnRuntime")
               if key in old_contract and old_contract.get(key) != new_contract.get(key)]
    if previous and (changed or new_contract.get("breakingChanges")) and not (allow_breaking and check):
        raise ValueError(f"Compatibility boundary changed ({', '.join(changed)}); review migrations and use --allow-breaking --check")
    if check:
        with tempfile.TemporaryDirectory(prefix="majo-candidate-") as temp:
            stage = Path(temp).resolve() / "game"
            shutil.copytree(project, stage, ignore=shutil.ignore_patterns(
                *IGNORED, "artifacts", ".majo", "TestResults", ".tools"))
            install(stage, content, provenance, report=False)
            run_checks(stage, godot, check_command)
    snapshot = save_snapshot(project)
    try:
        record = install(project, content, provenance)
    except BaseException:
        mark_snapshot(snapshot, "failed")
        raise
    mark_snapshot(snapshot, "available")
    if snapshot:
        metadata = json.loads((snapshot / "metadata.json").read_text())
        metadata["installedConfig"] = {p.relative_to(project).as_posix(): digest(p.read_bytes()) if p.exists() else None
                                       for p in host_paths(project)}
        (snapshot / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")
    if snapshot: print("Rollback point:", snapshot.name, flush=True)
    return record


def rollback(project, identifier=None, list_only=False, check=False, godot=None, check_command=None):
    directories = sorted((project / ".majo/history").glob("*/metadata.json"), reverse=True)
    available = [(path.parent, json.loads(path.read_text())) for path in directories]
    available = [(path, info) for path, info in available if info["status"] == "available"]
    if list_only:
        print(json.dumps([info for _, info in available], indent=2))
        return
    selected = next(((path, info) for path, info in available if identifier is None or info["id"] == identifier), None)
    if selected is None: raise ValueError("No matching rollback point")
    directory, info = selected
    if info.get("format") != 1: raise ValueError("Unsupported rollback snapshot format")
    # Only root host files may be restored outside the managed addon directories.
    if any('/' in p or '\\' in p or p not in {"project.godot", ".gitattributes"} and not p.endswith((".csproj", ".sln")) for p in info["config"]):
        raise ValueError("Invalid snapshot configuration paths")
    with zipfile.ZipFile(directory / "snapshot.zip") as archive:
        names = archive.namelist()
        old_lock = json.loads(archive.read(LOCK))
        content = {p: archive.read(p) for p in names if safe_path(p)}
        configs = {p: archive.read(p) if exists else None for p, exists in info["config"].items()}
        lock_bytes = archive.read(LOCK)
    if {p: digest(b) for p, b in content.items()} != old_lock["files"]:
        raise ValueError("Rollback snapshot hashes do not match its lock")
    ensure_clean(project, load_lock(project))
    for path, checksum in info.get("installedConfig", {}).items():
        if "/" in path or "\\" in path: raise ValueError("Invalid snapshot host path")
        current = digest((project / path).read_bytes()) if (project / path).exists() else None
        if current != checksum: raise ValueError(f"Host configuration changed since upgrade: {path}; merge it before rollback")
    if check:
        with tempfile.TemporaryDirectory(prefix="majo-rollback-check-") as temp:
            stage = Path(temp).resolve() / "game"
            shutil.copytree(project, stage, ignore=shutil.ignore_patterns(*IGNORED, "artifacts", ".majo", ".tools"))
            install(stage, content, old_lock["source"], report=False)
            for p, data in configs.items():
                if data is None: (stage / p).unlink(missing_ok=True)
                else: (stage / p).write_bytes(data)
            (stage / LOCK).write_bytes(lock_bytes)
            run_checks(stage, godot, check_command)
    install(project, content, old_lock["source"], report=False,
            restore_config=configs, restore_lock=lock_bytes)
    mark_snapshot(directory, "restored")
    print(f"Restored Majo {old_lock['version']} from {info['id']}. Rebuild/import the game before running.")


def initialize(project, name, content, provenance, check=False, godot=None, check_command=None):
    if project.exists() and any(project.iterdir()):
        raise ValueError("init requires an empty game directory")
    if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]*', name):
        raise ValueError("Game name must be a C# identifier (letters, numbers, underscores)")
    prefix = "addons/majo_runtime/Tools/Template/"
    templates = {p[len(prefix):-len('.template')]: b for p, b in content.items()
                 if p.startswith(prefix) and p.endswith('.template')}
    if not templates:
        raise ValueError("Package has no game template")
    project.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".majo-init-", dir=project.parent) as temp:
        staged = Path(temp).resolve() / "game"
        staged.mkdir()
        for path, data in templates.items():
            path = path.replace("Example", name)
            text = data.decode('utf-8-sig').replace("Example", name)
            if path == "project.godot":
                text = text.replace("Majo Runtime " + name, name)
            destination = staged / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_text(text)
        install(staged, content, provenance, report=False)
        if check: run_checks(staged, godot, check_command)
        if project.exists():
            project.rmdir()  # Only an empty destination is allowed.
        staged.rename(project)
    print(f"Created {name} with managed Majo addons in {project}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["init", "install", "upgrade", "rollback", "sync", "watch", "doctor"])
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--source", type=Path, help="Local system repository, not its addons folder")
    parser.add_argument("--archive", type=Path, help="Bundled with-yarn ZIP")
    parser.add_argument("--sha256", help="Expected archive SHA-256")
    parser.add_argument("--name", default="Game")
    parser.add_argument("--no-build", action="store_true", help="Watch: sync without dotnet build")
    parser.add_argument("--check", action="store_true", help="Validate candidate in an isolated game copy before installation")
    parser.add_argument("--godot", help="Godot .NET executable used by --check")
    parser.add_argument("--check-command", help="Additional game regression command, executed without a shell in the candidate copy")
    parser.add_argument("--to", help="Rollback point ID; defaults to the latest available point")
    parser.add_argument("--list", action="store_true", help="List available rollback points")
    parser.add_argument("--allow-breaking", action="store_true", help="Explicitly accept a compatibility boundary change; requires --check")
    args = parser.parse_args()
    project = args.project.resolve()
    if args.command == "rollback":
        rollback(project, args.to, args.list, args.check, args.godot, args.check_command)
        return
    if args.command == "doctor":
        find_project(project)
        record = load_lock(project)
        if not record: raise ValueError("No majo.lock.json; install with this tool first")
        ensure_clean(project, record)
        validate({p: (project / p).read_bytes() for p in record["files"]})
        updates = configured(project, find_project(project))
        if any(p.read_bytes() != b for p, b in updates.items()):
            raise ValueError("Host imports, MajoAssemblies autoload or editor plugins are missing; run install/sync to configure")
        print(json.dumps({k: v for k, v in record.items() if k != "files"}, indent=2))
        print("Majo dependencies, file hashes, imports, autoload and editor plugins OK")
        return
    if bool(args.source) == bool(args.archive):
        raise ValueError("Specify exactly one of --source or --archive")
    if args.command in {"sync", "watch"} and not args.source:
        raise ValueError("sync/watch require --source")
    if args.source and (project == args.source.resolve() or args.source.resolve() in project.parents
                        or project in args.source.resolve().parents):
        raise ValueError("Keep the game outside the system checkout")
    def read():
        result = read_source(args.source) if args.source else read_archive(args.archive, args.sha256)
        validate(result[0])
        return result
    content, provenance = read()
    if args.command == "init":
        initialize(project, args.name, content, provenance, args.check, args.godot, args.check_command)
    elif args.command in {"upgrade", "install"}:
        checked_install(project, content, provenance, args.check, args.godot, args.check_command, args.allow_breaking)
    else:
        install(project, content, provenance)
    if args.command != "watch":
        print(f"Next: dotnet build in {project}, then open its project.godot and let imports finish")
        return
    last = {p: digest(b) for p, b in content.items()}
    def build():
        if not args.no_build:
            result = subprocess.run(["dotnet", "build", "--nologo"], cwd=project)
            if result.returncode: print("Build failed; fix source and save again. Restart the game after a successful build.", flush=True)
    build()
    print("Watching source addon files. Stop running games before rebuilding; Ctrl+C exits.", flush=True)
    while True:
        time.sleep(1)
        content, provenance = read()
        current = {p: digest(b) for p, b in content.items()}
        if current == last: continue
        time.sleep(0.5)
        stable, stable_provenance = read()
        if {p: digest(b) for p, b in stable.items()} != current:
            continue
        content, provenance = stable, stable_provenance
        install(project, content, provenance)
        last = current
        build()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except (ValueError, OSError, RuntimeError, zipfile.BadZipFile, ET.ParseError) as error:
        raise SystemExit(str(error))
