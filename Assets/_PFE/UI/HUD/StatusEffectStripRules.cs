using System;
using System.Collections.Generic;
using System.Globalization;

namespace PFE.UI.HUD
{
    /// <summary>
    /// One live effect as the strip needs to see it. A plain struct rather than
    /// <see cref="PFE.Systems.Effects.ActiveEffect"/> so the rule below is executable in an offline
    /// fixture without building a template, a host or a resolver — the same split
    /// <c>HealthReadout</c> and <c>PickupFeedbackRules</c> already use.
    /// </summary>
    public readonly struct StatusEffectEntry
    {
        /// <summary>AS3 <c>Effect.id</c> — the frame label to draw.</summary>
        public readonly string Id;

        /// <summary>AS3 <c>Effect.t</c> — remaining canonical 30 Hz ticks.</summary>
        public readonly int TicksRemaining;

        /// <summary>AS3 <c>Effect.forever</c> — drawn as <c>∞</c> instead of a countdown.</summary>
        public readonly bool Forever;

        /// <summary>AS3 <c>Effect.vse</c> — being unset, so the oracle does not draw it.</summary>
        public readonly bool BeingUnset;

        /// <summary>AS3 <c>Effect.tip == 3</c> — the food/potion channel, drawn with the shared <c>food</c> frame.</summary>
        public readonly bool IsFood;

        public StatusEffectEntry(string id, int ticksRemaining, bool forever = false,
            bool beingUnset = false, bool isFood = false)
        {
            Id = id;
            TicksRemaining = ticksRemaining;
            Forever = forever;
            BeingUnset = beingUnset;
            IsFood = isFood;
        }
    }

    /// <summary>One strip slot: a sprite index and the countdown printed under it.</summary>
    public readonly struct StatusEffectSlotDisplay
    {
        /// <summary>Index into <c>statusEffectIcons</c>; <c>-1</c> means "this slot draws nothing".</summary>
        public readonly int IconIndex;

        /// <summary>The text under the icon. Empty when hidden.</summary>
        public readonly string Text;

        public StatusEffectSlotDisplay(int iconIndex, string text)
        {
            IconIndex = iconIndex;
            Text = text;
        }

        public bool IsHidden => IconIndex < 0;

        public static readonly StatusEffectSlotDisplay Hidden = new StatusEffectSlotDisplay(-1, string.Empty);
    }

    /// <summary>
    /// The port of <c>GUI.setEffects</c> (<c>…\pfe\scripts\fe\inter\GUI.as:1140-1242</c>) — which twelve
    /// icons the strip shows and what each one prints.
    ///
    /// <para><b>The slot count is 12, not a layout choice.</b> <c>internal var kolEff:int = 12</c>
    /// (<c>GUI.as:174</c>), and the constructor caches exactly that many children
    /// <c>vis["eff" + i]</c> (<c>:295-301</c>). The SWF only names <c>eff0..eff11</c>.</para>
    ///
    /// <para><b>The priority order is the oracle's, and it is a fixed three-then-list cascade.</b>
    /// Bleeding, then poison, then the magic shield, then <c>gg.effects</c> in array order. Each of the
    /// three specials may appear <b>at most once</b>, which is what the oracle's <c>t1</c>/<c>t2</c>/<c>t3</c>
    /// flags do: the flag is set on the first slot that <i>considers</i> it, not on the first slot that
    /// shows it. The flat loop below is that nested if/else chain unrolled — see
    /// <see cref="Build"/>.</para>
    ///
    /// <para><b>An unset effect blocks the tail rather than being skipped.</b> The oracle tests
    /// <c>n &lt; effects.length &amp;&amp; !effects[n].vse</c> and only advances <c>n</c> on a draw
    /// (<c>:1201-1228</c>), so a <c>vse</c> effect at the head leaves <b>every remaining slot empty</b>
    /// for that frame instead of pulling the next effect forward. The set splices it on the next tick, so
    /// the visible cost is one frame — but a port that "skipped" it would draw a different list, and the
    /// difference is exactly the kind of thing that later reads as an off-by-one.</para>
    /// </summary>
    public static class StatusEffectStripRules
    {
        /// <summary><c>kolEff</c> — <c>GUI.as:174</c>.</summary>
        public const int SlotCount = 12;

        /// <summary><c>World.fps</c> — the rate <c>Effect.t</c> is counted in (<c>GUI.as:1206</c> divides by it).</summary>
        public const int TicksPerSecond = 30;

        /// <summary>The forever marker — <c>veff[i].txt.text = "∞"</c> (<c>GUI.as:1210</c>).</summary>
        public const string ForeverText = "∞";

        /// <summary>
        /// Build the twelve slots.
        /// </summary>
        /// <param name="cut">AS3 <c>gg.cut</c> — the bleeding accumulator. <b>0 in this port</b>: see the remarks on <see cref="Build"/>.</param>
        /// <param name="poison">AS3 <c>gg.poison</c>. <b>0 in this port</b> — same reason.</param>
        /// <param name="shieldHp">AS3 <c>gg.shithp</c> — <c>UnitStats.ShitHp</c>, which the port does have.</param>
        /// <param name="effects">The live effect list, in array order (<c>UnitStats.Effects.Effects</c>).</param>
        /// <param name="slots">Destination, at least <see cref="SlotCount"/> long.</param>
        /// <remarks>
        /// <para><b>Why the loop is flat.</b> The oracle nests the cascade, and every level of the
        /// nesting sets its own flag on the way in, so the effective rule is "cut once, poison once,
        /// shield once, then the list" — which is what this loop says directly. Re-nesting it here would
        /// reproduce the <i>shape</i> of the original without making the behaviour any clearer, and the
        /// fixture asserts the behaviour.</para>
        ///
        /// <para><b><c>cut</c> and <c>poison</c> have no source in this port.</b> AS3 keeps them as
        /// plain <c>UnitPlayer</c> fields — <c>cut -= param1</c> (<c>UnitPlayer.as:3181-3186</c>) and the
        /// same for <c>poison</c> — and the port models bleeding as organ damage
        /// (<c>CharacterStats.ApplyBloodDamage</c>) with no accumulator to read. They are parameters
        /// rather than constants so wiring them later is one call site, and so this rule is testable
        /// against the oracle's own numbers today.</para>
        /// </remarks>
        public static void Build(
            float cut,
            float poison,
            float shieldHp,
            IReadOnlyList<StatusEffectEntry> effects,
            StatusEffectSlotDisplay[] slots)
        {
            if (slots == null)
            {
                return;
            }

            if (slots.Length < SlotCount)
            {
                throw new ArgumentException(
                    "slots must have at least " + SlotCount + " entries", nameof(slots));
            }

            bool cutConsidered = false;
            bool poisonConsidered = false;
            bool shieldConsidered = false;
            int next = 0;

            for (int i = 0; i < SlotCount; i++)
            {
                if (!cutConsidered)
                {
                    cutConsidered = true;
                    if (cut > 0f)
                    {
                        slots[i] = new StatusEffectSlotDisplay(
                            StatusEffectIcons.IndexOf("cut"), Magnitude(cut));
                        continue;
                    }
                }

                if (!poisonConsidered)
                {
                    poisonConsidered = true;
                    if (poison > 0f)
                    {
                        slots[i] = new StatusEffectSlotDisplay(
                            StatusEffectIcons.IndexOf("poison"), Magnitude(poison));
                        continue;
                    }
                }

                if (!shieldConsidered)
                {
                    shieldConsidered = true;
                    if (shieldHp > 0f)
                    {
                        slots[i] = new StatusEffectSlotDisplay(
                            StatusEffectIcons.IndexOf("shit"),
                            Math.Ceiling(shieldHp).ToString(CultureInfo.InvariantCulture));
                        continue;
                    }
                }

                // The list arm. A vse entry is not drawn AND does not advance `next` — see the class
                // remarks; the oracle's `else { visible = false }` is what this reproduces.
                if (effects != null && next < effects.Count && !effects[next].BeingUnset)
                {
                    StatusEffectEntry entry = effects[next];
                    next++;

                    string icon = entry.IsFood ? "food" : entry.Id;
                    string text = entry.Forever
                        ? ForeverText
                        : (entry.TicksRemaining / TicksPerSecond).ToString(CultureInfo.InvariantCulture);

                    slots[i] = new StatusEffectSlotDisplay(StatusEffectIcons.IndexOf(icon), text);
                    continue;
                }

                slots[i] = StatusEffectSlotDisplay.Hidden;
            }
        }

        /// <summary>
        /// <c>Math.round(cut * 10) / 10</c> (<c>GUI.as:1174</c>) — one decimal, and trailing zeros are
        /// not printed because Flash's number-to-string does not print them either.
        ///
        /// <para><b><c>AwayFromZero</c>, not .NET's default.</b> <c>Math.Round(double)</c> is
        /// banker's rounding, so 2.25 would become 2.2 where AS3's <c>Math.round</c> gives 2.3 — a
        /// visible off-by-a-tenth on exactly the values a bleed accumulator produces.</para>
        /// </summary>
        private static string Magnitude(float value)
        {
            double rounded = Math.Round(value * 10.0, MidpointRounding.AwayFromZero) / 10.0;
            return rounded.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
