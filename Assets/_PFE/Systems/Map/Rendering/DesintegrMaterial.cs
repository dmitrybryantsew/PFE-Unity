using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The material a dissolving corpse draws with — <c>PFE/UnitDissolve</c>, and the one place that knows
    /// how to turn the oracle's glow radius into a UV offset.
    ///
    /// <para><b>Why a per-renderer <see cref="Material"/> and not a <see cref="MaterialPropertyBlock"/>.</b>
    /// A <c>SpriteRenderer</c> binds its sprite's texture through the renderer's own property block, and
    /// <c>SetPropertyBlock</c> <i>replaces</i> that block rather than merging into it — so the obvious
    /// design (one shared material, per-renderer <c>_Dissolve</c>) loses <c>_MainTex</c> and draws a white
    /// quad. Copying the sprite texture into the block too would work until the animator changed frames,
    /// which a dissolving body still does. A per-instance material costs one allocation per dissolving
    /// corpse, and a room holds a handful, so the simple thing is also the cheap thing.</para>
    ///
    /// <para><b>Nothing here is cached on the renderer.</b> The material is created when the dissolve
    /// starts and destroyed when it ends (<c>UnitController.ExterminateCorpse</c>), because a unit can be
    /// revived (<c>ReviveFromCorpse</c>) and would otherwise come back still wearing it.</para>
    /// </summary>
    public static class DesintegrMaterial
    {
        /// <summary>The shader's name — <c>PFE_UnitDissolve.shader</c>'s own declaration.</summary>
        public const string ShaderName = "PFE/UnitDissolve";

        /// <summary>
        /// The dissolve shader, or <c>null</c> when it is absent — which is a real possibility rather
        /// than a hypothetical: a shader that no material asset references is stripped from a player
        /// build unless it lives under a <c>Resources</c> folder, and this one does for exactly that
        /// reason. <c>null</c> is handled by the caller, which falls back to the alpha fade.
        /// </summary>
        public static Shader GetShader() => Shader.Find(ShaderName);

        /// <summary>
        /// A fresh material for one dissolving body, or <c>null</c> when the shader is unavailable.
        /// </summary>
        /// <remarks>
        /// The caller owns the result and must <c>Destroy</c> it. Deliberately not pooled: a pool would
        /// have to be cleared on room change, and the whole population is "bodies currently dissolving",
        /// which is bounded by the number of units that died in the last 1.3 seconds.
        /// </remarks>
        public static Material Create()
        {
            Shader shader = GetShader();
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader)
            {
                name = "UnitDissolve"
            };
            material.hideFlags = HideFlags.HideAndDontSave;
            return material;
        }

        /// <summary>
        /// The <c>_GlowRadiusUV</c> for a sprite of the given size — <see cref="PFE.Systems.Combat.DesintegrRules.GlowBlurPixels"/>
        /// converted from pixels to the 0..1 UV space the shader samples in.
        /// </summary>
        /// <param name="spriteSizePixels">The unit's authored <c>scX</c>/<c>scY</c>.</param>
        /// <remarks>
        /// <para><b>Divided by the larger side, not by each side.</b> The shader's taps are
        /// <c>float2(r, r)</c> in UV, so a single radius has to mean the same physical distance in both
        /// axes; dividing by the larger dimension keeps the halo from becoming an ellipse on a tall
        /// sprite.</para>
        ///
        /// <para><b>Refuses a degenerate size.</b> A unit with no definition answers
        /// <c>Vector2.zero</c>, and dividing by it would hand the shader an infinite radius — which does
        /// not throw, it just samples one point forever and draws a flat halo. The clamp keeps the
        /// failure visible as "no glow" instead.</para>
        /// </remarks>
        public static float GlowRadiusUv(Vector2 spriteSizePixels)
        {
            float longest = Mathf.Max(spriteSizePixels.x, spriteSizePixels.y);
            if (longest <= 1f)
            {
                return 0f;
            }

            return PFE.Systems.Combat.DesintegrRules.GlowBlurPixels / longest;
        }
    }
}
