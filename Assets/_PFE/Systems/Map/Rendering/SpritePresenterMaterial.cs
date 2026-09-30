using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The unlit sprite material every map presenter renders with.
    ///
    /// <para><b>Shared rather than duplicated.</b> This logic used to live only in
    /// <c>RoomObjectVisualManager</c>; the unit spawner needs the same shader, and two copies of a shader
    /// lookup is precisely how one of them ends up magenta the day the render pipeline changes. The
    /// lookup order is load-bearing: under URP the built-in <c>Sprites/Default</c> is not the correct
    /// shader, so the URP sprite shader must be tried first.</para>
    /// </summary>
    public static class SpritePresenterMaterial
    {
        static Material s_material;

        public static Material Get()
        {
            if (s_material != null)
            {
                return s_material;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            if (shader == null)
            {
                return null;
            }

            s_material = new Material(shader)
            {
                name = "MapPresenter_Unlit"
            };
            s_material.hideFlags = HideFlags.HideAndDontSave;
            return s_material;
        }
    }
}
