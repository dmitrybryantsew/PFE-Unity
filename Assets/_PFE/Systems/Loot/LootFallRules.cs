using System;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// The falling half of AS3 <c>fe.loc.Loot</c> — gravity, sub-stepping, the landing snap and the
    /// <c>sndFall</c> ladder — as pure functions of their arguments.
    ///
    /// <para><b>Why a rules class and not the MonoBehaviour.</b> The port had <i>no</i> falling-loot
    /// model at all: <c>WorldItemPickup.Spawn</c> placed a static trigger collider at the requested
    /// point, so container loot appeared in a neat pile the instant the crate opened. The oracle does
    /// the opposite — <c>LootGen.newLoot</c> constructs every rolled item with <c>jump = true</c>
    /// (<c>LootGen.as:293</c>), so each one is thrown upward with a random horizontal shove and falls
    /// to the floor, playing its own landing sample when it gets there. Porting that means porting
    /// arithmetic that runs every frame for every pickup on the floor, and arithmetic inside a
    /// <c>MonoBehaviour</c> is arithmetic no offline fixture can pin. So the arithmetic lives here and
    /// the component only carries out the answers.</para>
    ///
    /// <para><b>Sign convention: this class speaks the port's Y-up space, not AS3's.</b> The oracle's
    /// <c>dy</c> is <i>positive downward</i> (AS3 Y runs down); the port's <c>vy</c> is <i>positive
    /// upward</i>. Every rule below is the exact mirror of its oracle line, and the mirror is applied
    /// here once rather than at each call site — the project has already paid for scattering it
    /// (<c>MEMORY.md</c>: "AS3 Y runs down, the port's up — a mirror, never a floor"). The oracle's
    /// magnitudes are untouched: <c>World.ddy = 1</c> px/frame², <c>World.maxdy = 20</c> px/frame,
    /// <c>World.maxdelta = 9</c> px/substep, and the landing gate <c>dy &gt; 5</c>.</para>
    /// </summary>
    public static class LootFallRules
    {
        // ── The oracle's constants, verbatim ────────────────────────────────────────────────────────

        /// <summary>AS3 <c>World.ddy</c> = 1 px/frame² (<c>World.as:46</c>).</summary>
        public const float Ddy = 1f;

        /// <summary>
        /// AS3 <c>World.maxdy</c> = 20 px/frame (<c>World.as:48</c>). <c>Loot.step</c> accelerates only
        /// while <c>dy &lt; World.maxdy</c> (<c>Loot.as:352</c>), so this is a terminal speed as well as
        /// the gate.
        /// </summary>
        public const float MaxDy = 20f;

        /// <summary>
        /// AS3 <c>World.maxdelta</c> = 9 px (<c>World.as:52</c>) — the longest single movement a
        /// collision sub-step may take (<c>Loot.as:372</c>).
        /// </summary>
        public const float MaxDelta = 9f;

        /// <summary>
        /// The landing sample's gate: <c>if(dy &gt; 5 &amp;&amp; Boolean(this.sndFall))</c>
        /// (<c>Loot.as:481</c>). A slow settle is silent, which is what stops a one-pixel nudge from
        /// rattling.
        /// </summary>
        public const float LandingSoundMinDy = 5f;

        /// <summary>The landing volume divisor: <c>Snd.ps(sndFall, X, Y, 0, dy / 15)</c> (<c>Loot.as:483</c>).</summary>
        public const float LandingVolumeDivisor = 15f;

        /// <summary>The splash volume divisor: <c>Snd.ps("fall_item_water", X, Y, 0, dy / 10)</c> (<c>Loot.as:547</c>).</summary>
        public const float WaterVolumeDivisor = 10f;

        /// <summary>AS3 <c>Loot.scX</c> default — 30 px, the loot's take box width (<c>Loot.as:83</c>).</summary>
        public const float DefaultWidthPx = 30f;

        /// <summary>AS3 <c>Loot.scY</c> default — 20 px, the loot's take box height (<c>Loot.as:84</c>).</summary>
        public const float DefaultHeightPx = 20f;

        // ── The sound ids ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>Loot.sndFall</c>'s initial value (<c>Loot.as:45</c>). It survives unless the item's own row
        /// overrides it, which is why the default is on the class and not a fallback at the call site.
        /// </summary>
        public const string DefaultFallSound = "fall_item";

        /// <summary>
        /// What a <c>scheme</c> or a <c>book</c> falls as — <c>Loot.as:206-215</c> sets
        /// <c>"fall_paper"</c> for both, before the row's own <c>@fall</c> gets a chance to override it.
        /// </summary>
        public const string PaperFallSound = "fall_paper";

        /// <summary>The splash — <c>Loot.checkWater</c> (<c>Loot.as:547</c>).</summary>
        public const string WaterFallSound = "fall_item_water";

        /// <summary>
        /// <c>Loot.checkWater</c>'s own gate is <c>dy &gt; 5</c> on the frame the loot enters water
        /// (<c>Loot.as:541</c>), the same threshold as the landing sample.
        /// </summary>
        public const float WaterSoundMinDy = 5f;

        // ── Velocity ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One frame of gravity — <c>if(!levit &amp;&amp; !this.vsos &amp;&amp; dy &lt; World.maxdy) dy += World.ddy</c>
        /// (<c>Loot.as:352-355</c>), mirrored to Y-up.
        ///
        /// <para><b>The clamp is the <i>gate</i>, not a <c>min</c>.</b> The oracle only adds while the
        /// speed is below the terminal, so a loot already at or past 20 px/frame keeps whatever it has
        /// instead of being pulled back to 20. Using <c>Math.Max(-MaxDy, vy - Ddy)</c> would be a
        /// different (and here indistinguishable) function for the values this ever sees, but it would
        /// stop being a port of that line — and the first caller that hands in a thrown 30 px/frame
        /// would see the difference.</para>
        /// </summary>
        public static float StepVelocity(float vy)
        {
            return vy > -MaxDy ? vy - Ddy : vy;
        }

        /// <summary>
        /// How many collision sub-steps one frame of movement needs — <c>Loot.as:372</c>:
        /// <c>floor(max(|dx|,|dy|) / World.maxdelta) + 1</c>. A negative or zero speed still asks for
        /// one step, which is what the oracle's <c>+1</c> guarantees.
        /// </summary>
        public static int SubstepCount(float vx, float vy)
        {
            float longest = Math.Max(Math.Abs(vx), Math.Abs(vy));
            return (int)(longest / MaxDelta) + 1;
        }

        // ── Landing ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether a landing at this speed makes a noise — <c>dy &gt; 5</c> (<c>Loot.as:481</c>),
        /// mirrored. Takes the <b>downward</b> speed as a positive number so the call site reads like the
        /// oracle's line; the component passes <c>-vy</c>.
        /// </summary>
        public static bool ShouldPlayLandingSound(float downwardSpeed)
        {
            return downwardSpeed > LandingSoundMinDy;
        }

        /// <summary>The landing sample's volume scale — <c>dy / 15</c> (<c>Loot.as:483</c>).</summary>
        public static float LandingVolume(float downwardSpeed)
        {
            return downwardSpeed / LandingVolumeDivisor;
        }

        /// <summary>The splash's volume scale — <c>dy / 10</c> (<c>Loot.as:547</c>).</summary>
        public static float WaterVolume(float downwardSpeed)
        {
            return downwardSpeed / WaterVolumeDivisor;
        }

        /// <summary>
        /// The throw a freshly spawned loot starts with — <c>Loot.as:229-233</c>:
        /// <code>
        /// if(jump) { dx = Math.random() * 10 - 5;  dy = Math.random() * 5 - 10; }
        /// </code>
        ///
        /// <para><b>The vertical sign is the whole point.</b> In AS3 <c>dy ∈ [-10, -5)</c> is
        /// <i>upward</i>, because Y runs down; so a jumping loot is lobbed <b>5..10 px/frame up</b> and
        /// <b>±5 px/frame sideways</b>. Returned here in Y-up terms (<paramref name="vy"/> positive),
        /// which is the same throw. A sign slip would bury the loot in the floor instead of tossing it,
        /// and it would still "work" — which is why the direction is pinned by a fixture.</para>
        ///
        /// <para><paramref name="r1"/> and <paramref name="r2"/> are the two RNG draws, each in
        /// <c>[0,1)</c>, in the order the oracle draws them (x first). Passing them in rather than
        /// calling a random source keeps this testable.</para>
        /// </summary>
        public static void JumpVelocity(float r1, float r2, out float vx, out float vy)
        {
            vx = r1 * 10f - 5f;
            // AS3: dy = r * 5 - 10  (negative = up). Mirrored: vy = 10 - r * 5.
            vy = 10f - r2 * 5f;
        }

        // ── The fall sample ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve <c>Loot.sndFall</c> for one drop — the whole of <c>Loot</c>'s constructor ladder
        /// (<c>Loot.as:45</c>, <c>:120-123</c>, <c>:134-137</c>, <c>:158-161</c>, <c>:206-219</c>).
        ///
        /// <para><b>Four branches, and they do not all read the same attribute.</b> This is the part that
        /// looks uniform and is not:</para>
        /// <list type="bullet">
        ///   <item><description><b>weapon</b> reads <c>xml.snd.@fall</c> — the <i>child</i>
        ///     <c>&lt;snd&gt;</c> node (<c>:120</c>).</description></item>
        ///   <item><description><b>expl</b> and <b>ammo</b> read <c>xml.@fall</c> — the row's own
        ///     attribute (<c>:134</c>, <c>:158</c>).</description></item>
        ///   <item><description><b>everything else</b> reads <c>xml.@fall</c> too (<c>:216</c>), but only
        ///     <i>after</i> <c>scheme</c> and <c>book</c> have been given
        ///     <see cref="PaperFallSound"/>. So for those two tips <c>@fall</c> <b>overrides</b> the
        ///     paper default, and for every other generic tip the default is
        ///     <see cref="DefaultFallSound"/>.</description></item>
        /// </list>
        ///
        /// <para>A weapon that has no <c>&lt;snd fall&gt;</c> keeps <see cref="DefaultFallSound"/>;
        /// it does <b>not</b> fall back to <c>@fall</c>, because the oracle never reads the root
        /// attribute on that branch.</para>
        /// </summary>
        /// <param name="isWeapon">The roll resolved through the <b>weapon</b> table.</param>
        /// <param name="isExpl">AS3 <c>Item.L_EXPL</c> — <c>"e"</c>.</param>
        /// <param name="isAmmo">AS3 <c>Item.L_AMMO</c> — <c>"a"</c>.</param>
        /// <param name="isScheme">AS3 <c>Item.L_SCHEME</c> — <c>"scheme"</c>.</param>
        /// <param name="isBook">AS3 <c>Item.L_BOOK</c> — <c>"book"</c>.</param>
        /// <param name="rootFall">The row's own <c>@fall</c>, or null/empty when absent.</param>
        /// <param name="sndFall">The row's <c>&lt;snd fall="…"&gt;</c>, or null/empty when absent.</param>
        public static string ResolveFallSoundId(
            bool isWeapon, bool isExpl, bool isAmmo, bool isScheme, bool isBook,
            string rootFall, string sndFall)
        {
            string sound = DefaultFallSound;

            if (isWeapon)
            {
                if (!string.IsNullOrEmpty(sndFall)) sound = sndFall;
            }
            else if (isExpl || isAmmo)
            {
                if (!string.IsNullOrEmpty(rootFall)) sound = rootFall;
            }
            else
            {
                // `if(tip == L_SCHEME) sndFall = "fall_paper"; if(tip == L_BOOK) sndFall = "fall_paper";`
                // — two separate ifs in the oracle, which is why this is not an `||` that a later edit
                // could merge with the weapon branch above.
                if (isScheme) sound = PaperFallSound;
                if (isBook) sound = PaperFallSound;

                // ...and only then does the row's own @fall win (`:216`).
                if (!string.IsNullOrEmpty(rootFall)) sound = rootFall;
            }

            return sound;
        }

        /// <summary>
        /// The same ladder, resolving the row out of the shipped <c>AllData</c> catalog first — the form
        /// every drop site actually wants, because a caller has the roll's <b>tip</b> and <b>id</b> and not
        /// the raw attributes.
        ///
        /// <para><b>One definition, three call sites.</b> A container's eject
        /// (<c>ContainerPropPresenter</c>), a player's drop (<c>PlayerInventory.DropItem</c>) and a corpse's
        /// spill (<c>UnitController.SpillDrops</c>) all need this answer. Each of them used to own a copy
        /// of the ladder in an earlier draft of this change, which is three places for the
        /// <c>snd/@fall</c>-versus-<c>@fall</c> distinction to rot in.</para>
        ///
        /// <para><b>Which table is asked matters.</b> A weapon's <c>&lt;snd fall&gt;</c> lives in
        /// <c>AllData.d.weapon</c> and an item's in <c>AllData.d.item</c>; 32 ids exist in both, so asking
        /// the wrong one would hand a weapon the sound of a same-named item.
        /// <see cref="ILootItemCatalog.Find"/> is tip-routed for exactly that reason. A null catalog is not
        /// an error: the default <c>fall_item</c> is a real answer and is what an item with no
        /// <c>@fall</c> gets anyway.</para>
        /// </summary>
        /// <param name="catalog">The <c>AllData</c> row source, or null.</param>
        /// <param name="isWeapon">Whether this drop resolved through the weapon table.</param>
        /// <param name="tip">The roll's effective AS3 tip.</param>
        /// <param name="itemId">The row id, variant-stripped.</param>
        public static string ResolveFallSoundId(ILootItemCatalog catalog, bool isWeapon, string tip, string itemId)
        {
            string rootFall = null;
            string sndFall = null;

            if (catalog != null)
            {
                LootItemRow? row = catalog.Find(isWeapon ? LootTips.Weapon : tip, itemId);
                if (row.HasValue)
                {
                    rootFall = row.Value.FallSound;
                    sndFall = row.Value.SndFallSound;
                }
            }

            return ResolveFallSoundId(
                isWeapon,
                isExpl: tip == LootTips.Expl,
                isAmmo: tip == LootTips.Ammo,
                isScheme: tip == LootTips.Scheme,
                isBook: tip == LootTips.Book,
                rootFall: rootFall,
                sndFall: sndFall);
        }
    }
}
