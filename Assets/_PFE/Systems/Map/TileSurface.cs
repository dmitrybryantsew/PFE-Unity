using UnityEngine;
using PFE.Systems.Audio;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Lightweight tag component stamped onto every tile collider GameObject by
    /// TileCollider.Initialize(), converting TileData.MaterialType to SurfaceMaterial
    /// so the impact sound resolver can query it with a single GetComponent call.
    ///
    /// Scene authors can also add this manually to props/boxes to override the surface sound.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TileSurface : MonoBehaviour, IHasSurfaceMaterial
    {
        [SerializeField] private SurfaceMaterial _material = SurfaceMaterial.Concrete;

        public SurfaceMaterial SurfaceMaterial => _material;

        /// <summary>Set by TileCollider during tile generation.</summary>
        public void Set(SurfaceMaterial mat) => _material = mat;

        // ── Mapping ───────────────────────────────────────────────────────────

        /// <summary>Convert the tile system's MaterialType to a SurfaceMaterial.</summary>
        public static SurfaceMaterial FromMaterialType(MaterialType mat)
        {
            switch (mat)
            {
                case MaterialType.Metal:  return SurfaceMaterial.Metal;
                case MaterialType.Wood:   return SurfaceMaterial.Wood;
                case MaterialType.Glass:  return SurfaceMaterial.Glass;
                case MaterialType.Stone:  return SurfaceMaterial.Concrete;
                default:                  return SurfaceMaterial.Concrete; // most walls are concrete
            }
        }
    }
}
