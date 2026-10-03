using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Where a held weapon sits, as a pure function of the owner's body and the cursor — the port of
    /// AS3 <c>UnitPlayer.setWeaponPos</c> (<c>UnitPlayer.as:3514-3585</c>) and the base
    /// <c>Unit.setWeaponPos</c> (<c>Unit.as:3288-3294</c>).
    ///
    /// <para><b>Why this exists.</b> The shipped rig put the weapon on a fixed marker child at local
    /// <c>(1, 1)</c> — one unit right and one unit up from the character's feet. The character is
    /// <c>0.7</c> units tall, so the gun was drawn about <c>0.65</c> units above its own head. That is
    /// the reported "levitation". AS3 has no such marker: the hold point is <i>derived</i> every frame
    /// from the owner's body box and where the cursor is, which is what makes the weapon rise and fall
    /// with the character and reach out when you aim past its snout.</para>
    ///
    /// <para><b>The axis flip is the whole trick.</b> Flash's y grows downwards, so AS3 writes
    /// <c>weaponY = Y - scY * 0.7</c> and means "0.7 of a body-height <i>above</i> the feet". Unity's
    /// y grows upwards and the unit's transform sits on its feet (<c>RoomUnitSpawner.cs:330</c> puts
    /// the collider at <c>+height/2</c> so the box spans feet-to-head), so the same point is
    /// <c>feetY + scY * 0.7</c>. Every literal below is transcribed from the oracle and then flipped
    /// once, here, rather than being pre-negated at each call site.</para>
    ///
    /// <para><b>Unity-free on purpose.</b> Everything takes and returns plain <see cref="Vector2"/>
    /// numbers — no <c>Transform</c>, no <c>MonoBehaviour</c> — so the rule can be executed and pinned
    /// offline, the way <c>UnitSweepMath</c> and <c>UnitSpriteAnchor</c> are.</para>
    /// </summary>
    public static class WeaponHoldPointMath
    {
        /// <summary>
        /// Flash pixels per Unity unit. The oracle's magic numbers (<c>15</c>, <c>40</c>) are pixel
        /// distances and only mean anything once divided by this.
        /// </summary>
        public const float FlashPixelsPerUnit = 100f;

        /// <summary>AS3's <c>scY * 0.7</c> — the normal hold height, 70% of a body up from the feet.</summary>
        public const float NormalHoldHeightFraction = 0.7f;

        /// <summary>
        /// AS3's <c>scY * 0.4</c> — the <c>tip == 1</c> hold height. <c>tip</c> 1 is the punch/kick
        /// family, held lower than a gun.
        /// </summary>
        public const float PunchHoldHeightFraction = 0.4f;

        /// <summary>AS3's base-class <c>scY * 0.5</c> — the fallback for a unit with <c>krep</c>.</summary>
        public const float FallbackHoldHeightFraction = 0.5f;

        /// <summary>AS3's <c>weaponY -= 40</c> — the weapon-up lift, in Flash pixels.</summary>
        public const float WeaponUpLiftPixels = 40f;

        /// <summary>AS3's <c>weaponX + storona * 15</c> — how far ahead the wall probe looks, in Flash pixels.</summary>
        public const float WallProbeAheadPixels = 15f;

        /// <summary>
        /// Everything the rule reads. A struct rather than a long parameter list because the oracle's
        /// branch reads the owner twice and the aim once, and positional <c>bool</c> arguments at a
        /// call site are unreadable.
        ///
        /// <para>Defaults describe the shipped player: <c>littlepip</c> is <c>0.5 × 0.7</c>, carries no
        /// <c>krep</c> (<c>weaponKrep == 0</c>, which is what selects the derived branch at all), and
        /// is not on a staircase, not standing with the weapon raised, and not mid weapon-swap.</para>
        /// </summary>
        public struct Inputs
        {
            /// <summary>Owner's world X. AS3 <c>X</c>; the body box's horizontal centre.</summary>
            public float OwnerX;

            /// <summary>
            /// Owner's world Y <b>at the feet</b>. AS3 <c>Y</c> — the oracle's origin is the feet
            /// (<c>Unit.as:1875-1878</c>: <c>Y2 = Y</c> is the box's bottom), and the port keeps the
            /// same convention, so this is <c>UnitController.FeetWorldY</c> — the unit's own
            /// transform position, not its collider's lower edge.
            /// </summary>
            public float OwnerFeetY;

            /// <summary>Body width in world units. AS3 <c>scX</c>.</summary>
            public float BodyWidth;

            /// <summary>Body height in world units. AS3 <c>scY</c>.</summary>
            public float BodyHeight;

            /// <summary>Facing: <c>+1</c> right, <c>-1</c> left. AS3 <c>storona</c>.</summary>
            public float FacingSign;

            /// <summary>Cursor / aim world X. AS3 <c>celX</c>.</summary>
            public float AimX;

            /// <summary>
            /// AS3 <c>tip</c> — the weapon family. <c>1</c> is the punch/kick hold height; every other
            /// value takes the normal one. Also gates the weapon-swap override (<c>tip != 5</c>).
            /// </summary>
            public int Tip;

            /// <summary>
            /// AS3 <c>weaponKrep</c> — the owner's <c>krep</c> attribute, defaulting to <c>0</c>.
            /// Non-zero sends the whole rule to the base-class fallback, so this is the branch selector,
            /// not a tuning knob.
            /// </summary>
            public float WeaponKrep;

            /// <summary>
            /// AS3 <c>isLaz</c> — the <b>stair direction</b> the owner is standing on: <c>0</c> for
            /// level ground, <c>±1</c> on a slope. Assigned at <c>Unit.as:2604</c>
            /// (<c>isLaz = storona = tile.stair</c>) and tested for truthiness here.
            ///
            /// <para>Despite the name it is not a laser sight: on a staircase the oracle pins the
            /// weapon to the body and suppresses the reach-out, so a unit climbing does not poke its
            /// gun into the step in front of it.</para>
            /// </summary>
            public bool OnStairs;

            /// <summary>AS3 <c>stay</c> — the owner is standing still. Half of the weapon-up gate.</summary>
            public bool Stay;

            /// <summary>AS3 <c>weapUp</c> — the owner has the weapon raised. Half of the weapon-up gate.</summary>
            public bool WeaponUp;

            /// <summary>
            /// AS3 <c>work == "change" &amp;&amp; t_work &gt; changeWeaponTime3</c> — the swap animation has
            /// reached its "holstered" phase, so the weapon drops to the base hold while it is swapped.
            /// </summary>
            public bool SwappingWeapon;
        }

        /// <summary>
        /// Resolve the hold point in world units.
        /// </summary>
        /// <param name="inputs">Owner body, facing and aim. See <see cref="Inputs"/>.</param>
        /// <param name="isSolidAt">
        /// Whether a solid tile covers a world-space point — AS3's <c>loc.getAbsTile(x, y).phis == 1</c>.
        /// Pass <c>null</c> when the caller has no tile map: the oracle's clamps then never fire, which
        /// is what AS3 does in a room with no walls at the probe points. The clamps only pull the weapon
        /// back from a wall it would otherwise poke into, so omitting them is a visible-at-the-wall
        /// difference and nothing else.
        /// </param>
        public static Vector2 Resolve(in Inputs inputs, System.Func<Vector2, bool> isSolidAt = null)
        {
            float ownerX = inputs.OwnerX;
            float feetY  = inputs.OwnerFeetY;
            float scX    = inputs.BodyWidth;
            float scY    = inputs.BodyHeight;
            float storona = inputs.FacingSign >= 0f ? 1f : -1f;

            float weaponX;
            float weaponY;

            if (inputs.WeaponKrep == 0f)
            {
                // ── AS3: the body box's edges, `X1 = X - scX/2` / `X2 = X + scX/2` ──
                float x1 = ownerX - scX * 0.5f;
                float x2 = ownerX + scX * 0.5f;

                // AS3: `if(storona > 0 && celX > X2 || storona < 0 && celX < X1) weaponX = X + scX * storona;`
                // The gun only leaves the body once the cursor is past the snout, and then by a whole
                // body width. Written as two positive tests so it reads like the oracle rather than as
                // a single signed comparison.
                bool cursorPastSnout = storona > 0f ? inputs.AimX > x2 : inputs.AimX < x1;
                weaponX = cursorPastSnout ? ownerX + scX * storona : ownerX;

                // AS3: a unit on a staircase holds the weapon against the body (isLaz != 0).
                if (inputs.OnStairs)
                    weaponX = ownerX;

                // Hold height. AS3 assigns weaponY *after* the clamps below, so the clamps read the
                // previous frame's height. Computed first here instead: the oracle's ordering is a
                // stale read, and reproducing it would make the result depend on call history for a
                // difference of nothing — the two heights only diverge on the frame `tip` changes.
                weaponY = feetY + scY * (inputs.Tip == 1
                    ? PunchHoldHeightFraction
                    : NormalHoldHeightFraction);

                // AS3: `if(loc.getAbsTile(weaponX,weaponY).phis == 1 || loc.getAbsTile(weaponX + storona*15,weaponY).phis == 1) weaponX = X;`
                // Probe the hold point and a point 15 px further out; either being solid pulls the
                // weapon back onto the body.
                if (isSolidAt != null)
                {
                    float ahead = storona * (WallProbeAheadPixels / FlashPixelsPerUnit);
                    if (isSolidAt(new Vector2(weaponX, weaponY)) ||
                        isSolidAt(new Vector2(weaponX + ahead, weaponY)))
                    {
                        weaponX = ownerX;
                    }
                }

                // AS3: `if(stay && this.weapUp) if(getTile(... weaponY - 40 ...).phis != 1) weaponY -= 40;`
                // Weapon-up lifts the gun 40 px, unless there is a ceiling to bump into.
                if (inputs.Stay && inputs.WeaponUp)
                {
                    float lifted = weaponY + WeaponUpLiftPixels / FlashPixelsPerUnit;
                    if (isSolidAt == null || !isSolidAt(new Vector2(weaponX, lifted)))
                        weaponY = lifted;
                }
            }
            else
            {
                // AS3 base `Unit.setWeaponPos`: a unit with `krep` gets the flat waist-height hold.
                weaponX = ownerX;
                weaponY = feetY + scY * FallbackHoldHeightFraction;
            }

            // AS3: `if(this.work == "change" && this.t_work > this.changeWeaponTime3 && tip != 5)`
            // — mid-swap the weapon drops to the base hold, whatever the branch above decided.
            if (inputs.SwappingWeapon && inputs.Tip != 5)
            {
                weaponX = ownerX;
                weaponY = feetY + scY * FallbackHoldHeightFraction;
            }

            return new Vector2(weaponX, weaponY);
        }

        /// <summary>
        /// The owner's <b>magic / horn</b> mount: AS3 takes the muzzle bone's tip through
        /// <c>localToGlobal</c> and falls back to the body when that point is inside a wall
        /// (<c>UnitPlayer.as:3561-3584</c>).
        ///
        /// <para>The bone path needs the rig, so this port covers only the two fallbacks — which is
        /// what the oracle itself uses whenever the bone lookup throws. It is deliberately separate
        /// from <see cref="Resolve"/>: the two are independent assignments in AS3, and the magic
        /// fallback is <c>Y - 35</c>/<c>Y - 75</c> px rather than any fraction of the body.</para>
        /// </summary>
        /// <param name="ownerX">Owner's world X.</param>
        /// <param name="ownerFeetY">Owner's world Y at the feet.</param>
        /// <param name="bodyHeight">AS3 <c>scY</c>, used by the <c>catch</c> fallback.</param>
        /// <param name="isSitting">AS3 <c>isSit</c> — seated owners use the shorter 35 px drop.</param>
        /// <param name="blocked">
        /// Whether the bone point was inside a solid tile. Callers with no rig pass <c>false</c>, which
        /// reproduces the oracle's <c>try</c> path failing straight to the <c>catch</c>.
        /// </param>
        public static Vector2 ResolveMagicFallback(float ownerX, float ownerFeetY, float bodyHeight,
                                                   bool isSitting = false, bool blocked = false)
        {
            if (!blocked)
                return new Vector2(ownerX, ownerFeetY + bodyHeight * FallbackHoldHeightFraction);

            // AS3: `magicY = Y - 35` seated, `Y - 75` standing (Flash y, so both are lifts).
            float dropPx = isSitting ? 35f : 75f;
            return new Vector2(ownerX, ownerFeetY + dropPx / FlashPixelsPerUnit);
        }
    }
}
