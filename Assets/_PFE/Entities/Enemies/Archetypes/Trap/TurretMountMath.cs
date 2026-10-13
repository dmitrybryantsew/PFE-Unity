using System;
using System.Globalization;
using PFE.Entities.Enemies;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The pure arithmetic of the AS3 turret mounts — <c>fe/unit/UnitTurret.as</c> — with the two
    /// things the port cannot express as a component field pulled out into plain functions so they can
    /// be asserted offline.
    ///
    /// <para><b>Why a static holder and not methods on the controller/brain.</b> Every one of these
    /// answers is a function of data (a definition id, a placement attribute, a difficulty level and a
    /// random draw). Written inline on a MonoBehaviour they would be reachable only through a scene,
    /// and a test whose arrange needs a GameObject cannot run in the project's offline wall — which
    /// is how "the code looks right" gets to ship.</para>
    /// </summary>
    public static class TurretMountMath
    {
        /// <summary>The prefix <c>UnitTurret.as:94</c> stamps every mount id with.</summary>
        public const string IdPrefix = "turret";

        /// <summary>
        /// AS3 <c>UnitTurret.turrettip</c> from the definition id — <c>UnitTurret.as:94</c> composes
        /// <c>id = "turret" + this.turrettip</c>, so the id carries the mount: 0 floor, 1 land, 2 wall,
        /// 3 arm, 4 combat, 5 boss. <c>hturret</c>/<c>hturret2</c> keep tip 0 (they set
        /// <c>this.hidden</c>, :78-85).
        ///
        /// <para>Returns -1 for an id that is not a turret row, so a caller never mistakes a
        /// non-turret for the floor mount.</para>
        /// </summary>
        public static int TipOfDefinitionId(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return -1;
            string id = unitId.ToLowerInvariant();
            if (!id.StartsWith(IdPrefix, StringComparison.Ordinal)) return -1;

            string digits = id.Substring(IdPrefix.Length);
            if (digits.Length == 0) return 0; // the family template `turret` is the floor mount

            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tip)
                ? tip
                : -1;
        }

        /// <summary>
        /// The half-plane a placement's <c>@vis</c> aims the mount at — <c>UnitTurret.putLoc</c>'s
        /// <c>aRot</c> tables (:201-231):
        /// <c>right</c>→<c>[45,-45]</c>, <c>left</c>→<c>[135,-135]</c>, <c>up</c>→<c>[-45,-135]</c>,
        /// <c>down</c>→<c>[45,135]</c>, each swept against <c>vKonus = Math.PI / 2</c> (:204), so the
        /// sweep covers the whole half-plane named.
        ///
        /// <para>Returns <see cref="EnemySensors.VisionCone.Horizontal"/> for an absent value — the
        /// mount's default table is the four diagonals, whose aggregate has no single horizontal side,
        /// so the facing the placement rolled is kept — and <c>null</c> for a value the oracle's four
        /// <c>if</c>s do not name, so the caller can report it rather than treat it as absent.</para>
        /// </summary>
        public static EnemySensors.VisionCone? ConeForVis(string vis)
        {
            switch (vis)
            {
                case "right":
                case "left":
                    return EnemySensors.VisionCone.Horizontal;
                case "up":
                    return EnemySensors.VisionCone.Up;
                case "down":
                    return EnemySensors.VisionCone.Down;
                case null:
                case "":
                    return EnemySensors.VisionCone.Horizontal;
                default:
                    return null;
            }
        }

        /// <summary>
        /// The horizontal side a placement's <c>@vis</c> turns the mount to. Only the two horizontal
        /// tables have a side: <c>right</c> is <c>[45,-45]</c> and <c>left</c> is <c>[135,-135]</c>.
        /// <c>up</c>/<c>down</c> are vertical half-planes with no side, so the facing the placement
        /// already resolved is returned unchanged.
        /// </summary>
        public static int FacingForVis(string vis, int currentFacing)
        {
            switch (vis)
            {
                case "right": return 1;
                case "left": return -1;
                default: return currentFacing;
            }
        }

        /// <summary>
        /// AS3 <c>UnitTurret.as:46-65</c> — the <c>tr</c> that names the weapon row:
        /// <code>
        /// if(param4 &amp;&amp; param4.tr) tr = param4.tr;                    // the saved object — not modelled
        /// else if(param3 &amp;&amp; param3.@tr.length()) tr = param3.@tr;    // the placement node
        /// else if(param2 &lt; 10) tr = floor(random*3+1);                   // locDifLevel bands
        /// else if(param2 &lt; 15) tr = floor(random*5+1);
        /// else tr = floor(random*7+1);
        /// </code>
        /// <para><b>The band roll takes the caller's draw.</b> AS3's <c>Math.random()</c> is Flash's
        /// unseeded global; the port's equivalent is <c>UnityEngine.Random</c>, and passing it in as a
        /// function keeps the band assertable without a scene.</para>
        /// </summary>
        /// <param name="authoredTr">The placement node's <c>tr</c> attribute, or null/empty.</param>
        /// <param name="locationDifficulty">AS3 <c>locDifLevel</c>.</param>
        /// <param name="roll01">A draw in [0,1). Used only when no <c>tr</c> is authored.</param>
        public static int Tr(string authoredTr, float locationDifficulty, Func<float> roll01)
        {
            if (!string.IsNullOrEmpty(authoredTr) &&
                int.TryParse(authoredTr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int authored))
            {
                return authored;
            }

            int difficulty = (int)locationDifficulty;
            int span = difficulty < 10 ? 3 : (difficulty < 15 ? 5 : 7);
            return (int)(roll01() * span) + 1;
        }

        /// <summary>
        /// The weapon id a mount of this <c>tr</c> holds — <c>UnitTurret.as:126-127</c>'s
        /// <c>new Weapon(this, "turret" + this.tr)</c>. Note the span of the shipped table: 1-7 from the
        /// difficulty bands, plus <c>turret10</c>, which is what the three <c>bossturret</c> placements
        /// author.
        /// </summary>
        public static string WeaponId(int tr)
        {
            return IdPrefix + tr.ToString(CultureInfo.InvariantCulture);
        }
    }
}
