#!/usr/bin/env python3
"""Build deterministic, cropped sprite atlases from the objection.lol fixture."""

from __future__ import annotations

import json
import re
import shutil
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1] / "demos" / "ace-attorney"
SOURCE_MANIFEST = ROOT / "asset_manifest.json"
PAGE_SIZE = 2048
PADDING = 2


def slug(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")


def build_pose(character_dir: Path, pose: dict) -> dict:
    pose_id = slug(pose["name"])
    destination = character_dir / "poses" / pose_id
    destination.mkdir(parents=True, exist_ok=True)
    for stale in destination.glob("atlas_*.png"):
        stale.unlink()

    pages: list[Image.Image] = [Image.new("RGBA", (PAGE_SIZE, PAGE_SIZE))]
    cursor_x = cursor_y = row_height = 0
    placed: dict[str, tuple[int, list[int], list[int]]] = {}

    def add_frame(frame: dict) -> dict:
        nonlocal cursor_x, cursor_y, row_height
        path = frame["file"]
        if path not in placed:
            image = Image.open(ROOT / path).convert("RGBA")
            bounds = image.getchannel("A").getbbox() or (0, 0, 1, 1)
            cropped = image.crop(bounds)
            width, height = cropped.size
            if width + PADDING > PAGE_SIZE or height + PADDING > PAGE_SIZE:
                raise ValueError(f"Frame exceeds page size: {path}")
            if cursor_x + width + PADDING > PAGE_SIZE:
                cursor_x = 0
                cursor_y += row_height
                row_height = 0
            if cursor_y + height + PADDING > PAGE_SIZE:
                pages.append(Image.new("RGBA", (PAGE_SIZE, PAGE_SIZE)))
                cursor_x = cursor_y = row_height = 0
            pages[-1].alpha_composite(cropped, (cursor_x, cursor_y))
            placed[path] = (
                len(pages) - 1,
                [cursor_x, cursor_y, width, height],
                [bounds[0], bounds[1]],
            )
            cursor_x += width + PADDING
            row_height = max(row_height, height + PADDING)
        page, rect, offset = placed[path]
        return {"page": page, "rect": rect, "offset": offset,
                "duration_ms": frame["duration_ms"]}

    def clip(animation: dict) -> dict:
        return {"frames": [add_frame(frame) for frame in animation["frames"]]}

    idle = clip(pose["idle"])
    speak = clip(pose["speak"])
    intro = [
        {**clip(state["animation"]),
         "next_pose_delay_ms": state["next_pose_delay_ms"],
         "no_speak_delay": state["no_speak_delay"]}
        for state in pose["intro"]
    ]
    for number, image in enumerate(pages):
        image.save(destination / f"atlas_{number:02d}.png", optimize=True)

    return {
        "id": pose_id,
        "source_id": pose["id"],
        "pages": [f"poses/{pose_id}/atlas_{number:02d}.png"
                  for number in range(len(pages))],
        "idle": idle,
        "speak": speak,
        "intro": intro,
        "audio_ticks": [
            {"file": tick["audio"]["file"], "time_ms": tick["time_ms"],
             "volume_percent": tick["volume_percent"]}
            for tick in pose["audio_ticks"]
        ],
    }


def main() -> None:
    source = json.loads(SOURCE_MANIFEST.read_text())
    for character in source["characters"]:
        character_dir = ROOT / "assets" / "characters" / character["asset_dir"].split("/")[-1]
        character_dir.mkdir(parents=True, exist_ok=True)
        pose_root = character_dir / "poses"
        expected = {slug(pose["name"]) for pose in character["poses"]}
        if pose_root.is_dir():
            for directory in pose_root.iterdir():
                if directory.is_dir() and directory.name not in expected:
                    shutil.rmtree(directory)
        poses = [build_pose(character_dir, pose) for pose in character["poses"]]
        canvas = character["poses"][0]["idle"]["canvas"]
        for pose in character["poses"]:
            animations = [pose["idle"], pose["speak"]] + [
                state["animation"] for state in pose["intro"]
            ]
            if any(animation["canvas"] != canvas for animation in animations):
                raise ValueError(f"Mixed canvas sizes for {character_dir.name}")
        manifest = {
            "schema_version": 1,
            "id": character_dir.name,
            "source_id": character["id"],
            "canvas": canvas,
            "default_pose": poses[0]["id"],
            "poses": poses,
        }
        (character_dir / "sprite_character.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        print(f"{character_dir.name}: {len(poses)} poses, "
              f"{sum(len(p['pages']) for p in poses)} pages")


if __name__ == "__main__":
    main()
