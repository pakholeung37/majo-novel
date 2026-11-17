"""Native Godot export preset, architecture and .NET runtime identifiers."""
import platform
import sys
from typing import NamedTuple


class ExportTarget(NamedTuple):
    preset: str
    architecture: str
    rid: str


def export_target(system=None, machine=None):
    system = sys.platform if system is None else system
    machine = platform.machine() if machine is None else machine
    architectures = {"x86_64": ("x86_64", "x64"), "amd64": ("x86_64", "x64"),
                     "arm64": ("arm64", "arm64"), "aarch64": ("arm64", "arm64")}
    systems = {"darwin": ("Consumer macOS", "osx"), "linux": ("Consumer Linux", "linux"),
               "win32": ("Consumer Windows", "win")}
    if system not in systems or machine.lower() not in architectures:
        raise ValueError(f"Unsupported export target: {system}/{machine}")
    preset, os_id = systems[system]
    architecture, dotnet_arch = architectures[machine.lower()]
    return ExportTarget(preset, architecture, f"{os_id}-{dotnet_arch}")
