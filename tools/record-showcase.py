#!/usr/bin/env python3
"""Record existing demos with synchronized Yarn source, audio, and a JSON timeline."""

import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


TARGETS = ("adv02", "trial00", "ace-attorney", "live2d")
ROOT = Path(__file__).resolve().parents[1]


def find_ffmpeg():
    executable = shutil.which("ffmpeg")
    if executable:
        return executable
    try:
        import imageio_ffmpeg

        return imageio_ffmpeg.get_ffmpeg_exe()
    except ImportError:
        return None


def convert(ffmpeg, folder, report, keep_master):
    # Source overlays are baked into the movie. A clean game video needs its own
    # recording in game mode; cropping a composite cannot remove the overlay.
    video = "game.mp4" if report["recordingStyle"] == "game" else "script.mp4"
    encoding = ["-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]
    command = [ffmpeg, "-y", "-i", str(folder / "master.avi"),
               "-map", "0:v", "-map", "0:a?", *encoding, str(folder / video)]
    with (folder / "encode.log").open("w") as log:
        subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
        dialogue = [event for event in report["events"] if event["kind"] == "dialogue"]
        seconds = (dialogue[min(len(dialogue) // 3, len(dialogue) - 1)]["seconds"] + 0.8
                   if dialogue else report["frames"] / report["fps"] / 2)
        seconds = min(seconds, report["frames"] / report["fps"] - 0.2)
        subprocess.run([ffmpeg, "-y", "-ss", str(seconds), "-i", str(folder / video),
                        "-frames:v", "1", "-update", "1", str(folder / "preview.png")],
                       stdout=log, stderr=subprocess.STDOUT, check=True)
    if not keep_master:
        (folder / "master.avi").unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("target", choices=(*TARGETS, "all"))
    parser.add_argument("--seconds", type=float, default=0, help="Preview duration; 0 records until the demo ends.")
    parser.add_argument("--fps", type=int, choices=(30, 60), default=30)
    parser.add_argument("--style", choices=("technical", "game"), default="technical",
                        help="Synchronized source overlay or clean game footage.")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--godot", default=os.environ.get("GODOT_BIN", "/Applications/Godot_mono.app/Contents/MacOS/Godot"))
    parser.add_argument("--keep-master", action="store_true", help="Retain Godot's MJPEG/PCM AVI after MP4 conversion.")
    parser.add_argument("--skip-build", action="store_true")
    args = parser.parse_args()
    if args.seconds < 0:
        parser.error("--seconds must be non-negative")
    godot = shutil.which(args.godot) or args.godot
    if not Path(godot).is_file():
        parser.error(f"Godot .NET executable not found: {godot}")
    output = (args.output or ROOT / "artifacts" / "recordings" /
              datetime.datetime.now().strftime("%Y%m%d-%H%M%S")).resolve()
    output.mkdir(parents=True, exist_ok=True)
    ffmpeg = find_ffmpeg()
    if not args.skip_build:
        with (output / "build.log").open("w") as log:
            subprocess.run(["dotnet", "build", str(ROOT / "Majo Novel.csproj"), "--nologo"],
                           cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
            subprocess.run([godot, "--headless", "--path", str(ROOT), "--editor", "--quit"],
                           cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
    targets = TARGETS if args.target == "all" else (args.target,)
    for target in targets:
        folder = output / target
        folder.mkdir(exist_ok=False)
        print(f"Recording {target} → {folder}", flush=True)
        command = [godot, "--path", str(ROOT), "--scene", "res://demos/recording/ShowcaseRecording.tscn",
                   "--resolution", "2560x1440", "--windowed", "--always-on-top", "--disable-vsync", "--fixed-fps", str(args.fps),
                   "--write-movie", str(folder / "master.avi"), "--",
                   f"--showcase={target}", f"--recording-output={folder}",
                   f"--recording-style={args.style}",
                   f"--recording-fps={args.fps}", f"--recording-seconds={args.seconds}"]
        with (folder / "record.log").open("w") as log:
            subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True,
                           timeout=max(600, args.seconds * 10) if args.seconds else 7200)
        report = json.loads((folder / "timeline.json").read_text())
        if report["status"] not in ("completed", "preview") or report["unmappedEvents"] or report["errors"]:
            raise RuntimeError(f"Recording did not validate: {folder / 'timeline.json'}")
        if not report["mappedEvents"]:
            raise RuntimeError(f"No script activity was recorded: {target}")
        movie = re.search(r"(\d+) frames at (\d+) FPS", (folder / "record.log").read_text())
        if movie is None or int(movie[2]) != args.fps or abs(int(movie[1]) - report["frames"]) > 4:
            raise RuntimeError(f"Movie frames differ from the script clock; keep the recording window visible and retry: {folder}")
        if ffmpeg:
            print(f"Encoding {target}: {report['mappedEvents']} synchronized events", flush=True)
            convert(ffmpeg, folder, report, args.keep_master)
        else:
            print("FFmpeg is unavailable; master.avi contains the video and audio. Install tools/recording-requirements.txt for MP4 output.", flush=True)
        print(f"Finished {target}: {report['frames'] / args.fps:.1f}s ({report['status']})", flush=True)
    print(f"Materials saved to {output}", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired, RuntimeError) as error:
        print(f"Recording failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
