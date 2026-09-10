#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;
using Majo.Runtime.Actors.Layered;

namespace Majo.Runtime.Actors.Godot;

/// <summary>
/// Replays the imported stencil writers in layer order in each masked sprite's
/// shader. Sampling the resident atlases avoids screen/backbuffer feedback inside
/// CanvasGroup and keeps masks local to the actor and to each transition endpoint.
/// </summary>
internal sealed class LayeredCharacterMasking
{
    private readonly Dictionary<string, Mask> masks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Shader> shaders = new(StringComparer.Ordinal);

    public LayeredCharacterMasking(LayeredCharacterManifest manifest)
    {
        foreach (var layer in manifest.Layers)
        {
            masks.Add(layer.Path.Replace('/', '_'), Parse(layer.Material));
        }
    }

    public void Apply(CanvasGroup composite)
    {
        var writers = new List<(Sprite2D Sprite, Mask Mask)>();
        foreach (var sprite in composite.GetChildren().OfType<Sprite2D>())
        {
            if (!sprite.Visible || !masks.TryGetValue(sprite.Name, out var mask)) continue;
            if (mask.IsReader)
            {
                // Never mutate shared materials: Duplicate() shares resources with
                // the live actor, but source and target captures need different masks.
                sprite.Material = CreateMaterial(sprite, mask, writers);
            }
            else if (mask.Reference > 0)
            {
                writers.Add((sprite, mask));
            }
        }
    }

    private ShaderMaterial CreateMaterial(Sprite2D target, Mask mask,
        List<(Sprite2D Sprite, Mask Mask)> writers)
    {
        var atlases = new List<Texture2D>();
        var atlasIndices = new List<int>();
        foreach (var (sprite, _) in writers)
        {
            var atlas = ((AtlasTexture)sprite.Texture).Atlas;
            var index = atlases.IndexOf(atlas);
            if (index < 0)
            {
                index = atlases.Count;
                atlases.Add(atlas);
            }
            atlasIndices.Add(index);
        }

        var key = $"{mask.Multiply}:{string.Join(',', atlasIndices)}";
        if (!shaders.TryGetValue(key, out var shader))
        {
            shader = new Shader { Code = BuildShader(mask.Multiply, atlasIndices, atlases.Count) };
            shaders.Add(key, shader);
        }
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("mask_reference", mask.Reference);
        for (var index = 0; index < atlases.Count; index++)
            material.SetShaderParameter($"mask_atlas_{index}", atlases[index]);

        for (var index = 0; index < writers.Count; index++)
        {
            var (sprite, writer) = writers[index];
            var texture = (AtlasTexture)sprite.Texture;
            var rect = sprite.GetRect();
            var transform = sprite.Transform.AffineInverse() * target.Transform;
            material.SetShaderParameter($"mask_x_{index}", new Vector3(
                transform.X.X / rect.Size.X, transform.Y.X / rect.Size.X,
                (transform.Origin.X - rect.Position.X) / rect.Size.X));
            material.SetShaderParameter($"mask_y_{index}", new Vector3(
                transform.X.Y / rect.Size.Y, transform.Y.Y / rect.Size.Y,
                (transform.Origin.Y - rect.Position.Y) / rect.Size.Y));
            var atlasSize = texture.Atlas.GetSize();
            material.SetShaderParameter($"mask_region_{index}", new Vector4(
                texture.Region.Position.X / atlasSize.X, texture.Region.Position.Y / atlasSize.Y,
                texture.Region.Size.X / atlasSize.X, texture.Region.Size.Y / atlasSize.Y));
            material.SetShaderParameter($"mask_info_{index}", new Vector3(
                writer.Reference, writer.Cutoff, sprite.Modulate.A * sprite.SelfModulate.A));
        }
        return material;
    }

    private static string BuildShader(bool multiply, List<int> atlasIndices, int atlasCount)
    {
        var code = new StringBuilder("shader_type canvas_item;\nrender_mode unshaded");
        if (multiply) code.Append(", blend_mul");
        code.Append(";\nuniform int mask_reference;\nvarying vec2 mask_position;\n");
        for (var i = 0; i < atlasCount; i++)
            code.Append(CultureInfo.InvariantCulture,
                $"uniform sampler2D mask_atlas_{i} : repeat_disable, filter_linear;\n");
        for (var i = 0; i < atlasIndices.Count; i++)
            code.Append(CultureInfo.InvariantCulture, $"""
                uniform vec3 mask_x_{i};
                uniform vec3 mask_y_{i};
                uniform vec4 mask_region_{i};
                uniform vec3 mask_info_{i};

                """);
        code.Append("""
            void vertex() { mask_position = VERTEX; }
            bool writes_mask(sampler2D atlas, vec2 uv, vec4 region, vec3 info) {
                if (any(lessThan(uv, vec2(0.0))) || any(greaterThanEqual(uv, vec2(1.0)))) {
                    return false;
                }
                vec2 half_texel = 0.5 / vec2(textureSize(atlas, 0));
                vec2 atlas_uv = clamp(region.xy + uv * region.zw,
                    region.xy + half_texel, region.xy + region.zw - half_texel);
                return texture(atlas, atlas_uv).a * info.z >= info.y;
            }
            void fragment() {
                float stencil = 0.0;
                vec3 position = vec3(mask_position, 1.0);

            """);
        for (var i = 0; i < atlasIndices.Count; i++)
            code.Append(CultureInfo.InvariantCulture, $$"""
                if (writes_mask(mask_atlas_{{atlasIndices[i]}},
                    vec2(dot(mask_x_{{i}}, position), dot(mask_y_{{i}}, position)),
                    mask_region_{{i}}, mask_info_{{i}})) { stencil = mask_info_{{i}}.x; }

                """);
        // Imported Masked materials use Unity LEqual (reference <= stencil),
        // so Ref1 includes the Ref2 eye region, while Ref2 excludes the body.
        code.Append("if (stencil < float(mask_reference)) { discard; }\n");
        if (multiply)
            code.Append("vec4 source = COLOR; COLOR = vec4(mix(vec3(1.0), source.rgb, source.a), 1.0);\n");
        code.Append("}\n");
        return code.ToString();
    }

    private static Mask Parse(string material)
    {
        var suffix = material.Split('#');
        var multiply = material.Contains("Multiply", StringComparison.OrdinalIgnoreCase);
        if (suffix.Length != 2) return new Mask(0, false, 0, multiply);
        var fields = suffix[1].Split('_');
        if (fields.Length < 2 || fields[0] is not ("Mask" or "Masked") ||
            !fields[1].StartsWith("Ref", StringComparison.Ordinal) ||
            !int.TryParse(fields[1].AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var reference))
            return new Mask(0, false, 0, multiply);

        // Values verified against the source game's exported material properties.
        var cutoff = fields.Contains("CutoffMin") ? .05f : .2f;
        return new Mask(reference, fields[0] == "Masked", cutoff, multiply);
    }

    private readonly record struct Mask(int Reference, bool IsReader, float Cutoff, bool Multiply);
}
