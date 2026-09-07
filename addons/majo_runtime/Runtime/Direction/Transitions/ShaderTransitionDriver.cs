#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Transitions;

public sealed class ShaderTransitionDriver : ITransitionDriver
{
    private readonly ShaderMaterial materialTemplate;
    private readonly Texture2D? ruleTexture;
    private readonly float smoothness;
    private readonly bool invert;

    public ShaderTransitionDriver(
        DirectionTransitionId id,
        ShaderMaterial materialTemplate,
        Texture2D? ruleTexture = null,
        float smoothness = 0.1f,
        bool invert = false)
    {
        Id = id;
        this.materialTemplate = materialTemplate ?? throw new ArgumentNullException(nameof(materialTemplate));
        this.ruleTexture = ruleTexture;
        this.smoothness = smoothness;
        this.invert = invert;
    }

    public ShaderTransitionDriver(
        DirectionTransitionId id,
        Shader shader,
        Texture2D? ruleTexture = null,
        float smoothness = 0.1f,
        bool invert = false)
        : this(
            id,
            new ShaderMaterial { Shader = shader ?? throw new ArgumentNullException(nameof(shader)) },
            ruleTexture,
            smoothness,
            invert)
    {
    }

    public DirectionTransitionId Id { get; }

    public bool SupportsCustomEasing => true;

    public void Setup(VisualTransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Spec.DurationSeconds <= 0)
        {
            return;
        }

        var material = (ShaderMaterial)materialTemplate.Duplicate();
        material.SetShaderParameter("progress", 0.0f);
        if (ruleTexture is not null)
        {
            material.SetShaderParameter("rule_texture", ruleTexture);
        }
        material.SetShaderParameter("smoothness", smoothness);
        material.SetShaderParameter("invert", invert);

        context.Surface.Material = material;

        context.AnimateProgress(progress =>
            {
                if (context.IsActive?.Invoke() != false && GodotObject.IsInstanceValid(context.Surface))
                {
                    material.SetShaderParameter("progress", progress);
                }
            });

        context.Tween.Finished += () =>
        {
            if (GodotObject.IsInstanceValid(context.Surface) &&
                ReferenceEquals(context.Surface.Material, material))
            {
                context.Surface.Material = null;
            }
        };
    }
}
