using PFE.Core;
using PFE.Data.Definitions;

namespace PFE.Systems.Magic
{
    /// <summary>
    /// What a <see cref="Spell.Cast"/> call did. Carries the refusal so a caller can tell "refused by
    /// the cooldown" from "refused because the port has not written this effect yet" — which a bare
    /// <c>bool</c> would collapse into one unhelpful "no".
    /// </summary>
    public readonly struct SpellCastResult
    {
        /// <summary>True when the cast happened and the effect ran with <c>est == 1</c>.</summary>
        public readonly bool Cast;

        /// <summary>Why it did not happen; <see cref="SpellCastRefusal.None"/> on success.</summary>
        public readonly SpellCastRefusal Refusal;

        /// <summary>
        /// <c>t_culd</c> after the cast, in frames. 0 when the cast did not happen or the caster is not
        /// the player (an NPC never stamps a cooldown — see <see cref="Spell.Cast"/>).
        /// </summary>
        public readonly int CooldownTicks;

        /// <summary>Mana actually removed. 0 for an NPC, whose cast is free.</summary>
        public readonly float ManaSpent;

        /// <summary>The target after the <c>dist</c> clamp — the point the effect actually ran at.</summary>
        public readonly float TargetX;
        public readonly float TargetY;

        internal SpellCastResult(
            bool cast, SpellCastRefusal refusal, int cooldownTicks, float manaSpent,
            float targetX, float targetY)
        {
            Cast          = cast;
            Refusal       = refusal;
            CooldownTicks = cooldownTicks;
            ManaSpent     = manaSpent;
            TargetX       = targetX;
            TargetY       = targetY;
        }
    }

    /// <summary>
    /// One cast-from-inventory spell — the port of AS3 <c>fe.unit.Spell</c>.
    ///
    /// <para><b>Not a weapon, and not on the weapon controller.</b> A spell is never equipped: the nine
    /// <c>sp_*</c> <c>&lt;weapon tip='5' spell='1'/&gt;</c> rows exist only so the inventory can list
    /// them, and <c>WeaponControllerFactory</c> and <c>PlayerWeaponLoadout.Equip</c> both refuse one.
    /// AS3 casts from the inventory instead (<c>UnitPlayer.as:3627-3635</c> →
    /// <c>Invent.useItem</c> → <c>Spell.cast()</c>). That trigger path is the next slice; this class is
    /// the thing it will drive.</para>
    ///
    /// <para><b>What is ported here, and what is not.</b> The <b>whole prologue</b> is ported — the gate
    /// order, the mana cost, the line test, the range clamp, the cooldown stamp — because that is the
    /// shared logic and the part a test can pin down. Of the nine effects, three need no subsystem the
    /// port lacks: <c>sp_cryst</c> (pure state), <c>sp_slow</c> (<c>AddEffect("inhibitor")</c>) and
    /// <c>sp_mshit</c> (a single stat write). The other six return
    /// <see cref="SpellCastRefusal.EffectNotPorted"/> rather than pretending to succeed — they need
    /// unit spawns, ghost tiles, pets, the blood pool, or a levitation impulse, each its own slice.</para>
    ///
    /// <para><b>No Unity type appears in this file</b>, so the entire cast path runs offline. That is
    /// deliberate: the alternative is a fixture that constructs a live unit and dies on the
    /// <c>ECall</c> wall, which is exactly how the assault path's tests ended up editor-only.</para>
    /// </summary>
    public sealed class Spell
    {
        /// <summary>AS3 <c>est = 0</c> — the effect refused; nothing is charged.</summary>
        public const int EstFailed = 0;
        /// <summary>AS3 <c>est = 1</c> — success; mana is spent and the cooldown is stamped.</summary>
        public const int EstSuccess = 1;
        /// <summary>
        /// AS3 <c>est = 2</c> — <b>neither</b> branch of <c>cast()</c>'s tail matches, so the cast
        /// returns true while spending no mana and stamping no cooldown. Only <c>cast_cryst</c> sets it
        /// (a second <c>sp_cryst</c> inside the <c>t_cryst</c> window).
        /// </summary>
        public const int EstAlternate = 2;

        private readonly ISpellHost _host;
        private readonly SpellData _data;

        /// <summary>
        /// AS3 <c>culd</c> converted to frames at construction —
        /// <c>this.culd = this.xml.@culd * World.fps</c> (<c>Spell.as:98</c>), assigned into an
        /// <c>int</c> field, so it truncates. The importer stores the <b>raw</b> attribute; this is the
        /// one place the ×30 happens, which is why <c>sp_slow</c>'s <c>culd='10'</c> is 300 frames.
        /// </summary>
        private readonly int _culdFrames;

        /// <summary>The spell id — the <c>&lt;item&gt;</c> id, e.g. <c>sp_slow</c>.</summary>
        public string Id { get; }

        /// <summary>AS3 <c>t_culd</c>: frames remaining before this spell may be cast again.</summary>
        public int CooldownTicks { get; private set; }

        /// <summary>
        /// AS3 <c>active</c> (<c>Spell.as:30</c>, default false). Suppresses the cooldown message while
        /// a spell is being held/aimed (<c>:216</c>) — the player already knows, so the refusal is
        /// silent apart from the sound.
        /// </summary>
        public bool Active { get; set; }

        /// <summary>AS3 <c>est</c>, left at its last value for inspection. Default 1.</summary>
        public int Est { get; private set; } = EstSuccess;

        /// <summary>
        /// AS3 <c>power</c>, resolved from the owner each cast (<c>:238-249</c>). Multiplies the effect's
        /// magnitude in most of the nine.
        /// </summary>
        public float Power { get; private set; } = 1f;

        /// <summary>AS3 <c>X</c>/<c>Y</c> — the caster's magic origin, not the target.</summary>
        public float X { get; private set; }
        public float Y { get; private set; }

        /// <summary>AS3 <c>cx</c>/<c>cy</c> — the target, after the <c>dist</c> clamp.</summary>
        public float Cx { get; private set; }
        public float Cy { get; private set; }

        /// <summary>
        /// True when this id is one of the nine AS3 spells — i.e. AS3 would have found a <c>cast_*</c>
        /// function for it (<c>Spell.as:133-168</c>). False makes every cast
        /// <see cref="SpellCastRefusal.NoEffect"/>, which is what AS3 does for an id it does not know.
        /// </summary>
        public bool HasEffect { get; }

        /// <summary>
        /// True when the port has implemented this id's effect. <see cref="HasEffect"/> and this are
        /// different questions — see <see cref="SpellCastRefusal.EffectNotPorted"/>.
        /// </summary>
        public bool IsEffectPorted { get; }

        /// <summary>
        /// AS3 <c>Spell.prod</c> (<c>Spell.as:35</c>), set from the row's <c>@prod</c> presence flag at
        /// <c>:116-118</c> — <b>hold-to-repeat</b>. The trigger loops read it to decide whether the key
        /// stays held after a cast: <c>if(!this.currentSpell.prod) this.ctr.keyDef = false;</c>
        /// (<c>UnitPlayer.as:2241-2244</c>, and the same pair at <c>:2270-2273</c> for the favourite-spell
        /// hotkeys). Only <c>sp_cryst</c> carries the attribute, so it is the only spell that keeps
        /// firing while the button is down.
        ///
        /// <para>Exposed rather than read off the data by the caller because the trigger belongs to
        /// <see cref="SpellBook"/>, which holds <see cref="Spell"/> objects and not their rows.</para>
        /// </summary>
        public bool Produces => _data.prod;

        public Spell(string id, in SpellData data, ISpellHost host)
        {
            Id             = id;
            _data          = data;
            _host          = host;
            _culdFrames    = (int)(data.culd * SimClock.FramesPerSecond);
            HasEffect      = IsKnownSpellId(id);
            IsEffectPorted = IsPortedEffect(id);
        }

        /// <summary>
        /// The nine ids AS3 dispatches on (<c>Spell.as:133-168</c>), transcribed from the <c>if</c>
        /// chain rather than from the data file, because the chain — not the file — is what decides
        /// whether <c>cf</c> is non-null.
        /// </summary>
        private static bool IsKnownSpellId(string id)
        {
            switch (id)
            {
                case "sp_mwall":
                case "sp_mshit":
                case "sp_blast":
                case "sp_kdash":
                case "sp_slow":
                case "sp_cryst":
                case "sp_moon":
                case "sp_gwall":
                case "sp_invulner":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The subset whose effect this port implements — see the class remarks.</summary>
        private static bool IsPortedEffect(string id)
            => id == "sp_cryst" || id == "sp_mshit" || id == "sp_slow";

        /// <summary>
        /// AS3 <c>step()</c> (<c>Spell.as:172-178</c>): the cooldown ticks down one frame at a time.
        /// Called once per simulation tick by whoever owns the spell.
        /// </summary>
        public void Step()
        {
            if (CooldownTicks > 0) CooldownTicks--;
        }

        /// <summary>
        /// AS3 <c>cast(param1, param2)</c> (<c>Spell.as:184-300</c>), in the oracle's own order.
        ///
        /// <para><b>Placement is resolved before the gates, and that is safe.</b> AS3 sets
        /// <c>X</c>/<c>Y</c>/<c>power</c> from the owner <i>after</i> the player gate block
        /// (<c>:238-249</c>), but those lines only <i>read</i> the owner — no gate depends on them and
        /// they mutate nothing — while the line-of-sight test at <c>:262</c> needs <c>X</c>/<c>Y</c> and
        /// runs <i>after</i> the gates. Resolving them first therefore preserves the observable order of
        /// every refusal, which is what the gate tests pin down.</para>
        /// </summary>
        public SpellCastResult Cast(float cx, float cy)
        {
            // ---- placement, Spell.as:238-249 ----
            if (_host != null)
            {
                X = _host.MagicX;
                Y = _host.MagicY;
                Power = _host.SpellPower;
                // `if(this.player && this.teleSpell) this.power = this.gg.pers.telePower;`
                if (_host.IsPlayer && _data.tele) Power = _host.TelePower;
            }

            Cx = cx;
            Cy = cy;

            bool lineOfSight = true;
            if (_data.line && _host != null)
                lineOfSight = _host.IsLineVisible(X, Y, Cx, Cy);

            var ctx = new SpellCastContext(
                hasEffect:       HasEffect,
                isPlayer:        _host != null && _host.IsPlayer,
                spellId:         Id,
                atk:             _data.atk,
                alicorn:         _host != null && _host.Alicorn,
                rat:             _host != null ? _host.Rat : 0,
                weaponRespect:   _host != null && _host.WeaponRespect(Id),
                spellsPossible:  _host != null ? _host.SpellsPossible : 1,
                atkPossible:     _host != null && _host.AtkPossible,
                cooldownTicks:   CooldownTicks,
                ownerMana:       _host != null ? _host.Mana : 0f,
                manaHp:          _host != null ? _host.ManaHp : 0f,
                magic:           _data.magic,
                mana:            _data.mana,
                allDManaMult:    _host != null ? _host.AllDManaMult : 1f,
                line:            _data.line,
                hasOwner:        _host != null,
                lineOfSight:     lineOfSight);

            SpellCastRefusal refusal = SpellCastRules.Evaluate(ctx);
            if (refusal != SpellCastRefusal.None)
            {
                Report(refusal);
                return Failure(refusal);
            }

            if (!IsEffectPorted) return Failure(SpellCastRefusal.EffectNotPorted);

            // ---- range clamp, Spell.as:270-279 ----
            // Through locals: Cx/Cy are properties, so they cannot be passed by ref. The oracle
            // mutates this.cx/this.cy in place; the observable result is identical.
            float targetX = Cx, targetY = Cy;
            SpellCastRules.ClampToRange(X, Y, _data.dist, ref targetX, ref targetY);
            Cx = targetX;
            Cy = targetY;

            // ---- the effect, Spell.as:280 ----
            Est = EstSuccess;
            RunEffect();

            // ---- tail, Spell.as:281-299 ----
            float manaSpent = 0f;
            if (Est == EstSuccess)
            {
                if (_host.IsPlayer)
                {
                    // `manaSpell(magic * warlockDManaMult, mana * warlockDManaMult)`, and manaSpell
                    // multiplies each by allDManaMult again — see SpellCastRules.ManaSpend.
                    manaSpent = SpellCastRules.ManaSpend(_data.magic, _host.WarlockDManaMult, _host.AllDManaMult);
                    _host.SpendMana(manaSpent);
                    _host.DamageManaOrgan(
                        SpellCastRules.ManaSpend(_data.mana, _host.WarlockDManaMult, _host.AllDManaMult));
                    CooldownTicks = SpellCastRules.CooldownAfterCast(_culdFrames, _host.SpellDown);
                }

                if (!string.IsNullOrEmpty(_data.snd)) _host.PlaySound(_data.snd, X, Y);
            }
            else if (Est == EstFailed)
            {
                // `else if(this.est == 0) { Snd.ps("nomagic"); return false; }`
                _host.PlaySound("nomagic", X, Y);
                return Failure(SpellCastRefusal.EffectRefused);
            }
            // `est == 2` deliberately falls through: AS3 returns true without charging anything.

            return new SpellCastResult(true, SpellCastRefusal.None, CooldownTicks, manaSpent, Cx, Cy);
        }

        /// <summary>
        /// The player-facing feedback for each refusal, transcribed from the oracle. Note that
        /// <see cref="SpellCastRefusal.Alicorn"/> and <see cref="SpellCastRefusal.Rat"/> are
        /// <b>silent</b> in AS3 (<c>:193-200</c> return with no message and no sound), which is easy to
        /// "fix" into a message and should not be.
        /// </summary>
        private void Report(SpellCastRefusal refusal)
        {
            if (_host == null) return;
            bool player = _host.IsPlayer;

            switch (refusal)
            {
                case SpellCastRefusal.NoEffect:
                    // cf == null returns before any feedback.
                    break;

                case SpellCastRefusal.Alicorn:
                case SpellCastRefusal.Rat:
                    // Silent in the oracle. Do not add a message.
                    break;

                case SpellCastRefusal.WeaponRespect:
                    _host.ShowInfoText("disSpell");
                    _host.PlaySound("nomagic", X, Y);
                    break;

                case SpellCastRefusal.NoSpells:
                    _host.ShowInfoText("noSpells");
                    _host.PlaySound("nomagic", X, Y);
                    _host.ShowBulb(X, Y);
                    break;

                case SpellCastRefusal.Cooldown:
                    if (!Active)
                    {
                        if (_culdFrames >= SpellCastRules.CooldownFeedbackThreshold)
                            _host.ShowInfoText("spellCuld", (float)System.Math.Ceiling(
                                CooldownTicks / (double)SimClock.FramesPerSecond));
                        _host.PlaySound("nomagic", X, Y);
                        _host.ShowBulb(X, Y - 20f);
                    }
                    break;

                case SpellCastRefusal.OverMana:
                    _host.ShowInfoText("overMana");
                    _host.PlaySound("nomagic", X, Y);
                    _host.ShowBulb(X, Y - 20f);
                    break;

                case SpellCastRefusal.NoMana:
                    _host.ShowInfoText("noMana");
                    _host.PlaySound("nomagic", X, Y);
                    break;

                case SpellCastRefusal.NoLineOfSight:
                    if (player) _host.ShowInfoText("noVisible");
                    break;
            }
        }

        private SpellCastResult Failure(SpellCastRefusal refusal)
            => new SpellCastResult(false, refusal, CooldownTicks, 0f, Cx, Cy);

        /// <summary>
        /// The three ported effects, transcribed from <c>Spell.as:308-345, 418-424</c>. Each sets
        /// <see cref="Est"/>; the tail of <see cref="Cast"/> reads it.
        /// </summary>
        private void RunEffect()
        {
            switch (Id)
            {
                // Spell.as:309-316
                case "sp_mshit":
                    // `if(this.owner.player && World.w.alicorn) owner.shithp = pers.alicornShitHP;
                    //  else owner.shithp = this.hp * this.power;`
                    _host.SetShitHp(_host.IsPlayer && _host.Alicorn
                        ? _host.AlicornShitHp
                        : _data.hp * Power);
                    break;

                // Spell.as:418-424
                case "sp_slow":
                    // `if(this.owner) this.owner.addEffect("inhibitor", this.rad * this.power);`
                    _host.AddEffect("inhibitor", _data.rad * Power);
                    break;

                // Spell.as:323-331
                case "sp_cryst":
                    Est = EstSuccess;
                    if (_host.IsPlayer)
                    {
                        if (_host.CrystalCooldown > 0) Est = EstAlternate;
                        _host.CrystalCooldown = 5;
                    }
                    break;
            }
        }
    }
}
