using System;

namespace PFE.Sim.Units
{
    /// <summary>
    /// The <c>act</c> half of a <c>&lt;rep&gt;</c> row — the reason a unit opens its mouth.
    ///
    /// <para><b>Every value is a literal string in the oracle.</b> <c>replic("attack")</c>,
    /// <c>replic("dam")</c>, and so on; the string is both the argument and the row key. The enum
    /// exists so a typo is a compile error instead of a silently missing bark, and
    /// <see cref="ReplicRules.KeyFor"/> is the single place that maps back to the document's
    /// spelling — <c>donttoch</c> is misspelled in the data and must stay misspelled here.</para>
    /// </summary>
    public enum ReplicKind
    {
        /// <summary>No bark. The default value, so an uninitialised field is silent rather than noisy.</summary>
        None = 0,

        /// <summary><c>neutral</c> — idle chatter. Raider: 0.1 % per idle frame; robot: 2 % every 30 ticks.</summary>
        Neutral,

        /// <summary><c>attack</c> — the unit has seen the player and is engaging.</summary>
        Attack,

        /// <summary><c>dam</c> — took a hit. Double-gated: 50 % at the call site, then 5 % inside.</summary>
        Dam,

        /// <summary><c>die</c> — the death rattle. Only in the corpse branch, and only when not overkilled.</summary>
        Die,

        /// <summary><c>ear</c> — heard something it could not see.</summary>
        Ear,

        /// <summary><c>find</c> — lost sight of a target it knows about.</summary>
        Find,

        /// <summary><c>vse</c> — "that's all": the target escaped, stand down.</summary>
        Vse,

        /// <summary><c>nope</c> — heard nothing after all (healthy unit).</summary>
        Nope,

        /// <summary><c>dont</c> — "don't": heard something after all (wounded unit).</summary>
        Dont,

        /// <summary><c>levit</c> — picked up by telekinesis / levitation.</summary>
        Levit,

        /// <summary><c>grenade</c> — a grenade is incoming and it is running.</summary>
        Grenade,

        /// <summary><c>tele</c> — something teleported into view.</summary>
        Tele,

        /// <summary><c>fire</c> — raider <c>tr=8</c>'s flamer variant, 30 % of its attack barks.</summary>
        Fire,

        /// <summary><c>expl</c> — raider <c>tr=9</c>'s explosive variant, 30 % of its attack barks.</summary>
        Expl,

        /// <summary><c>hi</c> — a pony greets the player.</summary>
        Hi,

        /// <summary><c>int</c> — an introduction.</summary>
        Int,

        /// <summary><c>full</c> — the player's container is full.</summary>
        Full,

        /// <summary><c>empty</c> — the player's container is empty.</summary>
        Empty,

        /// <summary><c>success</c> — a lockpick/hack/repair succeeded.</summary>
        Success,

        /// <summary><c>fail</c> — it failed.</summary>
        Fail,

        /// <summary><c>unlock</c> — a lock opened.</summary>
        Unlock,

        /// <summary><c>hack</c> — a terminal opened.</summary>
        Hack,

        /// <summary><c>zhopa</c> — the profane "we're screwed" line (literally "arse").</summary>
        Zhopa,

        /// <summary><c>donttoch</c> — the vendor's "don't touch that". Misspelled in the data on purpose.</summary>
        DontToch
    }

    /// <summary>
    /// The enemy speech rules — AS3 <c>Unit.replic</c> (<c>Unit.as:4813-4850</c>), the
    /// <c>UnitRaider</c> override (<c>UnitRaider.as:1467-1479</c>) and <c>Res.repText</c>'s text half
    /// (<c>Res.as:281-312</c>).
    ///
    /// <para><b>Why a separate rules type and not a method on the controller.</b> Three of the four
    /// pieces are pure arithmetic on numbers the caller already has — a cooldown, two rolls, a variant
    /// index — and the fourth is string surgery on the localisation document. None of them needs a
    /// <c>MonoBehaviour</c>, a scene or a camera, so all four are pinned by offline fixtures against
    /// the shipped document. The controller keeps only the wiring: which unit speaks, where the bubble
    /// appears, and when the tick runs.</para>
    ///
    /// <para><b>The gates are the interesting part, and they are not symmetric.</b> Read as written, the
    /// oracle is:</para>
    /// <list type="number">
    /// <item><description><c>sost != 1 || id_replic == "" || !loc.active</c> → return. A dead unit, a
    /// unit with no voice set, and a unit in an unloaded room are all silent.</description></item>
    /// <item><description><c>dam</c>: a <b>5 %</b> roll may zero the cooldown; <c>die</c>: a <b>50 %</b>
    /// roll may. Neither emits anything by itself — they only open the window.</description></item>
    /// <item><description>If the cooldown is still positive, return. This is what stops a burst of hits
    /// from printing a line per bullet.</description></item>
    /// <item><description><b>The new cooldown is assigned before the text is resolved</b>, so a
    /// <c>(speaker, act)</c> pair the document does not have still costs a full cooldown. That is the
    /// oracle's own order and it is reproduced rather than tidied: an enemy whose voice set lacks
    /// <c>vse</c> would otherwise bark again on the very next frame of the same state.</description></item>
    /// <item><description><c>attack</c> costs 50 + 0…100 ticks; everything else 110 + 0…150.</description></item>
    /// </list>
    ///
    /// <para><b>The raider override runs <i>before</i> all of that.</b> <c>UnitRaider.replic</c> checks
    /// <c>t_replic &lt;= 0 &amp;&amp; param1 == "attack"</c> and then, for <c>tr=8</c> (the flamer
    /// body) or <c>tr=9</c> (the grenadier), replaces the act with <c>fire</c> or <c>expl</c> on a
    /// <b>30 %</b> roll. So a flamer raider says "Buurn!" instead of "Prepare to die!" about a third of
    /// the time — and because the roll is inside the cooldown test, it is consumed only on a bark
    /// frame.</para>
    /// </summary>
    public static class ReplicRules
    {
        /// <summary>AS3 <c>this.t_replic = 50 + Math.random() * 100</c> (<c>Unit.as:4838</c>).</summary>
        public const int AttackCooldownBase = 50;

        /// <summary>The span of the attack cooldown — <c>Math.random() * 100</c>.</summary>
        public const int AttackCooldownSpan = 100;

        /// <summary>AS3 <c>this.t_replic = 110 + Math.random() * 150</c> (<c>Unit.as:4842</c>).</summary>
        public const int OtherCooldownBase = 110;

        /// <summary>The span of every other cooldown — <c>Math.random() * 150</c>.</summary>
        public const int OtherCooldownSpan = 150;

        /// <summary>
        /// The chance that a <c>dam</c> call opens the window — <c>if(this.isrnd(0.05)) this.t_replic = 0;</c>
        /// (<c>Unit.as:4822-4825</c>).
        /// </summary>
        public const float DamWindowChance = 0.05f;

        /// <summary>
        /// The chance that a <c>die</c> call opens the window — <c>if(this.isrnd()) this.t_replic = 0;</c>
        /// (<c>Unit.as:4826-4829</c>, and <c>isrnd()</c>'s default is <c>0.5</c>, <c>:4852</c>).
        /// </summary>
        public const float DieWindowChance = 0.5f;

        /// <summary>
        /// The chance the <i>damage path</i> calls <c>replic("dam")</c> at all —
        /// <c>if(this.hp &gt; 0 &amp;&amp; !this.player &amp;&amp; this.isrnd())</c>
        /// (<c>Unit.as:3962-3965</c>). Combined with <see cref="DamWindowChance"/>, one hit in forty
        /// produces a line; the double gate is what keeps a firefight from becoming a monologue.
        /// </summary>
        public const float DamageBarkChance = 0.5f;

        /// <summary>
        /// The chance a corpse death calls <c>replic("die")</c> — the inner <c>isrnd()</c> inside
        /// <c>replic</c> is <see cref="DieWindowChance"/>; the call site itself is ungated
        /// (<c>Unit.as:4335</c>).
        /// </summary>
        public const float DeathBarkChance = DieWindowChance;

        /// <summary>
        /// The raider variants that swap their attack line for a weapon-specific one, and the chance of
        /// doing so — <c>tr == 8</c> → <c>fire</c>, <c>tr == 9</c> → <c>expl</c>
        /// (<c>UnitRaider.as:1471-1477</c>).
        /// </summary>
        public const float RaiderVariantBarkChance = 0.3f;

        /// <summary>The flamer raider body — <c>raider8</c>.</summary>
        public const int RaiderFlamerTr = 8;

        /// <summary>The grenadier raider body — <c>raider9</c>.</summary>
        public const int RaiderGrenadierTr = 9;

        /// <summary>The player's voice set — <c>UnitPlayer.as:350</c> <c>id_replic = "pip"</c>.</summary>
        public const string PlayerSpeaker = "pip";

        /// <summary>
        /// The initial cooldown's span — <c>t_replic:int = Math.random() * 100 - 50</c>
        /// (<c>Unit.as:498</c>), i.e. a uniform start in <c>[-50, 50)</c>.
        ///
        /// <para><b>Negative on purpose.</b> A unit that spawns with a negative cooldown is <i>ready</i>
        /// to speak, so a room full of raiders does not all open with a line on the same frame but does
        /// not stay mute for the first two seconds either. The value is truncated by the <c>int</c>
        /// assignment, so the real range is <c>-50 … 49</c>.</para>
        /// </summary>
        public const int InitialCooldownBias = -50;

        /// <summary>Span of <see cref="InitialCooldown"/> — <c>Math.random() * 100</c>.</summary>
        public const int InitialCooldownSpan = 100;

        /// <summary>The act string for <paramref name="kind"/>, or <c>null</c> for
        /// <see cref="ReplicKind.None"/>.</summary>
        public static string KeyFor(ReplicKind kind)
        {
            switch (kind)
            {
                case ReplicKind.Neutral: return "neutral";
                case ReplicKind.Attack: return "attack";
                case ReplicKind.Dam: return "dam";
                case ReplicKind.Die: return "die";
                case ReplicKind.Ear: return "ear";
                case ReplicKind.Find: return "find";
                case ReplicKind.Vse: return "vse";
                case ReplicKind.Nope: return "nope";
                case ReplicKind.Dont: return "dont";
                case ReplicKind.Levit: return "levit";
                case ReplicKind.Grenade: return "grenade";
                case ReplicKind.Tele: return "tele";
                case ReplicKind.Fire: return "fire";
                case ReplicKind.Expl: return "expl";
                case ReplicKind.Hi: return "hi";
                case ReplicKind.Int: return "int";
                case ReplicKind.Full: return "full";
                case ReplicKind.Empty: return "empty";
                case ReplicKind.Success: return "success";
                case ReplicKind.Fail: return "fail";
                case ReplicKind.Unlock: return "unlock";
                case ReplicKind.Hack: return "hack";
                case ReplicKind.Zhopa: return "zhopa";
                case ReplicKind.DontToch: return "donttoch";
                default: return null;
            }
        }

        /// <summary>
        /// The inverse of <see cref="KeyFor"/> — the act string a script verb names, back to a kind.
        /// </summary>
        /// <remarks>
        /// <b>An unknown key is <see cref="ReplicKind.None"/>, not a throw.</b> The oracle's
        /// <c>replic(param1)</c> takes any string and simply finds no row for it, so an authored
        /// <c>&lt;act&gt;nonsense&lt;/act&gt;</c> is silent in the original too. Matching that is what
        /// lets the NPC <c>tell</c> verb stay a verb rather than becoming a validation site.
        /// </remarks>
        public static ReplicKind KindFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return ReplicKind.None;

            switch (key.Trim().ToLowerInvariant())
            {
                case "neutral": return ReplicKind.Neutral;
                case "attack": return ReplicKind.Attack;
                case "dam": return ReplicKind.Dam;
                case "die": return ReplicKind.Die;
                case "ear": return ReplicKind.Ear;
                case "find": return ReplicKind.Find;
                case "vse": return ReplicKind.Vse;
                case "nope": return ReplicKind.Nope;
                case "dont": return ReplicKind.Dont;
                case "levit": return ReplicKind.Levit;
                case "grenade": return ReplicKind.Grenade;
                case "tele": return ReplicKind.Tele;
                case "fire": return ReplicKind.Fire;
                case "expl": return ReplicKind.Expl;
                case "hi": return ReplicKind.Hi;
                case "int": return ReplicKind.Int;
                case "full": return ReplicKind.Full;
                case "empty": return ReplicKind.Empty;
                case "success": return ReplicKind.Success;
                case "fail": return ReplicKind.Fail;
                case "unlock": return ReplicKind.Unlock;
                case "hack": return ReplicKind.Hack;
                case "zhopa": return ReplicKind.Zhopa;
                case "donttoch": return ReplicKind.DontToch;
                default: return ReplicKind.None;
            }
        }

        /// <summary>
        /// The cooldown a bark of <paramref name="kind"/> sets — <c>50 + random*100</c> for
        /// <c>attack</c>, <c>110 + random*150</c> for everything else (<c>Unit.as:4836-4843</c>).
        /// </summary>
        /// <param name="draw01">A <c>[0,1)</c> draw from the caller's stream.</param>
        public static int CooldownFor(ReplicKind kind, float draw01)
        {
            int baseTicks = kind == ReplicKind.Attack ? AttackCooldownBase : OtherCooldownBase;
            int span = kind == ReplicKind.Attack ? AttackCooldownSpan : OtherCooldownSpan;

            // AS3 assigns a Number to an int field, which truncates toward zero. `Math.floor` here would
            // agree for every non-negative draw; the explicit truncation is what the field does.
            return baseTicks + (int)(Clamp01(draw01) * span);
        }

        /// <summary>
        /// The starting cooldown for a freshly spawned unit — <c>Math.random() * 100 - 50</c>
        /// (<c>Unit.as:498</c>), truncated to <c>[-50, 49]</c>.
        /// </summary>
        public static int InitialCooldown(float draw01) => (int)(Clamp01(draw01) * InitialCooldownSpan) + InitialCooldownBias;

        /// <summary>
        /// One call to <c>Unit.replic</c>, minus the text: applies the <c>dam</c>/<c>die</c> window
        /// rolls, then the cooldown gate, and writes the new cooldown.
        /// </summary>
        /// <param name="kind">The act being attempted.</param>
        /// <param name="cooldown">The unit's <c>t_replic</c>, in ticks. Updated in place.</param>
        /// <param name="windowRoll">Draw for the <c>dam</c>/<c>die</c> window. Ignored for other kinds.</param>
        /// <param name="cooldownRoll">Draw for the new cooldown, consumed only when a bark is emitted.</param>
        /// <returns><c>true</c> when the caller should resolve and show a line.</returns>
        /// <remarks>
        /// <para><b>The cooldown is written even when the text turns out to be missing.</b> That is the
        /// oracle's order — the assignment is above <c>Res.repText</c> — and the caller must therefore
        /// resolve the text <i>after</i> this returns true, not before.</para>
        ///
        /// <para><b>Both rolls are always drawn in the same order.</b> <c>dam</c> and <c>die</c> consume
        /// <paramref name="windowRoll"/> first; the cooldown draw comes second and only on a bark. A
        /// caller that skipped the window draw for other kinds would shift the whole stream relative to
        /// the oracle, which is exactly the kind of divergence that shows up as "the raiders talk in a
        /// different order every run" rather than as a bug.</para>
        /// </remarks>
        public static bool TryOpenWindow(ReplicKind kind, ref int cooldown, float windowRoll, float cooldownRoll)
        {
            switch (kind)
            {
                case ReplicKind.Dam:
                    if (Clamp01(windowRoll) < DamWindowChance) cooldown = 0;
                    break;
                case ReplicKind.Die:
                    if (Clamp01(windowRoll) < DieWindowChance) cooldown = 0;
                    break;
            }

            if (cooldown > 0) return false;

            cooldown = CooldownFor(kind, cooldownRoll);
            return true;
        }

        /// <summary>
        /// The raider family's act override — <c>UnitRaider.replic</c> (<c>:1467-1479</c>): a flamer body
        /// (<c>tr=8</c>) says a <c>fire</c> line and a grenadier (<c>tr=9</c>) an <c>expl</c> one, each on
        /// a 30 % roll, instead of its generic <c>attack</c> line.
        /// </summary>
        /// <param name="kind">The act the caller asked for.</param>
        /// <param name="tr">The unit's variant index — <c>UnitRaider.tr</c>.</param>
        /// <param name="draw01">A <c>[0,1)</c> draw. <b>Consume it only when this returns a different
        /// kind</b>: the oracle rolls inside <c>if(tr == 8 …)</c>, so a <c>tr=1</c> raider draws
        /// nothing here at all.</param>
        /// <remarks>
        /// <para><b>Only <c>attack</c> is overridable.</b> A flamer's <c>dam</c> line is its generic
        /// one; the oracle's two <c>if</c>s are both guarded by <c>param1 == "attack"</c>.</para>
        ///
        /// <para><b>The two branches are independent <c>if</c>s, not <c>else if</c>.</b> They cannot
        /// both fire because <c>tr</c> has one value, but writing it as <c>else if</c> would hide that
        /// fact and invite a third variant that silently shadows the second.</para>
        /// </remarks>
        public static ReplicKind OverrideRaiderKind(ReplicKind kind, int tr, float draw01)
        {
            if (kind != ReplicKind.Attack) return kind;
            if (tr != RaiderFlamerTr && tr != RaiderGrenadierTr) return kind;

            if (Clamp01(draw01) >= RaiderVariantBarkChance) return kind;

            return tr == RaiderFlamerTr ? ReplicKind.Fire : ReplicKind.Expl;
        }

        /// <summary>
        /// Pick and prepare one line — <c>Res.repText</c> (<c>Res.as:281-312</c>).
        /// </summary>
        /// <param name="table">The parsed <c>&lt;replic&gt;</c> block.</param>
        /// <param name="speaker">The unit's <c>id_replic</c>.</param>
        /// <param name="kind">The act.</param>
        /// <param name="draw01">Draw for the line index. Consumed only when the pair exists.</param>
        /// <param name="male">
        /// AS3 <c>msex</c> — <b>true unless <c>&lt;vis sex='w'&gt;</c></b> (<c>Unit.as:1279-1281</c>,
        /// default <c>true</c> at <c>:434</c>). Note the polarity: a unit with no <c>sex</c> attribute is
        /// treated as male for text substitution, which is why the port derives it from
        /// <c>gender == Female</c> rather than from <c>gender == Male</c>.
        /// </param>
        /// <param name="matureFilter">AS3 <c>World.w.matFilter</c> — <b>true means censored</b>, and a
        /// <c>m="1"</c> line is dropped entirely rather than cleaned.</param>
        /// <param name="playerName">Substituted for the first <c>@lp</c>; may be null or empty.</param>
        /// <param name="text">The prepared line, or <c>null</c>.</param>
        /// <returns><c>true</c> when a non-empty line was produced.</returns>
        /// <remarks>
        /// <para><b>The substitution order is the oracle's.</b> The <c>#male|female#</c> splice happens
        /// first and <c>@lp</c> second, so a player name that itself contains <c>#</c> or <c>|</c>
        /// cannot break the gender splice. Nothing in the shipped document uses <c>#</c> at all
        /// (measured: 0 of 982 lines) — the branch is ported because the oracle has it and a Russian
        /// export is where it would appear.</para>
        ///
        /// <para><b>One deliberate deviation: an empty name leaves the token in place.</b> AS3
        /// <c>s.replace("@lp", World.w.pers.persName)</c> with an unset <c>persName</c> substitutes the
        /// literal string <c>"undefined"</c> (an empty one substitutes nothing). Both are worse than
        /// showing <c>@lp</c>, and the caller always has a name by the time a unit can speak, so the
        /// token is left as-is instead of reproducing either. Recorded because it is a deviation, not
        /// an oversight.</para>
        ///
        /// <para><b>An out-of-range index is clamped, not thrown.</b> <c>draw01</c> is the caller's, and
        /// a generator that returns exactly <c>1.0</c> would otherwise index past the end; AS3's
        /// <c>Math.random()</c> never returns 1, so clamping reproduces it rather than inventing a
        /// crash.</para>
        /// </remarks>
        public static bool TryResolveText(
            ReplicTextTable table,
            string speaker,
            ReplicKind kind,
            float draw01,
            bool male,
            bool matureFilter,
            string playerName,
            out string text)
        {
            text = null;

            string act = KeyFor(kind);
            if (table == null || act == null) return false;
            if (!table.TryGetLines(speaker, act, out var lines)) return false;

            int index = (int)(Clamp01(draw01) * lines.Count);
            if (index < 0) index = 0;
            if (index >= lines.Count) index = lines.Count - 1;

            ReplicLine line = lines[index];

            // Res.as:299-302 — the filtered build drops the line rather than substituting anything.
            if (matureFilter && line.MatureOnly) return false;

            string s = line.Text;
            s = SpliceGender(s, male);

            // AS3 `s.replace("@lp", …)` replaces the FIRST occurrence only — String.replace with a string
            // pattern is not global in ActionScript 3. Ten shipped lines carry the token twice
            // ("Hi, @lp from the future - says @lp, that is, you…"), so `.Replace` (which is global)
            // would substitute where the oracle leaves the second token alone.
            int token = s.IndexOf("@lp", StringComparison.Ordinal);
            if (token >= 0 && !string.IsNullOrEmpty(playerName))
            {
                s = s.Substring(0, token) + playerName + s.Substring(token + 3);
            }

            if (string.IsNullOrEmpty(s)) return false;

            text = s;
            return true;
        }

        /// <summary>
        /// <c>#male|female#</c> → one half (<c>Res.as:303-309</c>): find the first <c>#</c>, the last
        /// <c>#</c>, split what is between on <c>|</c> and take index 0 for <c>msex</c> or 1 otherwise.
        /// </summary>
        /// <remarks>
        /// <para><b>A one-part splice falls back to that part.</b> AS3 would concatenate
        /// <c>undefined</c> into the string (<c>split("|")[1]</c> on a single part), which is a bug the
        /// data never exercises; producing the part instead is the same visible result without the
        /// word "undefined" in a speech bubble.</para>
        /// </remarks>
        private static string SpliceGender(string text, bool male)
        {
            int first = text.IndexOf('#');
            if (first < 0) return text;

            int last = text.LastIndexOf('#');
            if (last <= first) return text;

            string inner = text.Substring(first + 1, last - first - 1);
            string[] parts = inner.Split('|');

            string chosen = parts.Length >= 2
                ? (male ? parts[0] : parts[1])
                : parts[0];

            return text.Substring(0, first) + chosen + text.Substring(last + 1);
        }

        /// <summary>
        /// <c>isrnd(param1 = 0.5)</c> (<c>Unit.as:4852-4855</c>) — <c>Math.random() &lt; param1</c>.
        /// </summary>
        public static bool IsRandom(float draw01, float chance) => Clamp01(draw01) < chance;

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || value < 0f) return 0f;
            return value > 1f ? 1f : value;
        }
    }
}
