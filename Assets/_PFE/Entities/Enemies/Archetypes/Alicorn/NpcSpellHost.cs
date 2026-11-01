using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Magic;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The <b>enemy spell host</b> — an <see cref="ISpellHost"/> for a unit that is not the player.
    ///
    /// <para><b>Why this had to exist before <c>sp_blast</c> could land.</b> AS3's <c>Spell</c> takes an
    /// <c>owner:Unit</c> and works for any unit: the entire first gate block is inside
    /// <c>if(this.player)</c> (<c>Spell.as:191-220</c>), so an NPC skips every check and goes straight
    /// to the effect. The port's only host was <c>LiveSpellHost</c>, reachable only through
    /// <c>PlayerSpellCaster</c> — so an enemy could not cast anything, and the <c>tr1</c> alicorn's
    /// reactive blast had no path to the effect even after the effect existed. This is that path.</para>
    ///
    /// <para><b>Every player-only member answers "the gate does not apply", and that is the faithful
    /// answer rather than a stub.</b> AS3 never reads <c>rat</c>, <c>spellsPoss</c>, <c>atkPoss</c>,
    /// <c>weaponRespect</c>, the mana pool or the mana organ on an NPC path, because the block that
    /// reads them is guarded by <c>this.player</c>. Returning 0/false/1 here is therefore not a
    /// placeholder — those values are unreachable, and a fixture that asserts them is asserting
    /// unreachability.</para>
    ///
    /// <para><b><see cref="Alicorn"/> is <c>false</c> and that is not an oversight.</b>
    /// <c>World.w.alicorn</c> is the flag for the <i>player</i> being transformed into an alicorn; an
    /// alicorn <i>enemy</i> is a different thing entirely. Answering <c>true</c> would send
    /// <c>sp_mshit</c> down the player's <c>alicornShitHP</c> branch and, worse, would make every future
    /// enemy spell look like it was cast by a transformed player.</para>
    /// </summary>
    public sealed class NpcSpellHost : ISpellHost
    {
        private readonly EnemyController _unit;

        /// <summary>AS3 <c>this.owner.spellPower</c> — supplied by the family, not by the unit's stats.</summary>
        private readonly float _spellPower;

        private bool _reportedMissingUnit;

        /// <param name="unit">The casting unit. May be <c>null</c> for a brain with no controller yet.</param>
        /// <param name="spellPower">
        /// AS3 <c>owner.spellPower</c>. Not read off <see cref="UnitController"/> because the port's
        /// <c>spellPower</c> lives on <c>CharacterStats</c>, which only the player has — see
        /// <see cref="AlicornVariantRules.SpellPower"/>.
        /// </param>
        public NpcSpellHost(EnemyController unit, float spellPower)
        {
            _unit = unit;
            _spellPower = spellPower;
        }

        // ---- the player-only gate block (Spell.as:191-220) -----------------------------------------

        /// <summary><c>Spell.player</c> — false. This is the member that makes every gate below moot.</summary>
        bool ISpellHost.IsPlayer => false;

        /// <summary><c>gg.rat</c> — unread for an NPC (<c>Spell.as:193</c> is inside <c>if(this.player)</c>).</summary>
        int ISpellHost.Rat => 0;

        /// <summary><c>World.w.alicorn</c> — the <i>player's</i> transformation flag. See the class remarks.</summary>
        bool ISpellHost.Alicorn => false;

        /// <summary><c>pers.spellsPoss</c> — unread for an NPC.</summary>
        int ISpellHost.SpellsPossible => 1;

        /// <summary><c>gg.atkPoss</c> — unread for an NPC.</summary>
        bool ISpellHost.AtkPossible => true;

        /// <summary><c>invent.weapons[id].respect</c> — an NPC has no inventory.</summary>
        bool ISpellHost.WeaponRespect(string spellId) => false;

        // ---- mana ----------------------------------------------------------------------------------

        /// <summary>
        /// <c>owner.mana</c>. The alicorn sets <c>mana = maxmana = 2000</c> (<c>UnitAlicorn.as:185</c>),
        /// and its <c>resmana</c> regenerates it — but the value is only ever <i>checked</i> inside the
        /// player gate, so a cast never spends it. Reported rather than zeroed so an overlay can show
        /// it; nothing on this path reads it.
        /// </summary>
        float ISpellHost.Mana => _unit != null ? 2000f : 0f;

        /// <summary><c>pers.manaHP</c> — the player's mana organ. An NPC has none.</summary>
        float ISpellHost.ManaHp => 0f;

        /// <summary><c>pers.allDManaMult</c> — a difficulty modifier on the player's mana cost.</summary>
        float ISpellHost.AllDManaMult => 1f;

        /// <summary><c>pers.warlockDManaMult</c> — as above.</summary>
        float ISpellHost.WarlockDManaMult => 1f;

        /// <summary><c>pers.spellDown</c> — as above.</summary>
        float ISpellHost.SpellDown => 1f;

        /// <summary>
        /// <c>manaSpell(...)</c>. <b>A no-op, and it must be.</b> <c>Spell.cast()</c> only reaches the
        /// spend inside <c>if(this.owner.player)</c> (<c>:281-283</c>) — an NPC cast is free in the
        /// oracle. Making this drain something would invent a resource an enemy does not have.
        /// </summary>
        void ISpellHost.SpendMana(float amount) { }

        /// <summary><c>pers.manaDamage(...)</c> — the same guard, the same no-op.</summary>
        void ISpellHost.DamageManaOrgan(float amount) { }

        // ---- placement (Spell.as:238-249) -----------------------------------------------------------

        /// <summary>
        /// <c>owner.magicX</c>. The unit's own X in pixels — <c>Unit.setWeaponPos</c> assigns
        /// <c>magicX = X</c> (<c>Unit.as:3292</c>).
        ///
        /// <para>Only <c>sp_blast</c> is cast from here today, and it <b>overwrites</b> both coordinates
        /// with the feet (<c>Spell.as:382-383</c>), so the horn-vs-feet distinction this could get wrong
        /// cannot be observed yet. When a second enemy spell lands, this is the line to re-check against
        /// the frame-data horn work.</para>
        /// </summary>
        float ISpellHost.MagicX => FeetX;

        /// <summary><c>owner.magicY</c> — <c>Y - scY * 0.5</c> (<c>Unit.as:3293</c>), i.e. the body centre.</summary>
        float ISpellHost.MagicY
        {
            get
            {
                if (_unit == null) return 0f;
                return FeetY + _unit.SpriteSizePixels.y * 0.5f;
            }
        }

        /// <summary><c>owner.spellPower</c> — the family's constant.</summary>
        float ISpellHost.SpellPower => _spellPower;

        /// <summary><c>gg.pers.telePower</c> — a player perk; the alicorn uses <see cref="ISpellHost.SpellPower"/>.</summary>
        float ISpellHost.TelePower => 1f;

        /// <summary>
        /// <c>loc.isLine(X, Y, cx, cy)</c> — only consulted when the row carries <c>line='1'</c>, and
        /// <c>sp_blast</c> does not (<c>AllData.as:4010</c>). Answered <c>true</c> rather than wired to a
        /// raycast so that a future <c>line</c> spell fails visibly (no cast is refused) instead of
        /// silently refusing every cast through an unimplemented test.
        /// </summary>
        bool ISpellHost.IsLineVisible(float fromX, float fromY, float toX, float toY) => true;

        // ---- effect surface --------------------------------------------------------------------------

        /// <summary><c>owner.addEffect(id, value)</c> — the same <c>UnitStats.Effects</c> set the player path uses.</summary>
        void ISpellHost.AddEffect(string effectId, float value)
        {
            UnitStats stats = _unit != null ? _unit.UnitStats : null;
            stats?.Effects.AddEffect(effectId, value);
        }

        /// <summary><c>owner.shithp = value</c> — the alicorn's shield pool, which <c>sp_mshit</c> sets and <c>AlicornShieldRules</c> drains.</summary>
        void ISpellHost.SetShitHp(float value)
        {
            UnitStats stats = _unit != null ? _unit.UnitStats : null;
            if (stats != null) stats.ShitHp = value;
        }

        /// <summary><c>pers.alicornShitHP</c> — the player's perk value; unreachable from an NPC.</summary>
        float ISpellHost.AlicornShitHp => 2000f;

        /// <summary><c>gg.t_cryst</c> — a player field; <c>cast_cryst</c> only reads it inside <c>if(this.player)</c>.</summary>
        int ISpellHost.CrystalCooldown { get => 0; set { } }

        /// <summary>
        /// <c>cast_moon</c> (<c>Spell.as:428-440</c>) — a <b>deliberate no-op</b> on an enemy caster.
        /// </summary>
        /// <remarks>
        /// The oracle's <c>cast_moon</c> touches <c>this.gg</c>, the global game object, so literally
        /// transcribed an alicorn casting <c>sp_moon</c> would summon <i>the player's</i> moon. Nothing
        /// in the data gives an enemy <c>sp_moon</c> (its only carrier is the level-20
        /// <c>&lt;weapon id='sp_moon' spell='1'/&gt;</c> row), so the branch is unreachable; reproducing
        /// it would mean giving an enemy write access to the player's companion slots to no effect. The
        /// gap is named here rather than papered over.
        /// </remarks>
        void ISpellHost.CastMoonPet() { }

        /// <summary>
        /// AS3 <c>cast_blast</c> (<c>Spell.as:371-418</c>) — the pulse, centred on the caster's feet and
        /// limited to its own room.
        /// </summary>
        /// <remarks>
        /// <b><c>alertRoom: false</c>, and that is the alicorn's half of the oracle.</b> The
        /// <c>loc.budilo(X, Y, 500)</c> at <c>:406-409</c> is inside <c>if(this.owner.player)</c>, so a
        /// monster's blast is silent to the room while a player's is not. The alicorn still shouts on
        /// <c>budilo()</c> when it acquires a target and when it dies — just not here.
        /// </remarks>
        void ISpellHost.ApplyRadialBlast(float radius, float damage, float power)
        {
            if (_unit == null)
            {
                if (_reportedMissingUnit) return;
                _reportedMissingUnit = true;
                Debug.LogWarning(
                    "[NpcSpellHost] a spell effect needed a unit but the host was built without one " +
                    "(the brain has no controller yet). The cast is spent with no effect.");
                return;
            }

            SpellBlastExecutor.Execute(_unit, radius, damage, power, alertRoom: false);
        }

        /// <summary>
        /// <c>Snd.ps(id, x, y)</c> — the unit's own sound service.
        ///
        /// <para>The coordinates arrive in <b>world pixels</b> (<c>Spell.X</c>/<c>Y</c> come from
        /// <see cref="ISpellHost.MagicX"/>, which this host answers in pixels) while
        /// <see cref="ISoundService.Play"/> takes a Unity world position, so the ÷100 belongs here —
        /// the same boundary conversion <c>LiveSpellWorld.IsBlocked</c> documents.</para>
        /// </summary>
        void ISpellHost.PlaySound(string soundId, float x, float y)
        {
            ISoundService sound = _unit != null ? _unit.Sound : null;
            if (sound == null || string.IsNullOrEmpty(soundId)) return;

            sound.Play(soundId, new Vector2(x, y) / TileQueryConstants.UnitToPixel);
        }

        // ---- refusal feedback (Spell.as:202-239) ----------------------------------------------------
        //
        // All three are HUD channels and an enemy has no HUD. AS3 reaches them through `World.w.gui`,
        // which is the player's interface; an NPC refusal is silent there too, so a no-op is the
        // faithful answer and not a gap.

        /// <summary><c>gui.infoText(key)</c> — the player's HUD. Silent for an NPC.</summary>
        void ISpellHost.ShowInfoText(string key) { }

        /// <summary><c>gui.infoText(key, n)</c> — the player's HUD.</summary>
        void ISpellHost.ShowInfoText(string key, float number) { }

        /// <summary><c>gui.bulb(x, y)</c> — the player's HUD.</summary>
        void ISpellHost.ShowBulb(float x, float y) { }

        // ---- geometry ------------------------------------------------------------------------------

        private float FeetX => _unit != null
            ? _unit.transform.position.x * TileQueryConstants.UnitToPixel
            : 0f;

        private float FeetY => _unit != null
            ? _unit.transform.position.y * TileQueryConstants.UnitToPixel
            : 0f;
    }
}
