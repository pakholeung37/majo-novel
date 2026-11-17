#!/usr/bin/env python3
"""Build deterministic source addon archives and verify the pinned upstream dependency."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import zipfile

ROOT = Path(__file__).resolve().parent.parent
IGNORED_DIRS = {"bin", "obj", ".godot", ".git", "__pycache__"}


def addon_files(directory):
    return sorted(
        path for path in directory.rglob("*")
        if path.is_file()
        and not IGNORED_DIRS.intersection(path.relative_to(directory).parts)
        and path.name != ".DS_Store"
        and path.suffix not in {".tmp", ".log"}
    )


def plugin_version(directory):
    match = re.search(r'^version\s*=\s*"([^"]+)"', (directory / "plugin.cfg").read_text(), re.M)
    if not match:
        raise ValueError(f"Missing plugin version: {directory}")
    return match.group(1)


def verify_upstream():
    manifest = json.loads((ROOT / "tools/yarn-upstream.json").read_text())
    directory = ROOT / "addons/YarnSpinner-Godot"
    files = addon_files(directory)
    actual = {path.relative_to(directory).as_posix() for path in files}
    if actual != set(manifest["files"]):
        raise ValueError("Vendored Yarn file list differs from the upstream manifest")
    for path in files:
        content = path.read_bytes()
        digest = hashlib.sha1(b"blob " + str(len(content)).encode() + b"\0" + content).hexdigest()
        if digest != manifest["files"][path.relative_to(directory).as_posix()]:
            raise ValueError(f"Vendored Yarn file was modified: {path}")
    if plugin_version(directory) != manifest["version"]:
        raise ValueError("Yarn plugin version differs from its upstream manifest")
    props = (directory / "YarnSpinner-Godot.props").read_text()
    if f'Include="YarnSpinner" Version="{manifest["runtimeVersion"]}"' not in props:
        raise ValueError("Yarn package version differs from its upstream manifest")
    return manifest, files


def build_archive(destination, sources, metadata):
    contents = {path.relative_to(ROOT).as_posix(): path.read_bytes() for path in sources}
    contents["addons/majo_runtime/release-manifest.json"] = (
        json.dumps(metadata, indent=2, sort_keys=True) + "\n").encode()
    with zipfile.ZipFile(destination, "w") as archive:
        for name, content in sorted(contents.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, content, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    with zipfile.ZipFile(destination) as archive:
        if archive.testzip() is not None:
            raise ValueError(f"Archive verification failed: {destination}")
    return hashlib.sha256(destination.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build/releases")
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    upstream, yarn_files = verify_upstream()
    directory = ROOT / "addons/majo_runtime"
    version = plugin_version(directory)
    metadata = (directory / "Runtime/Core/MajoRuntimeMetadata.cs").read_text()
    if f'PluginVersion = "{version}"' not in metadata:
        raise ValueError("Majo plugin.cfg and runtime version differ")
    for required in (
        "README.md", "LICENSE", "THIRD_PARTY_NOTICES.md", "CHANGELOG.md", "MajoRuntime.props",
        "Bridges/YarnSpinnerStateBridge/YarnSpinnerStateBridge.csproj",
        "Assemblies/Majo.Runtime.Core/Majo.Runtime.Core.csproj",
        "Assemblies/Majo.Runtime.Domain/Majo.Runtime.Domain.csproj",
        "Assemblies/Majo.Runtime.Domain/Majo.Runtime.Domain.sources.props",
        "Assemblies/Majo.Runtime.Commands/Majo.Runtime.Commands.csproj",
        "Assemblies/Majo.Runtime.Commands/Majo.Runtime.Commands.sources.props",
        "Assemblies/Majo.Runtime.Persistence/Majo.Runtime.Persistence.csproj",
        "Assemblies/Majo.Runtime.Persistence/Majo.Runtime.Persistence.sources.props",
        "Assemblies/Majo.Runtime.Resources/Majo.Runtime.Resources.csproj",
        "Assemblies/Majo.Runtime.Resources/Majo.Runtime.Resources.sources.props",
        "Assemblies/GodotLibrary.props",
        "Assemblies/Majo.Runtime.Engine/Majo.Runtime.Engine.csproj",
        "Assemblies/Majo.Runtime.Engine/Majo.Runtime.Engine.sources.props",
        "Assemblies/Majo.Runtime.Yarn/Majo.Runtime.Yarn.csproj",
        "Assemblies/Majo.Runtime.Yarn/Majo.Runtime.Yarn.sources.props",
        "Assemblies/Majo.Runtime.Godot/Majo.Runtime.Godot.csproj",
        "Assemblies/Majo.Runtime.Godot/Majo.Runtime.Godot.sources.props",
        "Assemblies/YarnSpinner.Godot/YarnSpinner.Godot.csproj",
        "Assemblies/YarnSpinner.Godot/YarnSpinner.Godot.sources.props",
        "Build/MajoAssemblyLoader.cs", "Build/YarnHostRegistration.cs",
        "SourceGenerators/Majo.CommandGenerator/Majo.CommandGenerator.csproj",
    ):
        if not (directory / required).is_file():
            raise ValueError(f"Missing addon release file: {required}")
    if args.verify_only:
        print(f"Majo {version}; official Yarn {upstream['version']}: {len(yarn_files)} files verified")
        return
    args.output_dir.mkdir(parents=True, exist_ok=True)
    majo_files = addon_files(directory)
    hashes = []
    for bundled in (False, True):
        name = f"majo-runtime-{version}{'-with-yarn' if bundled else ''}.zip"
        release = {
            "version": version, "godot": "4.7.2 .NET", "targetFramework": "net8.0",
            "yarnGodotVersion": upstream["version"], "yarnRuntimeVersion": upstream["runtimeVersion"],
            "yarnUpstreamCommit": upstream["commit"], "yarnBundled": bundled,
        }
        digest = build_archive(args.output_dir / name, majo_files + (yarn_files if bundled else []), release)
        hashes.append(f"{digest}  {name}")
        print(args.output_dir / name)
    example_root = ROOT / "addons/majo_runtime/Tools/Template"
    example_files = addon_files(example_root)
    name = f"majo-runtime-{version}-example.zip"
    with zipfile.ZipFile(args.output_dir / name, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        with zipfile.ZipFile(args.output_dir / f"majo-runtime-{version}-with-yarn.zip") as bundled:
            for entry in bundled.infolist():
                archive.writestr(entry, bundled.read(entry.filename))
        for path in example_files:
            info = zipfile.ZipInfo(path.relative_to(example_root).as_posix().removesuffix(".template"), (1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    hashes.append(f"{hashlib.sha256((args.output_dir / name).read_bytes()).hexdigest()}  {name}")
    print(args.output_dir / name)
    installer = (ROOT / "install.py").read_bytes()
    (args.output_dir / "install.py").write_bytes(installer)
    hashes.append(f"{hashlib.sha256(installer).hexdigest()}  install.py")
    (args.output_dir / "SHA256SUMS").write_text("\n".join(hashes) + "\n")


if __name__ == "__main__":
    main()
