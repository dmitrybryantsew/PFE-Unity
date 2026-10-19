using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The three pieces a turret mount is actually drawn from, and the arithmetic that places them.
    ///
    /// <para><b>Why three pieces.</b> <c>UnitTurret</c>'s art is a MovieClip tree, not a bitmap
    /// (<c>UnitTurret.as:97-119</c>):</para>
    ///
    /// <code>
    /// vis                 visualTurretN      the mount plate and neck        (body)
    ///   vis.osn.puha      turel_puha_1262     10 frames, one per `tr`        (gun)
    ///   vis.osn.light     turret_light_1277   6 frames, one per `aiState+1`  (lamp)
    /// </code>
    ///
    /// <para><c>animate()</c> then makes the gun a <b>turret</b> rather than a decoration:
    /// <c>vis.osn.puha.rotation = currentWeapon.rot * 180 / Math.PI</c> (<c>:309</c>) and
    /// <c>vis.osn.puha.gotoAndStop(this.tr)</c> (<c>:298</c>), with the lamp stepping to
    /// <c>aiState + 1</c> (<c>:310-313</c>). <c>UnitSpriteImporter</c> copies the <i>outer</i> symbol's
    /// frames and that symbol has exactly one, so the port shipped a single baked composite per mount:
    /// the barrel could not turn, the barrel art could not follow <c>tr</c>, and the lamp was never
    /// drawn. These are the numbers that let a presenter draw them separately.</para>
    ///
    /// <para><b>Where every number comes from.</b> The gun and lamp placements are the
    /// <c>PlaceObject2</c> matrix translations of the mount's <c>osn</c> symbol, read out of
    /// <c>assets.swf</c> by <c>docs/Diagnostics/scripts/turret_swf_probe.py</c> — the same instrument that
    /// produces <see cref="TurretSpriteOrigin"/>, so the two cannot disagree about where the mount's
    /// origin is. The pivots are the two child symbols' own <c>DefineShape</c> bounds. Nothing here is
    /// eyeballed and nothing is a re-typed constant: re-running the probe reprints both tables.</para>
    ///
    /// <para><b>Why the arithmetic is a static class.</b> A <see cref="Sprite"/> cannot be constructed
    /// offline and a <c>MonoBehaviour</c> cannot be added to a GameObject outside play mode, so a rule
    /// written inline in <see cref="TurretPartPresenter"/> could only be checked by looking at it. Every
    /// function here takes and returns plain numbers.</para>
    /// </summary>
    public static class TurretParts
    {
        /// <summary>Frames in <c>turel_puha_1262</c>. <c>tr</c> selects one; the shipped table reaches 10.</summary>
        public const int GunFrameCount = 10;

        /// <summary>Frames in <c>turret_light_1277</c>. <c>aiState + 1</c> selects one.</summary>
        public const int LampFrameCount = 6;

        /// <summary>
        /// The gun's registration point inside its own exported frame, in <b>frame pixels from the
        /// frame's top-left corner</b>. This is the point AS3 rotates: <c>puha.rotation</c> turns the
        /// symbol about its own origin, so the barrel sweeps around the trunnion and the muzzle travels.
        /// The gun frame is 96x45, so this sits 15.1 px in from the left and 21.9 px up from the bottom —
        /// not the centre, which is why a centre pivot would swing the barrel out of its mount.
        /// </summary>
        public static readonly Vector2 GunPivotPx = new Vector2(15.1f, 23.12f);

        /// <summary>
        /// The lamp's registration point in its own frame, in frame pixels from the top-left. The lamp
        /// frame is 29x29 and the symbol is centred on itself, so this is the centre — recorded rather
        /// than assumed, because "it is centred" is the kind of claim that stops being true.
        /// </summary>
        public static readonly Vector2 LampPivotPx = new Vector2(14.5f, 14.5f);

        /// <summary>
        /// Per mount, the <b>Flash-space</b> offset of each part from the mount's origin, in pixels with
        /// <b>y pointing down</b> — the oracle's own convention, kept rather than pre-flipped so the
        /// table can be compared against the SWF dump line for line.
        ///
        /// <para>The gun column is also the oracle's <c>setWeaponPos</c> constant negated
        /// (<c>UnitTurret.as:422-441</c>): tip 0/4 → 13.8, tip 1 → 60, tip 2/5 → 20, tip 3 → 55, each
        /// within the 1.8 px muzzle offset. Two independent sources for the same number.</para>
        /// </summary>
        static readonly (string UnitId, Vector2 Gun, Vector2 Lamp)[] Placements =
        {
            // unit id   gun (flash px, y down)   lamp
            ("turret0", new Vector2(-0.35f, -13.80f), new Vector2(0.00f, -16.50f)),
            ("turret1", new Vector2(-0.35f, -60.00f), new Vector2(-0.35f, -60.00f)),
            ("turret2", new Vector2(0.00f, -20.00f), new Vector2(0.00f, -19.75f)),
            ("turret3", new Vector2(0.00f, -55.00f), new Vector2(0.00f, -55.00f)),
            ("turret4", new Vector2(-0.35f, -13.80f), new Vector2(0.00f, -16.50f)),
            ("turret5", new Vector2(0.00f, -20.00f), new Vector2(0.00f, -19.75f)),
        };

        /// <summary>The part offsets for a mount, or false for any other unit.</summary>
        public static bool TryGetPlacements(string unitId, out Vector2 gun, out Vector2 lamp)
        {
            if (!string.IsNullOrEmpty(unitId))
            {
                for (int i = 0; i < Placements.Length; i++)
                {
                    if (string.Equals(Placements[i].UnitId, unitId, System.StringComparison.OrdinalIgnoreCase))
                    {
                        gun = Placements[i].Gun;
                        lamp = Placements[i].Lamp;
                        return true;
                    }
                }
            }

            gun = Vector2.zero;
            lamp = Vector2.zero;
            return false;
        }

        /// <summary>
        /// A part's offset from the mount origin, converted to <b>room pixels</b> — the port's
        /// bottom-up space, the same space <see cref="UnitSpriteAnchor.PixelOffset"/> answers in.
        ///
        /// <para>Only the y sign changes. The mount's origin pixel and the part's offset are both
        /// measured down from the frame's top edge in Flash; a child transform in the port is measured
        /// up from the mount's feet. So <c>y_flash = -12</c> (twelve pixels above the origin, which in
        /// Flash is a smaller y) becomes <c>y_room = +12</c>.</para>
        /// </summary>
        public static Vector2 LocalPixels(Vector2 flashPlacement)
        {
            return new Vector2(flashPlacement.x, -flashPlacement.y);
        }

        /// <summary>
        /// The local offset, in room pixels, that puts <paramref name="desiredPxFromTopLeft"/> of a
        /// sprite on the node's own origin.
        ///
        /// <para>This is <see cref="UnitSpriteAnchor.PixelOffset"/>'s arithmetic with the sign of the
        /// answer reversed: the anchor moves a <i>renderer</i> so the registration point lands on the
        /// unit, this moves a <i>renderer under a pivot node</i> so the registration point lands on the
        /// node. Written out rather than reused because the anchor's version tests
        /// <c>registrationPoint &gt;= 0</c> to mean "declared", and a part pivot is a plain coordinate
        /// where 0 is a legal value.</para>
        ///
        /// <para><b>Why a pivot node at all.</b> A <see cref="Sprite"/>'s pivot is baked into the asset
        /// at import and cannot be changed at runtime, so a gun whose meta says "centre" would rotate
        /// about its middle however carefully the code asked it not to. Putting the renderer under an
        /// empty node at the registration point makes the rotation independent of the import settings —
        /// and makes it survive a re-import, which is the same reason the anchor is computed at draw
        /// time.</para>
        /// </summary>
        /// <param name="spritePivot">The sprite's pivot, in pixels from its bottom-left corner.</param>
        /// <param name="spriteSize">The sprite's pixel size.</param>
        /// <param name="desiredPxFromTopLeft">Where the registration point is in the image.</param>
        public static Vector2 PivotCorrection(Vector2 spritePivot, Vector2 spriteSize,
                                              Vector2 desiredPxFromTopLeft)
        {
            return new Vector2(spritePivot.x - desiredPxFromTopLeft.x,
                               spritePivot.y - (spriteSize.y - desiredPxFromTopLeft.y));
        }

        /// <summary>
        /// AS3 <c>vis.osn.puha.rotation = currentWeapon.rot * 180 / Math.PI</c> — and nothing else.
        ///
        /// <para><b>No flip term and no <c>rotUp</c>, unlike a held weapon.</b> A character's weapon is
        /// drawn with <c>vis.scaleX</c> and a <c>rotUp</c> lift (<see cref="PFE.Systems.Weapons.WeaponVisMath"/>);
        /// a mount is never mirrored (<c>UnitTurret</c> writes no <c>vis.scaleX</c>) and its barrel has no
        /// elevation term, so the oracle's whole rule is one multiply. Copying the held-weapon rule here
        /// would mirror the barrel and add a lift the mount does not have.</para>
        ///
        /// <para>The angle needs no sign change on the way into Unity: the port's
        /// <see cref="PFE.Sim.Weapons.WeaponRuntimeState.Rot"/> is already a Unity-convention world angle
        /// — <c>RangedWeaponController</c> computes it with <c>Mathf.Atan2</c> and
        /// <c>WeaponPresenter</c> writes it straight into <c>Quaternion.Euler</c>.</para>
        /// </summary>
        public static float GunRotationDeg(float rotRadians)
        {
            return rotRadians * Mathf.Rad2Deg;
        }

        /// <summary>
        /// The gun frame for a weapon <c>tr</c>. <c>gotoAndStop(this.tr)</c> uses <c>tr</c> as a frame
        /// number directly, and <c>tr</c> comes from the difficulty bands (1-3, 1-5, 1-7) or the
        /// placement's authored <c>@tr</c> — which is how <c>turret10</c> reaches the last frame. Values
        /// outside 1..10 are clamped rather than wrapped: a wrap would draw frame 1 for <c>tr = 11</c>,
        /// which looks like a working mount.
        /// </summary>
        public static int GunFrame(int tr)
        {
            if (tr < 1) return 1;
            return tr > GunFrameCount ? GunFrameCount : tr;
        }

        /// <summary>
        /// The lamp frame for an AI state: <c>vis.osn.light.gotoAndStop(aiState + 1)</c>
        /// (<c>UnitTurret.as:310-313</c>). Frame 1 is the unlit pose and the symbol's frame 1 is
        /// <b>empty</b>, so an idle mount shows no lamp — that is the oracle's behaviour, not a missing
        /// asset.
        /// </summary>
        public static int LampFrame(int aiState)
        {
            int frame = aiState + 1;
            if (frame < 1) return 1;
            return frame > LampFrameCount ? LampFrameCount : frame;
        }

        /// <summary><c>Resources</c> path of a mount's body, which is the composite frame minus the gun.</summary>
        public static string BodyResourcePath(string unitId) => "TurretParts/" + unitId + "/body";

        /// <inheritdoc cref="BodyResourcePath"/>
        public static string GunResourcePath(int frame)
            => "TurretParts/_shared/gun/f" + frame.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

        /// <inheritdoc cref="BodyResourcePath"/>
        public static string LampResourcePath(int frame)
            => "TurretParts/_shared/lamp/f" + frame.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    }
}
