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

        /// <summary>
        /// The surface a tile sounds as, from the tile's <b>raw AS3 <c>mat</c> index</b>.
        ///
        /// <para><b>The argument is <c>(int)TileData.material</c>, not a <see cref="MaterialType"/>.</b>
        /// <c>TileDecoder</c> writes AS3's <c>mat</c> into that field with a straight cast, and the two
        /// numberings disagree from 2 upward — see <see cref="TileMaterialRules"/> for the full table
        /// and for what the old conversion cost. The rule itself lives there so a fixture can run it;
        /// this is the component-side wrapper.</para>
        /// </summary>
        public static SurfaceMaterial FromAs3Material(int as3Mat)
            => TileMaterialRules.SurfaceForAs3Material(as3Mat);
    }
}
