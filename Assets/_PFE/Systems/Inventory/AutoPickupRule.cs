namespace PFE.Systems.Inventory
{
    /// <summary>
    /// The decision half of walk-over auto-collection — a faithful port of the proximity and magnet
    /// branches of AS3 <c>Loot.take()</c> (<c>fe/loc/Loot.as:281-326</c>).
    ///
    /// <para><b>Why this is its own class.</b> The decision is pure arithmetic on four numbers (the
    /// player's body centre, the loot's position, the take window, and the flag), so it runs on the
    /// offline wall. <c>PlayerAutoPickup</c> is then the untestable-but-obvious half: it gathers the
    /// numbers and applies the result. Same split as <c>InventoryCategoryRules</c>.</para>
    ///
    /// <para><b>Source pixels, not Unity units.</b> Every constant here is the oracle's own number. The
    /// art imports at 100 px per unit (the scale <c>WorldConstants.ACTION_REACH</c> and
    /// <c>WorldItemPickup.PickupRadius</c> already use), so the conversion is one multiply — but it is
    /// done in ONE place (<see cref="ToUnits"/>) rather than folded into each constant, because a
    /// silently pre-divided constant is indistinguishable from a typo.</para>
    /// </summary>
    public static class AutoPickupRule
    {
        /// <summary>Source pixels per Unity world unit — the art import scale.</summary>
        public const float PixelsToUnits = 0.01f;

        /// <summary>
        /// <c>Loot.as:290</c> — the take band, <b>±20 source px on each axis</b>. The oracle writes it
        /// as four strict comparisons (<c>_loc2_ &lt; 20 &amp;&amp; _loc2_ &gt; -20</c>), so the band is
        /// open, not closed: a loot at exactly 20 px is <i>not</i> taken.
        /// </summary>
        public const float TakeBandPixels = 20f;

        /// <summary>
        /// <c>Loot.as:314</c> — the magnet band, <c>±takeR</c>, and <c>takeR</c> starts at
        /// <c>osnRad = 50</c> (<c>Loot.as:15</c>). Only <c>actTake</c> (a cursor take) widens it to
        /// <c>actRad</c>, which the port has no counterpart for.
        /// </summary>
        public const float MagnetRadiusPixels = 50f;

        /// <summary>
        /// <c>Loot.as:314</c> — the magnet only runs from <c>isTake &gt;= 20</c>, i.e. during the first
        /// half of the window.
        /// </summary>
        public const int MagnetWindowThreshold = 20;

        /// <summary>
        /// <c>UnitPlayer.as:2551</c>, <c>:2570</c> (a horizontal direction held) and <c>:2685</c> (jump) —
        /// every writer sets the window to <b>40</b>. It is decremented once per tick
        /// (<c>:897-899</c>), so the window is 40 ticks.
        /// </summary>
        public const int TakeWindowTicks = 40;

        /// <summary>
        /// <c>Loot.as:318-320</c> — the magnet's per-tick displacement is <c>delta / 5</c>, so the loot
        /// closes one fifth of the remaining gap each tick.
        /// </summary>
        public const float MagnetDivisor = 5f;

        /// <summary>Source pixels → Unity world units.</summary>
        public static float ToUnits(float pixels) => pixels * PixelsToUnits;

        /// <summary>The take band half-width in world units (0.2).</summary>
        public static float TakeBand => ToUnits(TakeBandPixels);

        /// <summary>The magnet radius in world units (0.5).</summary>
        public static float MagnetRadius => ToUnits(MagnetRadiusPixels);

        /// <summary>
        /// <c>Loot.as:290</c> — is the loot inside the take band?
        ///
        /// <para><b>Both axes must pass, and both are open intervals.</b> The oracle's four comparisons
        /// are strict, so the boundary belongs to neither side.</para>
        /// </summary>
        public static bool WithinTakeBand(float dxUnits, float dyUnits)
            => dxUnits < TakeBand && dxUnits > -TakeBand
            && dyUnits < TakeBand && dyUnits > -TakeBand;

        /// <summary><c>Loot.as:314</c> — is the loot inside the magnet band? Also open on both axes.</summary>
        public static bool WithinMagnetBand(float dxUnits, float dyUnits)
            => dxUnits < MagnetRadius && dxUnits > -MagnetRadius
            && dyUnits < MagnetRadius && dyUnits > -MagnetRadius;

        /// <summary>
        /// <c>Loot.as:314</c> — the magnet runs while <c>isTake &gt;= 20</c>. <c>isTake</c> is <i>pinned</i>
        /// at 40 while a horizontal direction is held, so in practice this is "while walking".
        /// </summary>
        public static bool MagnetEngaged(int takeWindow) => takeWindow >= MagnetWindowThreshold;

        /// <summary><c>Loot.as:290</c> — the take branch needs <c>isTake &gt;= 1</c>.</summary>
        public static bool TakeEngaged(int takeWindow) => takeWindow >= 1;

        /// <summary>
        /// One tick of the magnet: the displacement toward <paramref name="deltaUnits"/>
        /// (<c>Loot.as:318-320</c>, <c>delta / 5</c>).
        /// </summary>
        public static float MagnetStep(float deltaUnits) => deltaUnits / MagnetDivisor;

        /// <summary>
        /// The player's <b>body centre</b> on the vertical axis, which is what the oracle measures the
        /// take band against — <c>Loot.as:289</c> is <c>gg.Y - gg.scY / 2 - lootY</c>, not <c>gg.Y - lootY</c>.
        ///
        /// <para><b>This is load-bearing, not a refinement.</b> The player's origin is its feet
        /// (<c>UnitController</c>: <c>Y1 = Y - scY</c>), and the player's body is 70 source px tall, so
        /// loot lying on the ground sits <b>35 px below the centre</b> — outside the ±20 px take band.
        /// Ground loot is therefore only ever taken <i>after the magnet has lifted it</i> into the band.
        /// Measuring from the feet instead would make the band miss every grounded item and the whole
        /// feature would look broken while every individual number still looked right.</para>
        /// </summary>
        public static float BodyCentreY(float feetY, float bodyHeightUnits) => feetY + bodyHeightUnits * 0.5f;
    }
}
