#!/usr/bin/env python3
"""Fetch a small, reproducible objection.lol sprite-animation fixture."""

from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
from concurrent.futures import ThreadPoolExecutor
from io import BytesIO
from pathlib import Path
from urllib.parse import quote

from PIL import Image, ImageDraw


BASE_URL = "https://objection.lol"
ROOT = Path(__file__).resolve().parents[1]
DEMO = ROOT / "demos" / "ace-attorney"
ASSETS = DEMO / "assets"
MANIFEST = DEMO / "asset_manifest.json"

# Each character covers idle/speech; selected action poses also cover one-shot
# transitions and timed sound cues. IDs come from the public preset API.
SELECTION = {
    1: [1, 2, 3],       # Phoenix: stand, desk slam, point
    2: [5, 6, 9],       # Edgeworth: stand, desk slam, confident shrug
    10: [30, 31, 44],   # Judge: stand, negative, positive
    14: [77, 78, 79],   # Maya (witness): stand, think, happy
    21: [130, 131, 137],  # Dick: stand, confident, headscratch
}
CHARACTER_NAMES = {1: "Phoenix", 2: "Miles", 10: "Judge", 14: "Maya", 21: "Dick"}
BACKGROUND_NAMES = {189: "Defense", 194: "Prosecution", 192: "Judge", 197: "Witness"}
MUSIC_IDS = [2, 1, 14, 4, 15, 701]  # Courtroom cues plus Maya/Dick themes
MUSIC_FILE_NAMES = {15: "Maya", 701: "Dick"}
GENERIC_OBJECTION_URL = "/Audio/Vocal/sfx-objection.wav"


def fetch(url: str) -> bytes:
    result = subprocess.run(
        ["curl", "--globoff", "-fsSL", "--retry", "5", "--retry-all-errors", "--retry-delay", "1", "--max-time", "45", url],
        check=True,
        capture_output=True,
    )
    return result.stdout


def absolute_url(path: str) -> str:
    return BASE_URL + quote(path, safe="/())-_.~[]")


def previous_assets() -> dict[str, Path]:
    """Reuse verified local downloads while changing the folder layout."""
    if not MANIFEST.is_file():
        return {}
    cache = {}

    def visit(value: object) -> None:
        if isinstance(value, dict):
            if "source_url" in value and "file" in value:
                file = DEMO / value["file"]
                if file.is_file():
                    cache[value["source_url"]] = file
            for child in value.values():
                visit(child)
        elif isinstance(value, list):
            for child in value:
                visit(child)

    visit(json.loads(MANIFEST.read_text()))
    return cache


def fetch_asset(path: str, cache: dict[str, Path]) -> bytes:
    previous = cache.get(absolute_url(path))
    return previous.read_bytes() if previous else fetch(absolute_url(path))


def relative(path: Path) -> str:
    return path.relative_to(DEMO).as_posix()


def save_resource(path: str, data: bytes, destination: Path) -> dict:
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(data)
    return {
        "file": relative(destination),
        "source_url": absolute_url(path),
        "sha256": hashlib.sha256(data).hexdigest(),
        "bytes": len(data),
    }


def save_animation(path: str, data: bytes, animation_dir: Path) -> dict:
    source = save_resource(path, data, animation_dir / "source.gif")
    image = Image.open(BytesIO(data))
    if image.format != "GIF":
        raise ValueError(f"Expected GIF at {path}, got {image.format}")

    frames_dir = animation_dir / "frames"
    frames_dir.mkdir(parents=True, exist_ok=True)
    frames = []
    for index in range(image.n_frames):
        image.seek(index)
        frame_path = frames_dir / f"{index:03d}.png"
        # Pillow presents the composited logical GIF frame here, accounting for
        # partial updates and disposal while retaining the common canvas origin.
        image.convert("RGBA").save(frame_path, optimize=True)
        frames.append({
            "file": relative(frame_path),
            "duration_ms": image.info.get("duration", 100),
        })
    return {
        "source": source,
        "canvas": [image.width, image.height],
        "loop": image.info.get("loop", 0),
        "frames": frames,
    }


def save_preview(manifest: dict) -> None:
    cell_width, cell_height = 320, 235
    sheet = Image.new("RGB", (cell_width * 3, cell_height * len(manifest["characters"])), "#263044")
    draw = ImageDraw.Draw(sheet)
    for row, character in enumerate(manifest["characters"]):
        for column, pose in enumerate(character["poses"]):
            animation = pose["intro"][0]["animation"] if pose["intro"] else pose["idle"]
            frame = Image.open(DEMO / animation["frames"][0]["file"]).convert("RGBA")
            frame.thumbnail((cell_width, 210))
            background = Image.new("RGBA", (cell_width, 210), "#ddd9cb")
            background.alpha_composite(frame, ((cell_width - frame.width) // 2, (210 - frame.height) // 2))
            x, y = column * cell_width, row * cell_height
            sheet.paste(background.convert("RGB"), (x, y))
            draw.text((x + 8, y + 215), f"{character['name']} / {pose['name']}", fill="white")
    sheet.save(ASSETS / "preview.png")


def main() -> None:
    characters = json.loads(fetch(BASE_URL + "/api/assets/character/getPreset"))
    music_catalog = json.loads(fetch(BASE_URL + "/api/assets/music/getPreset"))
    selected = {character["id"]: character for character in characters if character["id"] in SELECTION}
    if set(selected) != set(SELECTION):
        raise ValueError("One or more selected preset characters are unavailable")
    music_by_id = {track["id"]: track for track in music_catalog}
    if not set(MUSIC_IDS) <= set(music_by_id):
        raise ValueError("One or more selected BGM tracks are unavailable")

    background_data = {}
    paths = {GENERIC_OBJECTION_URL}
    objection_bubbles = {}
    for character_id, pose_ids in SELECTION.items():
        character = selected[character_id]
        poses = {pose["id"]: pose for pose in character["poses"]}
        if not set(pose_ids) <= set(poses):
            raise ValueError(f"Selected pose missing for {character['name']}")
        paths.add(character["blipUrl"])
        for pose_id in pose_ids:
            pose = poses[pose_id]
            paths.update((pose["idleImageUrl"], pose["speakImageUrl"]))
            paths.update(state["imageUrl"] for state in pose["poseStates"])
            paths.update(tick["fileName"] for tick in pose["poseAudioTicks"])

        background_id = character["backgroundId"]
        background = json.loads(fetch(BASE_URL + f"/api/assets/background/{background_id}"))
        background_data[background_id] = background
        paths.update((background["url"], background["deskUrl"]))
        if character_id != 10:
            bubble = next((bubble for bubble in character["speechBubbles"] if bubble["name"] == "Objection!"), None)
            if not bubble:
                raise ValueError(f"Objection callout missing for {character['name']}")
            objection_bubbles[character_id] = bubble
            paths.update((bubble["imageUrl"], bubble["soundUrl"]))

    tracks = [music_by_id[id] for id in MUSIC_IDS]
    paths.update(track["url"] for track in tracks)

    print(f"Downloading {len(paths)} distinct public assets...")
    cache = previous_assets()
    with ThreadPoolExecutor(max_workers=2) as pool:
        downloaded = dict(zip(sorted(paths), pool.map(lambda path: fetch_asset(path, cache), sorted(paths))))

    background_paths = {}
    for background_id, background in background_data.items():
        name = BACKGROUND_NAMES[background_id]
        background_paths[background["url"]] = ASSETS / "backgrounds" / f"{name}{Path(background['url']).suffix}"
        background_paths[background["deskUrl"]] = ASSETS / "backgrounds" / f"{name}_Desk{Path(background['deskUrl']).suffix}"
    music_paths = {
        track["url"]: ASSETS / "audio" / "bgm" / f"{MUSIC_FILE_NAMES.get(track['id'], track['name'])}{Path(track['url']).suffix}"
        for track in tracks
    }
    voice_paths = {
        bubble["soundUrl"]: ASSETS / "audio" / "voice" / f"{CHARACTER_NAMES[character_id]}_Objection{Path(bubble['soundUrl']).suffix}"
        for character_id, bubble in objection_bubbles.items()
        if bubble["soundUrl"] != GENERIC_OBJECTION_URL
    }
    bubble_image_path = next(iter(objection_bubbles.values()))["imageUrl"]
    resources = {}
    for path, data in downloaded.items():
        if path.lower().endswith(".gif"):
            character_id = int(Path(path).parts[3])
            destination = ASSETS / "characters" / CHARACTER_NAMES[character_id] / "animations" / Path(path).stem
            resources[path] = save_animation(path, data, destination)
        elif path in background_paths:
            resources[path] = save_resource(path, data, background_paths[path])
        elif path in music_paths:
            resources[path] = save_resource(path, data, music_paths[path])
        elif path in voice_paths:
            resources[path] = save_resource(path, data, voice_paths[path])
        elif path == bubble_image_path:
            resources[path] = save_resource(path, data, ASSETS / "ui" / "Objection.png")
        elif path == GENERIC_OBJECTION_URL:
            resources[path] = save_resource(path, data, ASSETS / "audio" / "sfx" / "Objection.wav")
        elif path.startswith("/Audio/"):
            name = "Blip.wav" if path == "/Audio/blip.wav" else Path(path).name
            resources[path] = save_resource(path, data, ASSETS / "audio" / "sfx" / name)
        else:
            raise ValueError(f"Unexpected resource path: {path}")

    manifest = {
        "source": BASE_URL,
        "catalog_url": BASE_URL + "/api/assets/character/getPreset",
        "frame_origin": "top_left",
        "frame_timing_unit": "milliseconds",
        "characters": [],
        "backgrounds": [],
        "callouts": [],
        "sound_effects": [],
        "music": [],
    }
    for character_id, pose_ids in SELECTION.items():
        character = selected[character_id]
        character_dir = ASSETS / "characters" / CHARACTER_NAMES[character_id]
        manifest["characters"].append({
            "id": character_id,
            "name": character["name"],
            "asset_dir": relative(character_dir),
            "side": character["side"],
            "background_id": character["backgroundId"],
            "offset": [character["offsetX"], character["offsetY"]],
            "blip": resources[character["blipUrl"]],
            "poses": [
                {
                    "id": pose["id"],
                    "name": pose["name"],
                    "idle": resources[pose["idleImageUrl"]],
                    "speak": resources[pose["speakImageUrl"]],
                    "intro": [
                        {
                            "animation": resources[state["imageUrl"]],
                            "next_pose_delay_ms": state["nextPoseDelay"],
                            "no_speak_delay": state["noSpeakDelay"],
                        }
                        for state in pose["poseStates"]
                    ],
                    "audio_ticks": [
                        {
                            "audio": resources[tick["fileName"]],
                            "time_ms": tick["time"],
                            "volume_percent": tick["volume"],
                        }
                        for tick in pose["poseAudioTicks"]
                    ],
                }
                for pose in (next(p for p in character["poses"] if p["id"] == pose_id) for pose_id in pose_ids)
            ],
        })
    for background_id in sorted(background_data):
        background = background_data[background_id]
        manifest["backgrounds"].append({
            "id": background_id,
            "name": background["name"],
            "image": resources[background["url"]],
            "desk": resources[background["deskUrl"]],
        })
    for character_id, bubble in objection_bubbles.items():
        manifest["callouts"].append({
            "character_id": character_id,
            "name": bubble["name"],
            "duration_ms": bubble["duration"],
            "image": resources[bubble["imageUrl"]],
            "voice": resources[bubble["soundUrl"]],
        })
    manifest["sound_effects"].append({"name": "Objection!", "audio": resources[GENERIC_OBJECTION_URL]})
    for track in tracks:
        manifest["music"].append({
            "id": track["id"],
            "name": track["name"],
            "volume_percent": track["volume"],
            "audio": resources[track["url"]],
        })

    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    save_preview(manifest)
    # Remove only directories produced by earlier versions of this fixture.
    for old_directory in (
        ASSETS / "original", ASSETS / "frames",
        *(ASSETS / "characters" / name for name in ("Phoenix Wright", "Miles Edgeworth", "The Judge")),
    ):
        if old_directory.is_dir():
            shutil.rmtree(old_directory)
    print(f"Wrote {MANIFEST.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
