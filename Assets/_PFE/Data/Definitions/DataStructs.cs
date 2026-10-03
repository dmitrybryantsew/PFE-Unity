using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Supporting data structures for PFE data system.
    /// </summary>

    /// <summary>
    /// A unit's per-damage-type incoming-damage <b>multiplier</b>, AS3 <c>Unit.vulner</c>.
    ///
    /// <para><b>Neutral is 1, not 0.</b> This is a multiplier table, so a unit with no
    /// <c>&lt;vulner&gt;</c> element takes <c>×1</c> from everything. Its sibling
    /// <see cref="ResistTable"/> — an armour's <i>resistance</i> — looks like the same shape and has
    /// neutral <c>0</c>. The two must not be interchanged: handing an armour's resistances to a
    /// vulnerability consumer would read every unset field as immunity.</para>
    ///
    /// <para><b>Seventeen fields, sixteen of which XML can reach.</b> AS3's array is
    /// <c>Unit.kolVulners = 20</c> slots, but the <c>&lt;vulner&gt;</c> reader
    /// (<c>Unit.as:1209-1272</c>) tests exactly sixteen attributes — <c>bul</c>, <c>blade</c>,
    /// <c>phis</c>, <c>fire</c>, <c>expl</c>, <c>laser</c>, <c>plasma</c>, <c>venom</c>, <c>emp</c>,
    /// <c>spark</c>, <c>acid</c>, <c>cryo</c>, <c>poison</c>, <c>bleed</c>, <c>fang</c>, <c>pink</c>.
    /// Slots 15/17/18 (<c>bale</c>, <c>psy</c>, <c>astro</c>) are therefore <b>unreachable from
    /// data</b> and are deliberately absent. <see cref="necro"/> is present because AS3 does use
    /// <c>D_NECRO</c> — but from <i>code</i> (<c>Pers.as:1425</c> zeroes it for the undead), never from
    /// <c>&lt;vulner&gt;</c>.</para>
    ///
    /// <para><b>Attributes the data writes and AS3 ignores.</b> <c>AllData.as</c> uses <c>bullet</c>
    /// three times, <c>necro</c> once and <c>necr</c> once inside <c>&lt;vulner&gt;</c>; AS3 reads none
    /// of them — it reads <c>bul</c>, and <c>@necro</c> only on an armour's <c>&lt;upd&gt;</c>
    /// (<c>Armor.as:253</c>). They are rejected rather than honoured, which is the same rule
    /// <see cref="ResistTable"/> applies to <c>dark='0.2'</c>, and the reason
    /// <see cref="SetVulnerabilityByAs3Attribute"/> is the only way in from XML.</para>
    /// </summary>
    [System.Serializable]
    public struct VulnerabilityData
    {
        [Header("Physical Damage")]
        public float bullet;      // PhysicalBullet
        public float blade;       // Blade
        public float phis;        // PhysicalMelee

        [Header("Elemental Damage")]
        public float fire;        // Fire
        public float expl;        // Explosive
        public float laser;       // Laser
        public float plasma;      // Plasma
        public float spark;       // Spark
        public float acid;        // Acid
        public float cryo;        // Cryo

        [Header("Biological Damage")]
        public float venom;       // Venom
        public float poison;      // Poison
        public float bleed;       // Bleed
        public float fang;        // Fang

        [Header("Special Damage")]
        public float emp;         // EMP
        public float pink;        // Pink
        public float necro;       // Necrotic

        public VulnerabilityData(float defaultValue = 1f)
        {
            bullet = blade = phis = fire = expl = laser = plasma = spark = defaultValue;
            acid = cryo = venom = poison = bleed = fang = emp = pink = necro = defaultValue;
        }

        /// <summary>
        /// AS3's neutral unit: every multiplier <c>1</c> — <b>except <c>emp</c>, which is <c>0</c></b>.
        ///
        /// <para>The exception is AS3's, not a choice. <c>Unit.as:583-590</c> fills the whole array with
        /// <c>1</c> and then unconditionally assigns <c>vulner[D_EMP] = 0</c>; the per-frame reset does
        /// the same (<c>Pers.as:946-950</c>). So <b>every unit in the game is immune to EMP until
        /// something grants it</b>, and <c>0</c> is the identity for "this hit does nothing" —
        /// <c>Unit.damage():3527-3530</c> multiplies by it, and <c>udarBullet():4075</c> returns
        /// <c>-1</c> ("passed through, not a hit") when it is <c>&lt;= 0</c>. An all-ones default would
        /// silently hand every unit EMP damage the oracle denies it.</para>
        ///
        /// <para>A property rather than a <c>static readonly</c> field: <see cref="VulnerabilityData"/>
        /// is a mutable struct, and a shared instance would be one stray write away from corrupting
        /// every unit.</para>
        /// </summary>
        public static VulnerabilityData Neutral
        {
            get
            {
                var neutral = new VulnerabilityData(1f);
                neutral.emp = 0f;
                return neutral;
            }
        }

        /// <summary>
        /// The identity multiplier: every slot <c>1</c>, <b>including <c>emp</c></b> — so applying it
        /// changes nothing.
        ///
        /// <para>Distinct from <see cref="Neutral"/> on purpose, and the distinction is load-bearing.
        /// <see cref="Neutral"/> is AS3's <i>baseline for a unit</i>, and it carries <c>emp = 0</c>, so
        /// it <b>is</b> a change. This is what the damage path multiplies by when the vulnerability
        /// term is switched off — i.e. the pre-A7 behaviour, where the term did not exist at all.
        /// Using <see cref="Neutral"/> for that would silently make every unit EMP-immune the moment
        /// the term was disabled, which is exactly backwards.</para>
        /// </summary>
        public static VulnerabilityData Unmodified => new VulnerabilityData(1f);

        /// <summary>
        /// Get vulnerability multiplier for damage type.
        /// </summary>
        public float GetVulnerability(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.PhysicalBullet: return bullet;
                case DamageType.Blade: return blade;
                case DamageType.PhysicalMelee: return phis;
                case DamageType.Fire: return fire;
                case DamageType.Explosive: return expl;
                case DamageType.Laser: return laser;
                case DamageType.Plasma: return plasma;
                case DamageType.Spark: return spark;
                case DamageType.Acid: return acid;
                case DamageType.Cryo: return cryo;
                case DamageType.Venom: return venom;
                case DamageType.Poison: return poison;
                case DamageType.Bleed: return bleed;
                case DamageType.Fang: return fang;
                case DamageType.EMP: return emp;
                case DamageType.Pink: return pink;
                case DamageType.Necrotic: return necro;
                default: return 1f;
            }
        }

        /// <summary>
        /// Set the multiplier for a damage type.
        ///
        /// <para><b>Silently does nothing for the four types AS3's <c>&lt;vulner&gt;</c> reader cannot
        /// reach</b> — <see cref="DamageType.Balefire"/>, <see cref="DamageType.Psionic"/>,
        /// <see cref="DamageType.Astral"/>, plus the out-of-range
        /// <see cref="DamageType.Internal"/>/<see cref="DamageType.FriendlyFire"/>. Prefer
        /// <see cref="SetVulnerabilityByAs3Attribute"/> when the input is XML, so a miss is reported
        /// rather than dropped.</para>
        /// </summary>
        public void SetVulnerability(DamageType damageType, float value)
        {
            switch (damageType)
            {
                case DamageType.PhysicalBullet: bullet = value; break;
                case DamageType.Blade: blade = value; break;
                case DamageType.PhysicalMelee: phis = value; break;
                case DamageType.Fire: fire = value; break;
                case DamageType.Explosive: expl = value; break;
                case DamageType.Laser: laser = value; break;
                case DamageType.Plasma: plasma = value; break;
                case DamageType.Spark: spark = value; break;
                case DamageType.Acid: acid = value; break;
                case DamageType.Cryo: cryo = value; break;
                case DamageType.Venom: venom = value; break;
                case DamageType.Poison: poison = value; break;
                case DamageType.Bleed: bleed = value; break;
                case DamageType.Fang: fang = value; break;
                case DamageType.EMP: emp = value; break;
                case DamageType.Pink: pink = value; break;
                case DamageType.Necrotic: necro = value; break;
            }
        }

        /// <summary>
        /// Set the multiplier by AS3's own <c>&lt;vulner&gt;</c> attribute name (<c>"bul"</c>,
        /// <c>"phis"</c>, <c>"pink"</c>, …).
        ///
        /// <para>The importer uses this so the XML attribute names live in one place instead of in a
        /// switch at the call site — the split <see cref="ResistTable.SetResistByAs3Attribute"/>
        /// established. It also makes the mapping unit-testable: <c>PFE.Tests</c> references
        /// <c>PFE.Core</c> but not <c>PFE.Editor</c>.</para>
        ///
        /// <para><b>Why this returns <c>bool</c> instead of being a <c>void</c> switch.</b> The previous
        /// mapping lived in the importer and ended in <c>default: return DamageType.PhysicalMelee</c> —
        /// so <c>bul</c>, the attribute AS3 actually reads and the one <c>AllData.as</c> uses 28 times,
        /// fell through and was written into <c>phis</c> instead. A silent fallback turns a typo into
        /// plausible-looking data.</para>
        /// </summary>
        /// <returns>
        /// <c>false</c> for any attribute AS3's <c>&lt;vulner&gt;</c> reader does not test — including
        /// <c>bullet</c>, <c>necro</c> and <c>necr</c>, which <c>AllData.as</c> writes but the oracle
        /// ignores.
        /// </returns>
        public bool SetVulnerabilityByAs3Attribute(string attributeName, float value)
        {
            switch (attributeName)
            {
                case "bul":    bullet = value; return true;
                case "blade":  blade  = value; return true;
                case "phis":   phis   = value; return true;
                case "fire":   fire   = value; return true;
                case "expl":   expl   = value; return true;
                case "laser":  laser  = value; return true;
                case "plasma": plasma = value; return true;
                case "venom":  venom  = value; return true;
                case "emp":    emp    = value; return true;
                case "spark":  spark  = value; return true;
                case "acid":   acid   = value; return true;
                case "cryo":   cryo   = value; return true;
                case "poison": poison = value; return true;
                case "bleed":  bleed  = value; return true;
                case "fang":   fang   = value; return true;
                case "pink":   pink   = value; return true;
                default:       return false;
            }
        }

        /// <summary>
        /// Fold an armour's per-type <c>resist</c> into this multiplier table — AS3
        /// <c>Pers.armorParameters()</c> (<c>:2064-2070</c>):
        /// <c>gg.vulner[_loc2_] *= 1 - param1.resist[_loc2_]</c>.
        ///
        /// <para><b>Returns a new table rather than mutating this one.</b> AS3 mutates
        /// <c>gg.vulner</c> in place, which is safe there only because every path into it is preceded by
        /// <c>defaultParams()</c> re-zeroing the array (<c>:946-950</c>). Without that reset, calling
        /// this twice multiplies the first plate's resistance in twice — and the bug is silent, because
        /// the second plate's numbers still look plausible. Keeping the fold pure means the caller has
        /// to supply the baseline, so "reset, then re-apply" is the only expressible shape and swapping
        /// a plate <b>replaces</b> its contribution instead of compounding it.</para>
        ///
        /// <para><b>Three slots are deliberately untouched.</b> AS3's loop runs over all
        /// <c>Unit.kolVulners = 20</c> slots, but its <c>resist</c> array only ever holds the thirteen
        /// XML attributes plus <c>pink</c>. <c>poison</c>, <c>bleed</c> and <c>emp</c> have no resist
        /// slot, so AS3 multiplies them by <c>1 - 0 = 1</c> — a no-op. They are left alone here for the
        /// same reason <see cref="ResistTable.GetResist"/> returns <c>0</c> for them: the no-op is the
        /// oracle's behaviour, not an omission. In particular <c>emp</c> keeps the <c>0</c> that
        /// <see cref="Neutral"/> put there, so a unit stays EMP-immune through an armour change.</para>
        ///
        /// <para><b><c>pink</c> is the one that moves the wrong way.</b> Every <c>tip == 1</c> body
        /// armour carries <c>resist[PINK] = -0.5</c> from its constructor (<c>Armor.as:180-183</c>), so
        /// the fold gives <c>×1.5</c> — body armour makes its wearer take <i>more</i> pink damage. That
        /// is the oracle, and it is why this multiplies by <c>1 - resist</c> and not by
        /// <c>resist</c>.</para>
        /// </summary>
        /// <param name="resists">
        /// The equipped armour's resistance table. <c>default</c> — an all-zero table — is a no-op.
        /// </param>
        public VulnerabilityData WithResist(in ResistTable resists)
        {
            VulnerabilityData folded = this;

            folded.bullet *= 1f - resists.bullet;
            folded.blade  *= 1f - resists.blade;
            folded.phis   *= 1f - resists.physical;
            folded.fire   *= 1f - resists.fire;
            folded.expl   *= 1f - resists.explosive;
            folded.laser  *= 1f - resists.laser;
            folded.plasma *= 1f - resists.plasma;
            folded.spark  *= 1f - resists.spark;
            folded.acid   *= 1f - resists.acid;
            folded.cryo   *= 1f - resists.cryo;
            folded.venom  *= 1f - resists.venom;
            folded.fang   *= 1f - resists.fang;
            folded.necro  *= 1f - resists.necrotic;
            folded.pink   *= 1f - resists.pink;

            return folded;
        }

        /// <summary>
        /// AS3's <c>&lt;vulner&gt;</c> attribute names, in the order <c>Unit.as:1209-1272</c> tests
        /// them. Exposed so the importer and its tests agree on one list rather than two that can drift.
        /// </summary>
        public static readonly string[] As3AttributeNames =
        {
            "bul", "blade", "phis", "fire", "expl", "laser", "plasma", "venom",
            "emp", "spark", "acid", "cryo", "poison", "bleed", "fang", "pink",
        };
    }

    /// <summary>
    /// Weapon chance entry for unit inventory.
    /// Defines which weapons a unit can carry and with what probability.
    /// </summary>
    [System.Serializable]
    public struct WeaponChance
    {
        public string weaponId;     // Weapon ID from WeaponData
        [Range(0f, 1f)]
        public float chance;        // Probability (0-1)
        public int difficulty;      // Required difficulty level

        public WeaponChance(string id, float ch, int dif = 0)
        {
            weaponId = id;
            chance = ch;
            difficulty = dif;
        }
    }

    /// <summary>
    /// One animation state of a unit — the port of AS3's <c>BlitAnim</c> (<c>fe/serv/BlitAnim.as</c>).
    ///
    /// <para><b>A state is a ROW of the unit's sprite sheet.</b> The unit's sheet comes from
    /// <c>&lt;vis blit='sprX' sprX='W' [sprY='H']/&gt;</c>; it is a grid of <c>sprX</c> x
    /// (<c>sprY</c>, defaulting to <c>sprX</c>) cells, 24 columns wide. This struct names one row of
    /// that grid and how to walk along it. The cell for the current frame is
    /// <c>(frame * sprX, row * sprY, sprX, sprY)</c>.</para>
    ///
    /// <para><b>Field names, and why they are not AS3's.</b> AS3's <c>BlitAnim</c> fields are
    /// <c>id</c>, <c>firstf</c>, <c>maxf</c>, <c>retf</c>, <c>replay</c>, <c>stab</c>, <c>df</c>, and
    /// the XML attributes are the terse <c>y/len/ff/rf/df/rep/stab</c>. The previous revision of this
    /// struct kept the terse names <i>and</i> documented two of them wrongly — <c>ff</c> was labelled
    /// "frame skip (speed)" when <c>BlitAnim.as:3688</c> assigns it to <c>firstf</c>, the first frame
    /// <i>index</i> of the row, and <c>rf</c> was labelled "reverse frame" when <c>:3696</c> assigns it
    /// to <c>retf</c>, the index <c>replay</c> restarts from. Neither was read anywhere, so the
    /// comments were the only thing a reader had. They are now named for what the oracle does.</para>
    ///
    /// <para><b>The stepping rule</b> (<c>BlitAnim.as:3715-3733</c>):
    /// <c>if (isStatic) return; if (frame &lt; firstFrame + length - 1) frame += frameStep;
    /// else if (replay) frame = returnFrame; else stopped = true;</c>. So <c>length</c> is the count of
    /// usable cells, the cursor never leaves <c>[firstFrame, firstFrame + length - 1]</c>, and a
    /// <c>replay</c> state wraps to <c>returnFrame</c> rather than to <c>firstFrame</c>.</para>
    ///
    /// <para><b><c>isStatic</c> means the state does not advance itself</b> — <c>step()</c> returns
    /// immediately — and is driven externally by <c>setStab(progress)</c>, which sets
    /// <c>frame = length * clamp(progress, 0, 0.999)</c> (<c>:3741-3751</c>). That is how a
    /// <c>stab='1'</c> state like <c>jump</c> shows a pose chosen from the jump's own progress rather
    /// than from a timer. It is <b>not</b> "a stable/idle animation", which is what the old comment
    /// implied.</para>
    /// </summary>
    [System.Serializable]
    public struct AnimationFrame
    {
        /// <summary>Sheet row index. AS3 <c>@y</c> → <c>BlitAnim.id</c> (<c>BlitAnim.as:3684</c>).</summary>
        public int row;

        /// <summary>Usable cell count in the row. AS3 <c>@len</c> → <c>maxf</c> (default 1).</summary>
        public int length;

        /// <summary>
        /// First cell index of the row. AS3 <c>@ff</c> → <c>firstf</c>. <b>Not</b> a speed — the
        /// cursor starts here and stops before <c>firstFrame + length</c>.
        /// </summary>
        public int firstFrame;

        /// <summary>
        /// Cell index a <see cref="replay"/> state wraps back to. AS3 <c>@rf</c> → <c>retf</c>.
        /// <b>Not</b> a reverse flag.
        /// </summary>
        public int returnFrame;

        /// <summary>Cells advanced per step. AS3 <c>@df</c> (default 1).</summary>
        public float frameStep;

        /// <summary>Loop. AS3 <c>@rep</c>, which is <b>presence</b>-tested, so <c>rep='0'</c> is true.</summary>
        public bool replay;

        /// <summary>Do not self-advance; driven by <c>setStab</c>. AS3 <c>@stab</c>, also presence-tested.</summary>
        public bool isStatic;

        /// <summary>True when this state actually names cells in the sheet.</summary>
        public bool HasFrames => length > 0;

        /// <summary>
        /// The cell index to draw for a normalised <paramref name="progress"/> 0..1 — AS3
        /// <c>BlitAnim.setStab</c> (<c>BlitAnim.as:3741-3751</c>):
        /// <c>f = maxf * clamp(progress, 0, 0.999)</c>.
        ///
        /// <para><b><paramref name="firstFrame"/> is deliberately NOT added.</b> The oracle writes
        /// <c>this.f = this.maxf * param1</c> and ignores <c>firstf</c> entirely, even though
        /// <c>step()</c> otherwise keeps the cursor inside <c>[firstf, firstf + maxf - 1]</c>. That is an
        /// inconsistency in the source, and a port that quietly "fixed" it would draw a different cell
        /// from the game. It is unobservable in this data set either way: all <b>eight</b> <c>stab</c>
        /// rows in <c>AllData.as</c> are <c>jump</c> — <c>y='1' len='14'</c>, <c>y='3' len='16'</c> six
        /// times, <c>y='5' len='16'</c> — and none of them carries <c>ff</c>, so <c>firstf</c> is 0 for
        /// every one.</para>
        /// </summary>
        public int FrameAtProgress(float progress)
        {
            float p = progress < 0f ? 0f : (progress > 0.999f ? 0.999f : progress);
            return (int)(length * p);
        }

        /// <summary>
        /// <see cref="frameStep"/> with AS3's declared default applied — <c>BlitAnim.as:3687</c>
        /// <c>public var df:Number = 1;</c>.
        ///
        /// <para><b>Why the default lives here rather than on the field.</b> AS3 gets it from a field
        /// <i>initializer</i>, which every <c>BlitAnim</c> receives for free. This is a serializable
        /// struct, so a caller that builds one by hand — a test, a future spawner — gets <c>0</c>, and a
        /// zero step is a state AS3 reaches only by explicitly writing <c>df='0'</c>, which no row in
        /// <c>AllData.as</c> does. Reading <c>0</c> as the declared default keeps the port inside AS3's
        /// reachable set: <c>step()</c>'s bound test would stay true forever on a zero step, so the state
        /// would stall on its first cell with no error anywhere. <c>UnitAnimationParser</c> already writes
        /// <c>1</c> for an absent <c>df</c>; this is the belt to that pair of braces.</para>
        /// </summary>
        public float EffectiveFrameStep => frameStep > 0f ? frameStep : 1f;

        /// <summary>
        /// The cell index to draw for a cursor value — AS3 <c>Unit.blit(param1:int, param2:int)</c>
        /// (<c>Unit.as:2863-2869</c>), whose body is <c>blitRect.x = param2 * blitX</c>.
        ///
        /// <para><b>The cursor is fractional and the cell is not.</b> <c>BlitAnim.f</c> is an untyped
        /// <c>Number</c> advanced by <c>df</c>, and the data really does set <c>df</c> fractionally:
        /// <c>df='0.5'</c> six times, plus <c>df='0.4'</c> and <c>df='0.25'</c>. AS3 then hands that float
        /// to a parameter declared <c>int</c>, so it is <b>truncated toward zero</b>, not rounded — a
        /// <c>df='0.5'</c> row holds cell 1 for two ticks before moving to 2. Keeping the cursor a
        /// <c>float</c> and casting only here is the point: an <c>int</c> cursor would add <c>0.5</c> to
        /// itself forever and never leave cell 0.</para>
        ///
        /// <para><b>Not clamped</b>, like <see cref="UnitSheetLayout.PivotFor"/> — AS3 computes a rect
        /// outside the sheet without complaint, so the caller bounds-checks against the sliced array and
        /// reports it. The importer already lists the 11 states whose cells fall outside their own
        /// sheet.</para>
        /// </summary>
        public int CellFor(float frame) => (int)frame;

        /// <summary>
        /// Advance the cursor one animation frame — AS3 <c>BlitAnim.step()</c>
        /// (<c>BlitAnim.as:3715-3733</c>).
        ///
        /// <para>The oracle's body, line for line:</para>
        /// <code>
        /// if (this.stab) return;                       // isStatic — driven by setStab instead
        /// if (this.f &lt; this.firstf + this.maxf - 1) this.f += this.df;
        /// else if (this.replay) this.f = this.retf;    // retf, NOT firstf
        /// else this.st = true;                         // stopped, and it stays stopped
        /// </code>
        ///
        /// <para>Three details a "reasonable" rewrite loses. The bound is
        /// <c>firstFrame + length - 1</c>, i.e. <b><c>length</c> is a cell count, not an exclusive
        /// end</b>, so the cursor is only ever compared against the last <i>usable</i> index. A replay
        /// wraps to <c>returnFrame</c> — the data uses <c>rf='1'</c> nineteen times — so wrapping to
        /// <c>firstFrame</c> would replay the wind-up cells a row deliberately skips. And
        /// <c>stopped</c> is sticky: only <see cref="Restart"/> clears it.</para>
        ///
        /// <para><paramref name="frame"/> is a <c>float</c> on purpose — see <see cref="CellFor"/>.</para>
        /// </summary>
        public void Step(ref float frame, ref bool stopped)
        {
            if (isStatic)
            {
                return;
            }

            if (frame < firstFrame + length - 1)
            {
                frame += EffectiveFrameStep;
            }
            else if (replay)
            {
                frame = returnFrame;
            }
            else
            {
                stopped = true;
            }
        }

        /// <summary>
        /// Rewind the cursor to the row's first cell and clear <c>stopped</c> — AS3
        /// <c>BlitAnim.restart()</c> (<c>BlitAnim.as:3735-3739</c>):
        /// <c>this.st = false; this.f = this.firstf;</c>.
        ///
        /// <para>Called when the unit's state <i>changes</i>, never every tick — the oracle's draw loop
        /// restarts only when <c>animState != animState2</c> (<c>UnitAlicorn.as:317</c>). Restarting every
        /// tick would pin every animation to its first cell.</para>
        /// </summary>
        public void Restart(ref float frame, ref bool stopped)
        {
            frame = firstFrame;
            stopped = false;
        }
    }

    /// <summary>
    /// Every animation state a unit can be in — the port of AS3's <c>Unit.anims</c>, the array
    /// <c>Unit.as:1430-1436</c> fills as <c>anims[xbl.@id] = new BlitAnim(xbl)</c>.
    ///
    /// <para><b>The ids are the oracle's, and they are mostly Russian.</b> There are 176
    /// <c>&lt;blit&gt;</c> rows in <c>AllData.as</c> across 18 distinct ids. Five of them do not
    /// translate one-to-one, so <see cref="TrySet"/> owns that mapping in one place:</para>
    /// <list type="table">
    /// <item><term>plav</term><description>плавать — swim</description></item>
    /// <item><term>polz</term><description>ползать — crawl</description></item>
    /// <item><term>laz</term><description>лазать — climb</description></item>
    /// <item><term>pre</term><description>pre-attack</description></item>
    /// </list>
    /// <para><c>derg</c>, <c>super</c> and <c>attack</c> have no field here and are reported by the
    /// importer rather than dropped silently. <see cref="drag"/> and <see cref="transform"/> appear in
    /// no oracle row, so they are never populated — kept so the field set is not silently narrowed.
    /// </para>
    ///
    /// <para><b>Where the states come from — and why the parent chain matters.</b> The oracle splits
    /// animation data across two nodes and joins them in the controller:</para>
    ///
    /// <list type="number">
    /// <item>The <b>family node</b> (<c>raider</c>, <c>zombie</c>, <c>alicorn</c>, <c>hellhound</c>,
    /// <c>ant</c>, <c>bloat</c>…) declares the <c>&lt;blit&gt;</c> rows but names <b>no sheet</b> —
    /// <c>&lt;vis noise='600' visdam='1'/&gt;</c>.</item>
    /// <item>The <b>variant node</b> (<c>raider5</c>, <c>zombie3</c>, <c>alicorn1</c>…) declares the sheet
    /// — <c>&lt;vis blit='sprRaider5' sprX='120'/&gt;</c> — and usually <b>no blit rows at all</b>. When it
    /// does carry rows (<c>zombie1..7,9</c> each carry one <c>pre</c> row) they are a <b>delta</b>.</item>
    /// </list>
    ///
    /// <para>The join is the controller's double call — <c>UnitAlicorn.as:233-234</c>:
    /// <c>super.getXmlParam("alicorn"); super.getXmlParam();</c>. The first pass reads the family node
    /// (stats <i>and</i> blits); the second reads the unit's own node and overwrites. Because
    /// <c>Unit.as:1430</c> only assigns <c>anims[xbl.@id]</c> for rows it actually finds, the second pass
    /// <b>overlays per id</b> rather than replacing the set. <c>UnitRaider.as:302</c> does the same with a
    /// dynamic key, <c>super.getXmlParam(this.parentId)</c>, where subclasses set
    /// <c>parentId</c> to <c>"encl"</c>/<c>"merc"</c>/<c>"zebra"</c>/<c>"slaver"</c>/<c>"ranger"</c>.</para>
    ///
    /// <para><b>In every case the family id is exactly the unit's own <c>parent='…'</c> attribute</b>
    /// (<c>raider5 parent='raider'</c>, <c>zombie3 parent='zombie'</c>, <c>alicorn1 parent='alicorn'</c>),
    /// so the importer can reproduce the join from data alone without a controller→template table. That
    /// resolution is done <b>at import time and baked into the asset</b>, so the runtime needs no
    /// inheritance logic — the same choice already made for <c>fraction</c>.</para>
    ///
    /// <para><b>A unit with neither blits nor a sheet is not broken.</b> 24 of the ids rooms place have
    /// no blit rows — including the most-placed unit in the game (<c>slime</c>, 152 placements),
    /// <c>turret</c> (97) and <c>training</c> (6). Those are drawn by a <c>&lt;vis vclass='visualX'/&gt;</c>
    /// DisplayObject instead, and a handful (<c>training</c>, <c>npc</c>, <c>spectre</c>, <c>thunderhead</c>)
    /// declare no <c>&lt;vis&gt;</c> at all because their controller assigns the visual in code —
    /// <c>UnitTrain.as:41-49</c> picks <c>visualTrainArmor</c> when <c>tr == 1</c>, else
    /// <c>visualTrain</c>. See <c>TOPIC_unit_visuals_and_animation.md</c>.</para>
    /// </summary>
    [System.Serializable]
    public class AnimationSet
    {
        public AnimationFrame stay;
        public AnimationFrame walk;
        public AnimationFrame trot;
        public AnimationFrame run;
        public AnimationFrame jump;
        public AnimationFrame die;
        public AnimationFrame death;
        public AnimationFrame fall;
        public AnimationFrame sit;
        public AnimationFrame fly;
        public AnimationFrame dig;

        /// <summary>AS3 <c>plav</c>.</summary>
        public AnimationFrame swim;

        /// <summary>AS3 <c>polz</c>.</summary>
        public AnimationFrame crawl;

        /// <summary>AS3 <c>laz</c>.</summary>
        public AnimationFrame climb;

        /// <summary>AS3 <c>pre</c>.</summary>
        public AnimationFrame preAttack;

        /// <summary>No oracle id — never populated.</summary>
        public AnimationFrame drag;

        /// <summary>No oracle id — never populated.</summary>
        public AnimationFrame transform;

        /// <summary>
        /// Store the state named by an AS3 <c>&lt;blit id='…'&gt;</c>. Returns <c>false</c> for an id
        /// this set has no field for, so the caller can report it instead of losing it.
        /// </summary>
        public bool TrySet(string as3Id, AnimationFrame frame)
        {
            if (!IsMapped(as3Id))
            {
                return false;
            }

            Assign(as3Id, frame);
            return true;
        }

        /// <summary>True when <paramref name="as3Id"/> has a field in this set.</summary>
        public static bool IsMapped(string as3Id)
        {
            return System.Array.IndexOf(As3Ids, as3Id) >= 0;
        }

        /// <summary>
        /// The frame for an AS3 id, or an empty frame when the id has no field here.
        ///
        /// <para><b>Read-only.</b> An earlier revision wrote
        /// <c>TrySet(as3Id, default) ? GetMapped(as3Id) : default</c>, which used <c>TrySet</c> as a
        /// predicate — but <c>TrySet</c> assigns, so every read silently blanked the state it was reading.
        /// The mapped check is now its own predicate.</para>
        /// </summary>
        public AnimationFrame Get(string as3Id)
        {
            return IsMapped(as3Id) ? GetMapped(as3Id) : default;
        }

        void Assign(string as3Id, AnimationFrame frame)
        {
            switch (as3Id)
            {
                case "stay": stay = frame; break;
                case "walk": walk = frame; break;
                case "trot": trot = frame; break;
                case "run": run = frame; break;
                case "jump": jump = frame; break;
                case "die": die = frame; break;
                case "death": death = frame; break;
                case "fall": fall = frame; break;
                case "sit": sit = frame; break;
                case "fly": fly = frame; break;
                case "dig": dig = frame; break;
                case "plav": swim = frame; break;
                case "polz": crawl = frame; break;
                case "laz": climb = frame; break;
                case "pre": preAttack = frame; break;
            }
        }

        /// <summary>Every AS3 id this set can hold.</summary>
        public static readonly string[] As3Ids =
        {
            "stay", "walk", "trot", "run", "jump", "die", "death", "fall", "sit", "fly", "dig",
            "plav", "polz", "laz", "pre"
        };

        AnimationFrame GetMapped(string as3Id)
        {
            switch (as3Id)
            {
                case "stay": return stay;
                case "walk": return walk;
                case "trot": return trot;
                case "run": return run;
                case "jump": return jump;
                case "die": return die;
                case "death": return death;
                case "fall": return fall;
                case "sit": return sit;
                case "fly": return fly;
                case "dig": return dig;
                case "plav": return swim;
                case "polz": return crawl;
                case "laz": return climb;
                case "pre": return preAttack;
                default: return default;
            }
        }
    }

    /// <summary>
    /// Weapon tier definition for upgradeable weapons.
    /// 21 fields defining weapon stats at each tier.
    /// </summary>
    [System.Serializable]
    public struct WeaponTier
    {
        public int tier;                    // Tier number (1-4)

        [Header("Combat Stats")]
        public float rapid;                  // Fire rate
        public float damage;                 // Base damage
        public float tipDamage;              // Special damage
        public float pierce;                 // Armor piercing
        public float knockback;              // Knockback
        public float destroy;                // Destroy chance
        public float precision;              // Accuracy
        public float crit;                   // Crit chance
        public float critDamage;             // Crit damage

        [Header("Probability")]
        public float probiv;                 // Armor piercing chance

        [Header("Special")]
        public float fireDamage;             // Additional fire damage

        public WeaponTier(int t)
        {
            tier = t;
            rapid = damage = tipDamage = pierce = knockback = destroy = 0f;
            precision = crit = critDamage = probiv = fireDamage = 0f;
        }
    }

    /// <summary>
    /// Weapon effect (additional effects beyond damage).
    /// </summary>
    [System.Serializable]
    public struct WeaponEffect
    {
        public string effectId;              // Effect ID from EffectData
        [Range(0f, 1f)]
        public float chance;                 // Trigger chance
        public float damage;                 // Effect damage
        public float duration;               // Effect duration

        public WeaponEffect(string id, float ch, float dmg = 0f)
        {
            effectId = id;
            chance = ch;
            damage = dmg;
            duration = 0f;
        }
    }

    /// <summary>
    /// Stat modifier for skills and perks.
    /// </summary>
    [System.Serializable]
    public struct StatModifier
    {
        public string statId;                // Stat ID (e.g., "maxhp", "allDamMult")
        public ModifierType type;            // Add, Multiply, Set
        public ModifierTarget target;        // Player, Pers, Unit

        [Header("Values per rank/level")]
        public float v0;                     // Base value
        public float v1;                     // Level 1 / Rank 1
        public float v2;                     // Level 2 / Rank 2
        public float v3;                     // Level 3 / Rank 3
        public float v4;                     // Level 4 / Rank 4
        public float v5;                     // Level 5 / Rank 5

        public float vd;                     // Delta (linear increase per level)

        /// <summary>
        /// Get value for specific level/rank.
        /// </summary>
        public float GetValueForLevel(int level)
        {
            if (level == 0) return v0;
            if (level == 1) return v1;
            if (level == 2) return v2;
            if (level == 3) return v3;
            if (level == 4) return v4;
            if (level == 5) return v5;

            // For higher levels, use delta if available
            if (vd != 0)
                return v0 + vd * level;

            return v0;
        }
    }

    /// <summary>
    /// Text variable for dynamic UI text.
    /// </summary>
    [System.Serializable]
    public struct TextVariable
    {
        public string key;                   // Variable name (s1, s2, etc.)
        public string value;                 // Text value
    }

    /// <summary>
    /// Perk requirement definition.
    /// </summary>
    [System.Serializable]
    public struct PerkRequirement
    {
        public RequirementType type;
        public string skillId;               // For skill requirements
        public int level;                    // Base level required
        public int levelDelta;               // Additional levels per perk rank (dlvl)

        /// <summary>
        /// Check if requirement is met for given stats and rank.
        /// </summary>
        public bool IsMet(object stats, int currentRank)
        {
            // This would interface with CharacterStats
            // For now, return stub
            return true;
        }
    }

    /// <summary>
    /// Skill modifier for items and perks.
    /// 8 fields from AS3.
    /// </summary>
    [System.Serializable]
    public struct SkillModifier
    {
        public string skillId;               // Skill ID
        public float value;                  // Bonus value
        public bool isMultiplier;            // True = multiply, False = add
        public bool isWeaponSkill;           // Affects weapon skill

        public SkillModifier(string id, float val, bool mult = false)
        {
            skillId = id;
            value = val;
            isMultiplier = mult;
            isWeaponSkill = false;
        }
    }

    /// <summary>
    /// How a <see cref="EffectParam"/> writes its value onto the target.
    ///
    /// <para><b>This is AS3's <c>&lt;sk ref&gt;</c> attribute, and the default is the dangerous one.</b>
    /// <c>Unit.setSkillParam</c> (<c>Unit.as:3447-3462</c>) branches on <c>ref</c>: <c>"add"</c> →
    /// <c>this[id] += v</c>, <c>"mult"</c> → <c>this[id] *= v</c>, and <b>anything else — including a
    /// missing <c>ref</c> — assigns</b> (<c>this[id] = v</c>). So an absent <c>ref</c> is not "no
    /// operation", it is a <b>replace</b>. Getting that default wrong would turn a +25% buff into a
    /// set-to-0.25× and is exactly the kind of silent inversion this project keeps logging.</para>
    /// </summary>
    public enum EffectParamRef
    {
        /// <summary><c>this[id] = v</c> — AS3's fall-through when <c>ref</c> is neither add nor mult.</summary>
        Assign = 0,
        /// <summary><c>this[id] += v</c></summary>
        Add = 1,
        /// <summary><c>this[id] *= v</c></summary>
        Mult = 2,
    }

    /// <summary>
    /// One <c>&lt;sk&gt;</c> child of an <c>&lt;eff&gt;</c> node — a single stat-write an effect
    /// performs. The effect-level port of AS3's <c>Unit.setSkillParam</c> input.
    ///
    /// <para><b>Why this is not <see cref="SkillModifier"/>.</b> That struct is shared with perks and
    /// carries one <c>value</c> and a guessed <c>isMultiplier</c>. An effect param needs three things
    /// it cannot express: the <c>tip</c> discrimination (only <c>tip='res'</c> is a real special case
    /// in the oracle), the <c>ref</c> semantics above, and a <b>per-level value vector</b> — because
    /// <c>setSkillParam</c> selects <c>v0</c>/<c>v1</c>/<c>v2</c>… by the effect's current
    /// <c>lvl</c> (which rises with duration), and computes <c>v0 + lvl * vd</c> when a <c>vd</c>
    /// delta is present. Widening the shared struct would have forced those onto every perk.</para>
    /// </summary>
    [System.Serializable]
    public struct EffectParam
    {
        /// <summary>
        /// AS3 <c>&lt;sk id&gt;</c>. Its meaning depends on <see cref="IsResistance"/>:
        /// a <see cref="DamageType"/> index (<c>"11"</c> = Cryo) when resistance, otherwise the name
        /// of a field on the target (<c>"maxhp"</c>, <c>"tormoz"</c>, …).
        /// </summary>
        public string id;

        /// <summary>
        /// True for AS3 <c>tip='res'</c> — the <b>only</b> <c>tip</c> value
        /// <c>Unit.setSkillParam</c> treats specially (<c>Unit.as:3443</c>). A resistance write is
        /// <c>vulner[id] -= v</c> — <b>note the minus</b>: a positive value makes the target take
        /// <i>more</i> of that damage type, matching the vulnerability table's "1 = neutral, &gt;1 =
        /// weak" convention. <c>tip='weap'</c>/<c>'unit'</c> are <b>not</b> special cases: AS3 funnels
        /// them into the field-write branch, where they name fields that do not exist — so they are
        /// no-ops in the oracle too.
        /// </summary>
        public bool IsResistance;

        /// <summary>How the value is applied — AS3 <c>ref</c>.</summary>
        public EffectParamRef op;

        /// <summary>
        /// The per-level values, AS3 <c>v0</c>…<c>v5</c>. Index 0 is <c>v0</c>. The effect's current
        /// <c>lvl</c> (1..4) selects the entry; when the entry or the whole vector is absent the
        /// oracle falls back down a chain (<c>v&lt;lvl&gt;</c> → <c>v0</c>), reproduced by
        /// <see cref="ValueForLevel"/>.
        /// </summary>
        public float[] perLevel;

        /// <summary>
        /// AS3 <c>vd</c> — a per-level delta. When set (non-zero, or the attribute was present), the
        /// value is <c>v0 + lvl * vd</c> and the <see cref="perLevel"/> vector is bypassed entirely
        /// (<c>Unit.as:3429-3431</c>).
        /// </summary>
        public float delta;

        /// <summary>True when AS3 <c>vd</c> was present on the node (delta form wins over perLevel).</summary>
        public bool hasDelta;

        /// <summary>
        /// The value this param writes for a given index, reproducing AS3's fallback chain
        /// (<c>Unit.as:3429-3441</c>): <c>vd</c> present → <c>v0 + index*vd</c>; else
        /// <c>v&lt;index&gt;</c> if present; else <c>v0</c>; else 0.
        ///
        /// <para><b><paramref name="index"/> is not always the effect's level.</b> The NPC replay
        /// passes a hardcoded <c>1</c> while active and <c>0</c> while being removed; only the player
        /// replay passes <c>eff.lvl</c> (<c>Unit.as:3495</c> vs <c>Pers.as:2202</c>). The caller
        /// supplies the index precisely because the two paths disagree — see the design doc §3.</para>
        /// </summary>
        public float ValueForLevel(int index)
        {
            if (perLevel == null || perLevel.Length == 0)
                return hasDelta ? delta * index : 0f;

            float v0 = perLevel[0];
            if (hasDelta)
                return v0 + index * delta;

            if (index > 0 && index < perLevel.Length)
                return perLevel[index];
            return v0;
        }
    }

    /// <summary>
    /// Effect reference for items and perks.
    /// </summary>
    [System.Serializable]
    public struct EffectReference
    {
        public string effectId;              // Effect ID from EffectData
        [Range(0f, 1f)]
        public float chance;                 // Trigger chance
        public float duration;               // Override duration (0 = use default)
    }

    /// <summary>
    /// Component requirement for crafting.
    /// </summary>
    [System.Serializable]
    public struct ComponentRequirement
    {
        public string itemId;                // Item ID from ItemData
        public int count;                    // Quantity needed
    }

    /// <summary>
    /// Loot entry for loot tables.
    /// </summary>
    [System.Serializable]
    public struct LootEntry
    {
        public string itemId;                // Item ID
        [Range(0f, 1f)]
        public float chance;                 // Drop chance
        public int minCount;                 // Minimum drop
        public int maxCount;                 // Maximum drop
        public int requiredDifficulty;       // Required difficulty
    }

    /// <summary>
    /// Encounter probability for locations.
    /// </summary>
    [System.Serializable]
    public struct EncounterProbability
    {
        public string unitId;                // Unit ID
        [Range(0f, 1f)]
        public float chance;                 // Spawn chance
        public int minCount;                 // Minimum spawn
        public int maxCount;                 // Maximum spawn
    }

    /// <summary>
    /// Medical item data.
    /// 12 fields from AS3.
    /// </summary>
    [System.Serializable]
    public struct MedicalData
    {
        [Header("Healing")]
        public float hpRestore;              // HP restored
        public float organRestore;           // Organ HP restored
        public float overTime;               // HP per second
        public float duration;               // Effect duration

        [Header("Special")]
        public float removeRads;             // Radiation removed
        public bool cureAddiction;           // Cures chem addiction
        public bool restoreLimbs;            // Restores crippled limbs

        [Header("Side Effects")]
        public string addictionId;           // Addiction effect ID
        [Range(0f, 1f)]
        public float addictionChance;        // Chance of addiction

        [Header("Requirements")]
        public int medicineSkillRequired;     // Required medic skill
    }

    /// <summary>
    /// Ammo variant data.
    /// 10 fields from AS3.
    /// </summary>
    [System.Serializable]
    public struct AmmoVariantData
    {
        [Header("Modifiers")]
        public int armorPiercingBonus;       // pier in AS3
        public float damageMultiplier;       // damage mult
        public float armorMultiplier;        // armor mult
        public float knockbackMultiplier;    // knock mult
        public float precisionMultiplier;    // precision mult

        [Header("Special")]
        public bool extraDurabilityCost;     // Uses more weapon durability
        public int fireDamage;               // Additional fire damage
        public DamageType damageTypeOverride; // Override damage type
    }

    /// <summary>
    /// Crafting data.
    /// </summary>
    [System.Serializable]
    public struct CraftingData
    {
        public string resultItemId;          // Item created
        public int resultCount;              // Quantity
        public string requiredWorkbench;     // Workbench type
        public string requiredSkill;         // Skill ID
        public int requiredSkillLevel;       // Skill level needed
    }

    /// <summary>
    /// Book data (skill books).
    /// </summary>
    [System.Serializable]
    public struct BookData
    {
        public string skillId;               // Skill boosted
        public int bonusLevels;              // Levels granted (usually 1)
        public bool oneTime;                 // Can only read once
    }

    /// <summary>
    /// One <b>upgrade level</b> of a wearable item's stats — AS3's <c>&lt;upd&gt;</c> element, read by
    /// <c>Armor.getXmlParam()</c> (<c>Armor.as:196-280</c>).
    ///
    /// <para><b>Deliberately per-level only, and that is a fidelity decision.</b> AS3's <c>Armor</c>
    /// splits its fields in two: the ones parsed from the <c>&lt;armor&gt;</c> element itself
    /// (<c>hp</c>, <c>tip</c>, <c>clo</c>, <c>hide</c>, <c>und</c>, <c>norep</c>) hold for the whole
    /// item, and the ones parsed from <c>&lt;upd&gt;</c> change when it is upgraded
    /// (<c>Armor.as:186-193</c> re-reads them at <c>lvl</c>). Keeping the two apart here means an
    /// armour cannot end up with level-dependent durability, which the oracle has no way to express —
    /// and the importer cannot accidentally write it.</para>
    ///
    /// <para>The per-item half lives on <see cref="ItemDefinition"/>: <c>armorHP</c>, <c>armorTip</c>,
    /// <c>armorHideMane</c>, <c>armorIndestructible</c>.</para>
    /// </summary>
    [System.Serializable]
    public struct EquipmentData
    {
        public int armor;                    // Defense bonus      AS3 @armor
        public int magicArmor;               // Magic defense      AS3 @marmor

        /// <summary>
        /// Dexterity modifier — AS3 <c>Armor.dexter</c>, applied as
        /// <c>gg.dexter += dexter; gg.dodgePlus += dexter</c> (<c>Pers.as:2013-2018</c>).
        ///
        /// <para><b>Signed, and negative is a bonus.</b> <c>metal</c> declares
        /// <c>dexter='-0.3'</c> — heavy plate that makes you <i>harder</i> to dodge, not easier.
        /// This field was previously named <c>dexPenalty</c>, which read the sign backwards for
        /// exactly that item.</para>
        ///
        /// <para><b>No consumer yet.</b> AS3 aggregates it inside <c>Pers.armorParameters()</c>, which
        /// the port has no equivalent of — so this is imported data waiting for that subsystem, not a
        /// live modifier. It is populated because dropping it would lose the value silently.</para>
        /// </summary>
        public float dexterity;              // Signed dexterity modifier  AS3 @dexter

        /// <summary>
        /// Chance (0..1) that the flat rating applies to a hit — AS3 <c>Armor.armor_qual</c>, parsed
        /// from an armour item's <c>&lt;upd qual='…'&gt;</c> (<c>Armor.as:205-207</c>). Consumed as
        /// <c>isrnd(qual)</c> = <c>Math.random() &lt; qual</c> (<c>Unit.as:4855</c>).
        ///
        /// <para><b>The default is 0, and that is the oracle — not an oversight.</b> AS3's field default
        /// is <c>0</c> (<c>Armor.as:30</c>) and <c>isrnd(0)</c> is never true, so an armour with no
        /// <c>qual</c> attribute gives <b>no reduction at all</b>. It is a sharp default: a hand-authored
        /// armour definition that forgets <c>qual</c> looks fine in the inspector and silently absorbs
        /// nothing. It is kept because it is what AS3 does, every one of the 35 armour elements in
        /// <c>AllData</c> carries <c>qual</c>, and <c>ArmourDataParser</c> always writes it —
        /// and <c>ArmourEquipTests</c> pins the behaviour so it cannot change unnoticed.</para>
        ///
        /// <para>The item XML's real range is <b>0.5–1.0</b>. The 0.25–0.9 range belongs to the
        /// <i>unit pool's</i> <c>@aqual</c> — a different attribute on a different element, which an
        /// earlier revision of this comment conflated.</para>
        /// </summary>
        public float reliability;

        /// <summary>
        /// Per-type resistance. AS3 <c>Armor.resist</c>, from the <c>&lt;upd&gt;</c> element's
        /// <c>bul</c>/<c>phis</c>/<c>blade</c>/<c>expl</c>/<c>fang</c>/<c>fire</c>/<c>cryo</c>/<c>laser</c>/
        /// <c>plasma</c>/<c>spark</c>/<c>acid</c>/<c>necro</c>/<c>venom</c> attributes.
        /// <b>Neutral is 0</b> — see <see cref="ResistTable"/> for why this is not a
        /// <see cref="VulnerabilityData"/>.
        /// </summary>
        public ResistTable resists;
    }

    /// <summary>
    /// Component data (crafting materials).
    /// </summary>
    [System.Serializable]
    public struct ComponentData
    {
        public ComponentCategory category;   // Component type
        public bool isJunk;                  // Can be scrapped
    }

    /// <summary>
    /// Potion data (alchemy).
    /// </summary>
    [System.Serializable]
    public struct PotionData
    {
        public bool isCraftable;             // Can be crafted
        public string requiredSkill;         // Skill needed (survival)
        public int requiredSkillLevel;       // Skill level
    }
}
