#!/usr/bin/env python3
"""Rebuild Manosaba layered-character atlases as Cubism-ready PSDs."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageChops, ImageCms, ImageOps
from psd_tools import PSDImage
from psd_tools.constants import BlendMode, Resource
from psd_tools.psd.image_resources import ImageResource


ROOT = Path(__file__).resolve().parents[1]
CHARACTERS = ROOT / "demos/manosaba/assets/characters"
DEFAULT_OUTPUT = ROOT / "artifacts/live2d-psd"
BLENDS = {
    "Naninovel_Default": BlendMode.NORMAL,
    "Naninovel_Multiply": BlendMode.MULTIPLY,
    "Naninovel_Overlay": BlendMode.OVERLAY,
    "Naninovel_Softlight": BlendMode.SOFT_LIGHT,
}


def child_paths(path: str, paths: set[str]) -> set[str]:
    return {item for item in paths if item.startswith(path + "/")}


def default_visible(manifest: dict) -> set[str]:
    """Match LayerCompositionResolver.CreateDefaultState for manifest aliases."""
    layers = manifest["layers"]
    paths = {layer["path"] for layer in layers}
    groups = {layer["path"].rsplit("/", 1)[0] for layer in layers if "/" in layer["path"]}
    groups |= {"/".join(path.split("/")[:i]) for path in paths for i in range(1, path.count("/") + 1)}
    state = {layer["path"] for layer in layers if layer["enabled"]}
    aliases = manifest["compositionMap"]

    def members(node: str) -> set[str]:
        if node in paths:
            return {node}
        if node in groups:
            return child_paths(node, paths)
        raise ValueError(f"Unknown layer/group in appearance: {node}")

    def apply(composition: str, stack: tuple[str, ...] = ()) -> None:
        if len(stack) > 64:
            raise ValueError("Appearance alias nesting is too deep")
        for raw in composition.split(","):
            expr = raw.strip()
            if not expr:
                continue
            if expr in aliases:
                if expr in stack:
                    raise ValueError(f"Appearance alias cycle: {expr}")
                apply(aliases[expr], stack + (expr,))
                continue
            if expr == "+":
                state.update(paths)
                continue
            if expr == "-":
                state.clear()
                continue
            if expr[0] in "+-":
                group, op, member = "", expr[0], expr[1:]
            elif expr[-1] in "+->~":
                group, op, member = expr[:-1], expr[-1], ""
            else:
                parsed = next(
                    ((expr[:i], char, expr[i + 1:]) for i, char in enumerate(expr)
                     if char in "+->~" and expr[:i] in groups
                     and f"{expr[:i]}/{expr[i + 1:]}" in paths | groups),
                    None,
                )
                if parsed is None:
                    raise ValueError(f"Invalid appearance expression: {expr}")
                group, op, member = parsed
            node = f"{group}/{member}" if group and member else group or member
            op = ">" if op == "~" else op
            if op == "+":
                state.update(members(node))
            elif op == "-":
                state.difference_update(members(node))
            elif member:
                state.difference_update(members(group))
                state.update(members(node))
            else:
                if group not in groups:
                    raise ValueError(f"Unknown appearance group: {group}")
                parent = group.rsplit("/", 1)[0] if "/" in group else ""
                state.difference_update(paths if not parent else members(parent))
                state.update(members(group))

    apply(manifest["defaultAppearance"])
    return state


def render_layer(layer: dict, atlas: Image.Image, canvas: dict, size: tuple[int, int]):
    region = layer["region"]
    x, y, width, height = (region[key] for key in ("x", "y", "width", "height"))
    if width <= 0 or height <= 0 or x < 0 or y < 0 or x + width > atlas.width or y + height > atlas.height:
        raise ValueError(f"Atlas region outside image: {layer['path']}")
    if layer["rotationDegrees"]:
        raise ValueError(f"Rotated layer is unsupported: {layer['path']}")
    scale_x, scale_y = layer["scale"]["x"], layer["scale"]["y"]
    if scale_x <= 0 or scale_y <= 0:
        raise ValueError(f"Non-positive scale: {layer['path']}")
    image = atlas.crop((x, y, x + width, y + height)).convert("RGBA")
    if layer["flipX"]:
        image = ImageOps.mirror(image)
    if layer["flipY"]:
        image = ImageOps.flip(image)
    target_width = max(1, round(width * scale_x))
    target_height = max(1, round(height * scale_y))
    if image.size != (target_width, target_height):
        image = image.resize((target_width, target_height), Image.Resampling.LANCZOS)
    color = layer["color"]
    if any(color[channel] != 1 for channel in "rgb"):
        r, g, b, a = image.split()
        image = Image.merge("RGBA", tuple(channel.point(lambda value, factor=color[key]: round(value * factor))
                                          for channel, key in zip((r, g, b), "rgb")) + (a,))
    ppu = canvas["pixelsPerUnit"]
    left = round(size[0] / 2 + (layer["position"]["x"] - canvas["offsetX"]) * ppu
                 - layer["pivot"]["x"] * target_width)
    top = round(size[1] / 2 - (layer["position"]["y"] - canvas["offsetY"]) * ppu
                - (1 - layer["pivot"]["y"]) * target_height)
    # PSD layers may extend beyond the canvas, but cropping prevents large invisible data.
    clip = (max(0, left), max(0, top), min(size[0], left + target_width), min(size[1], top + target_height))
    if clip[0] >= clip[2] or clip[1] >= clip[3]:
        raise ValueError(f"Layer lies wholly outside canvas: {layer['path']}")
    if clip != (left, top, left + target_width, top + target_height):
        image = image.crop((clip[0] - left, clip[1] - top, clip[2] - left, clip[3] - top))
    return image, clip[0], clip[1]


def bake_stencil(image: Image.Image, left: int, top: int, reader: dict,
                 writers: list[dict], atlases: dict[str, Image.Image],
                 canvas: dict, size: tuple[int, int]) -> Image.Image:
    """Apply Unity's last-writer-wins stencil to a reader's alpha pixels."""
    reference = int(reader["material"].split("#Masked_Ref", 1)[1])
    stencil = Image.new("L", image.size, 0)
    bounds = (left, top, left + image.width, top + image.height)
    for writer in writers:
        if writer["sortingOrder"] >= reader["sortingOrder"]:
            break
        suffix = writer["material"].split("#Mask_Ref", 1)[1]
        writer_ref = int(suffix.split("_", 1)[0])
        cutoff = 0.05 if "CutoffMin" in suffix else 0.2
        source, wx, wy = render_layer(writer, atlases[writer["atlas"]], canvas, size)
        overlap = (max(left, wx), max(top, wy),
                   min(bounds[2], wx + source.width), min(bounds[3], wy + source.height))
        if overlap[0] < overlap[2] and overlap[1] < overlap[3]:
            alpha = source.getchannel("A").crop((overlap[0] - wx, overlap[1] - wy,
                                                   overlap[2] - wx, overlap[3] - wy))
            opacity = writer["color"]["a"]
            hit = alpha.point(lambda value: 255 if value / 255 * opacity >= cutoff else 0)
            stencil.paste(writer_ref, (overlap[0] - left, overlap[1] - top), hit)
        source.close()
    keep = stencil.point(lambda value: 255 if value >= reference else 0)
    image.putalpha(ImageChops.multiply(image.getchannel("A"), keep))
    return image


def export_character(directory: Path, output: Path) -> tuple[int, int]:
    manifest = json.loads((directory / "layered_character.json").read_text(encoding="utf-8"))
    if manifest["schemaVersion"] != 1:
        raise ValueError(f"Unsupported schema version: {manifest['schemaVersion']}")
    canvas = manifest["canvas"]
    size = (round(canvas["width"] * canvas["pixelsPerUnit"]),
            round(canvas["height"] * canvas["pixelsPerUnit"]))
    if min(size) <= 0 or max(size) > 30000:
        raise ValueError(f"Invalid PSD canvas: {size}")
    paths = [layer["path"] for layer in manifest["layers"]]
    if len(set(paths)) != len(paths):
        raise ValueError("Duplicate layer paths")
    visible = default_visible(manifest)
    active_writers = sorted((layer for layer in manifest["layers"]
                             if layer["path"] in visible and "#Mask_Ref" in layer["material"]),
                            key=lambda layer: (layer["sortingOrder"], layer["path"]))
    # Cubism imports hidden layers too. Build every variant and show only the default pose.
    # Godot draws later children above earlier ones; psd-tools also places newly
    # appended layers above existing siblings. Keep SortingOrder ascending.
    ordered = sorted(manifest["layers"], key=lambda layer: (layer["sortingOrder"], layer["path"]))
    psd = PSDImage.new("RGBA", size, depth=8)
    psd.background_color = None  # Transparent merged preview, not an opaque black backdrop.
    profile = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()
    psd.image_resources[Resource.ICC_PROFILE] = ImageResource(key=Resource.ICC_PROFILE, data=profile)
    atlases: dict[str, Image.Image] = {}
    groups: dict[tuple[str, ...], object] = {(): psd}
    used_names: set[str] = set()
    try:
        for layer in ordered:
            parts = layer["path"].split("/")
            for depth in range(1, len(parts)):
                key = tuple(parts[:depth])
                if key not in groups:
                    parent = groups[key[:-1]]
                    folder = psd.create_group(name=key[-1])
                    parent.append(folder)
                    groups[key] = folder
            name = parts[-1]
            # Live2D recommends globally unique names, including across folders.
            if name in used_names:
                name = layer["path"].replace("/", "__")
            if name in used_names:
                raise ValueError(f"Duplicate PSD layer name: {name}")
            used_names.add(name)
            atlas_name = layer["atlas"]
            if Path(atlas_name).name != atlas_name:
                raise ValueError(f"Unsafe atlas path: {atlas_name}")
            if atlas_name not in atlases:
                atlases[atlas_name] = Image.open(directory / atlas_name)
            image, left, top = render_layer(layer, atlases[atlas_name], canvas, size)
            if "#Masked_Ref" in layer["material"]:
                for writer in active_writers:
                    if writer["atlas"] not in atlases:
                        atlases[writer["atlas"]] = Image.open(directory / writer["atlas"])
                image = bake_stencil(image, left, top, layer, active_writers,
                                     atlases, canvas, size)
            base_material = layer["material"].split("#", 1)[0]
            if base_material not in BLENDS:
                raise ValueError(f"Unsupported material: {layer['material']}")
            pixel = psd.create_pixel_layer(image, name=name, left=left, top=top,
                                           opacity=round(layer["color"]["a"] * 255),
                                           blend_mode=BLENDS[base_material])
            pixel.visible = layer["path"] in visible
            groups[tuple(parts[:-1])].append(pixel)
            image.close()
        output.parent.mkdir(parents=True, exist_ok=True)
        psd.save(output)
    finally:
        for atlas in atlases.values():
            atlas.close()
    return len(ordered), len(visible)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("characters", nargs="*", help="Character directory names (default: all)")
    parser.add_argument("--input-dir", type=Path, default=CHARACTERS)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    names = args.characters or sorted(p.name for p in args.input_dir.iterdir()
                                      if (p / "layered_character.json").is_file())
    if not names:
        parser.error("No layered characters found")
    for name in names:
        if name in (".", "..") or Path(name).name != name:
            parser.error(f"Invalid character name: {name}")
        directory = args.input_dir / name
        if not (directory / "layered_character.json").is_file():
            parser.error(f"Missing character manifest: {directory}")
        output = args.output_dir / f"{name}.psd"
        count, shown = export_character(directory, output)
        print(f"{name}: {output} ({count} layers, {shown} visible, 8-bit RGB/sRGB)", flush=True)


if __name__ == "__main__":
    main()
