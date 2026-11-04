using PFE.Systems.Audio;

namespace PFE.Systems.Map
{
    /// <summary>
    /// AS3's tile material index → the port's <see cref="SurfaceMaterial"/>, as <b>one</b> table.
    ///
    /// <para><b>Why this type exists: the two numberings are not the same and one was cast onto the
    /// other.</b> AS3 <c>Tile.mat</c> is a 1..7 <i>debris</i> index (<c>Grafon.dyrka</c>'s chain, and the
    /// <c>mat</c> argument <c>Bullet.sound()</c> switches on). The port's
    /// <see cref="MaterialType"/> is 0..4 in a different order, and
    /// <c>TileDecoder</c> writes the AS3 index into it with a straight cast
    /// (<c>tile.material = (MaterialType)form.mat</c>) — so <c>TileData.material</c> holds the raw AS3
    /// index, <i>not</i> a <see cref="MaterialType"/>.</para>
    ///
    /// <para><b>The cast then went through <c>FromMaterialType</c>, which reads the value as a
    /// <see cref="MaterialType"/>, and the two disagree from 2 upward:</b></para>
    ///
    /// <list type="table">
    /// <item><description>AS3 <c>2</c> is <b>concrete</b>; <c>MaterialType.Wood</c> is 2 → the port played
    ///   <c>hit_wood</c>.</description></item>
    /// <item><description>AS3 <c>3</c> is <b>wood</b>; <c>MaterialType.Stone</c> is 3 → the port played
    ///   <c>hit_concrete</c>.</description></item>
    /// <item><description>AS3 <c>4</c> is <b>heavy concrete</b>; <c>MaterialType.Glass</c> is 4 → the port
    ///   played <c>hit_glass</c>.</description></item>
    /// <item><description>AS3 <c>5</c>, <c>6</c> and <c>7</c> have <b>no</b> <see cref="MaterialType"/>
    ///   value, so they fell to the <c>default</c> arm — concrete for 5/6, and 7 never reached it because
    ///   the cast produced an undefined enum value that <c>switch</c> sent to the same arm. Glass walls
    ///   and pole/pipe surfaces were therefore silent or wrong.</description></item>
    /// </list>
    ///
    /// <para><b>What the player heard.</b> A crowbar carries no <c>&lt;snd hit&gt;</c> (measured: 13 of
    /// the 14 <c>tip='1'</c> rows author none, and <c>pipe</c> is one of them), so the <i>material</i>
    /// layer is the only wall-impact sound it has. With this table wrong, a concrete wall clanged like
    /// timber and a wooden one thudded like stone — reported as "wrong wall hitting sound with
    /// melee".</para>
    ///
    /// <para><b>The mapping is <c>Bullet.sound()</c>'s own</b> (<c>weapon/Bullet.as:616-637</c>):
    /// <c>1 → hit_metal</c>, <c>2 / 4 / 6 → hit_concrete</c>, <c>3 → hit_wood</c>, <c>5 → hit_glass</c>,
    /// <c>7 → hit_pole</c>. It is transcribed from the sound switch rather than from the debris labels
    /// because the sound is what this table feeds; the two agree on every index that has a sound.</para>
    ///
    /// <para><b>Engine-free on purpose.</b> Plain <c>int</c> in, enum out, no <c>UnityEngine</c> — so the
    /// table can be executed by a fixture offline. <c>TileSurface</c> cannot be: it is a
    /// <c>MonoBehaviour</c>, and a rule that only a live editor can run is a rule nothing guards.</para>
    /// </summary>
    public static class TileMaterialRules
    {
        /// <summary>AS3 <c>mat</c> 1 — metal walls, robots, armoured props (<c>hit_metal</c>).</summary>
        public const int MetalMat = 1;

        /// <summary>AS3 <c>mat</c> 2 — the generic concrete wall (<c>hit_concrete</c>).</summary>
        public const int ConcreteMat = 2;

        /// <summary>AS3 <c>mat</c> 3 — wood (<c>hit_wood</c>).</summary>
        public const int WoodMat = 3;

        /// <summary>AS3 <c>mat</c> 4 — heavy concrete; same sound as 2, different debris.</summary>
        public const int HeavyConcreteMat = 4;

        /// <summary>AS3 <c>mat</c> 5 — glass (<c>hit_glass</c>).</summary>
        public const int GlassMat = 5;

        /// <summary>AS3 <c>mat</c> 6 — dirt / organic; same sound as 2, different debris.</summary>
        public const int DirtMat = 6;

        /// <summary>AS3 <c>mat</c> 7 — poles and pipes (<c>hit_pole</c>).</summary>
        public const int PoleMat = 7;

        /// <summary>
        /// The surface a tile's raw AS3 <c>mat</c> index sounds and reads as.
        ///
        /// <para><c>0</c> — and any index the oracle gives no sound — is
        /// <see cref="SurfaceMaterial.Default"/>, which the impact resolver treats as silent. That is
        /// the oracle's own answer: <c>Bullet.sound()</c> falls through every <c>if(param1 == …)</c> arm
        /// for a material it does not know.</para>
        /// </summary>
        /// <param name="as3Mat">AS3 <c>Tile.mat</c>, i.e. <c>(int)TileData.material</c> — see the type
        /// doc for why that cast is the raw index and not a <see cref="MaterialType"/>.</param>
        public static SurfaceMaterial SurfaceForAs3Material(int as3Mat)
        {
            switch (as3Mat)
            {
                case MetalMat:          return SurfaceMaterial.Metal;
                case ConcreteMat:
                case HeavyConcreteMat:
                case DirtMat:           return SurfaceMaterial.Concrete;
                case WoodMat:           return SurfaceMaterial.Wood;
                case GlassMat:          return SurfaceMaterial.Glass;
                case PoleMat:           return SurfaceMaterial.Pole;
                default:                return SurfaceMaterial.Default;
            }
        }
    }
}
