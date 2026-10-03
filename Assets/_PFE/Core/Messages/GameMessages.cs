using UnityEngine;

namespace PFE.Core.Messages
{
    #region Input Messages

    /// <summary>
    /// Published when jump input state changes.
    /// </summary>
    public struct JumpMessage
    {
        public bool IsStarted;
    }

    /// <summary>
    /// Published when attack input state changes.
    /// </summary>
    public struct AttackMessage
    {
        public bool IsStarted;
    }

    /// <summary>
    /// Published on the <b>press</b> edge of the reload button (R).
    ///
    /// <para><b>Why this exists at all.</b> <c>PlayerWeaponLoadout.StartReload()</c> and the whole
    /// <c>IWeaponController.StartReload()</c> chain were already written, and nothing in production
    /// called them — so the reload key simply did not exist in the port. That is not cosmetic: AS3
    /// calls <c>currentWeapon.detonator()</c> from the reload key (<c>UnitPlayer.as:2358</c>), and
    /// <c>x37</c> is a mine with <c>sens='0'</c> — it never triggers on proximity, so the radio
    /// detonator is its only way to fire. Without this message that weapon is inert.</para>
    ///
    /// <para><b>Press edge only, no release.</b> AS3's <c>keyReload</c> is a held boolean, but both
    /// of the things it drives are edge-triggered at the far end: <c>detonator()</c> runs on the
    /// first frame and then clears the key itself (<c>:2358-2361</c>), and the magazine reload is
    /// started once by <c>initReload()</c>. The port's <c>RangedWeaponController.StartReload()</c> is
    /// likewise idempotent (it guards on <c>TReload &lt;= 0</c>). Publishing a release edge would give
    /// consumers nothing to do with it.</para>
    ///
    /// <para>The port does <b>not</b> model AS3's 30-frame hold-to-<c>unloadWeapon()</c> path
    /// (<c>:2351-2357</c>): <c>unloadWeapon</c> is the "eject the magazine" action, and the port has
    /// no unload concept. Recorded, not silently dropped.</para>
    /// </summary>
    public struct ReloadMessage
    {
        public bool IsStarted;
    }

    /// <summary>
    /// Published on <b>both</b> edges of the Def key — AS3 <c>ctr.keyDef</c>, <c>C</c>
    /// (<c>inter/Ctr.as:25</c>). This is the <b>supportive-spell</b> cast button: it casts the spell
    /// selected in the inventory, and in alicorn mode substitutes <c>sp_mshit</c>
    /// (<c>UnitPlayer.as:2226-2247</c>).
    ///
    /// <para><b>Both edges, and the consumer writes back.</b> <c>keyDef</c> is a <i>held</i> boolean,
    /// and AS3 mutates it from inside the trigger: the cast clears it when it fails or when the spell has
    /// no <c>prod</c> (<c>:2237-2244</c>), so a non-<c>prod</c> spell is one press = one cast while
    /// <c>sp_cryst</c> (the only <c>prod='1'</c> row) repeats while held. A press-edge-only message
    /// could not express that — the whole <c>prod</c> rule would be unreachable.</para>
    ///
    /// <para><b>Not the same button as assault magic.</b> That is <c>keyMagic</c> = <c>T</c>
    /// (<c>Ctr.as:24</c>, <see cref="MagicWeaponMessage"/>), which fires the equipped magic <i>weapon</i>.
    /// The two are separate in the oracle and separate here; unifying them would make the nine
    /// supportive spells and the assault weapons fight over one key.</para>
    /// </summary>
    public struct SpellCastMessage
    {
        /// <summary>True while the Def key is down; false on release.</summary>
        public bool IsHeld;
    }

    /// <summary>
    /// Published on <b>both</b> edges of the magic-weapon key — AS3 <c>ctr.keyMagic</c>, <c>T</c>
    /// (<c>inter/Ctr.as:24</c>).
    ///
    /// <para>Fires the equipped magic <i>weapon</i> (<c>UnitPlayer.as:2207-2223</c>), which is a
    /// different path from the supportive spells: the weapon has a <c>&lt;char&gt;</c> body, a skill
    /// gate and an ammo cost, and it is refused by <c>WeaponControllerFactory</c> only when
    /// <c>weapon@spell</c> is set. The port already has that whole path
    /// (<c>MagicWeaponController</c>); what it never had was a key to press.</para>
    ///
    /// <para>Held rather than edge-triggered: AS3 clears the key itself only for a <c>tip == 4</c>
    /// weapon (<c>:2220-2222</c>), and the controller's own timers (<c>t_attack</c>, <c>TPrep</c>) are
    /// what actually pace the shots.</para>
    /// </summary>
    public struct MagicWeaponMessage
    {
        /// <summary>True while the magic-weapon key is down; false on release.</summary>
        public bool IsHeld;
    }

    /// <summary>
    /// Published on <b>both</b> edges of a favourite-spell hotkey — AS3 <c>ctr.keySpell1..4</c>, which
    /// default to <c>Z</c> and <c>X</c> with slots 3 and 4 <b>unbound</b>
    /// (<c>inter/Ctr.as:62-65</c>; <c>World.kolQS = 4</c>, <c>World.as:62</c>).
    ///
    /// <para><b>The slot indexes <c>invent.fav</c> after the weapon slots:</b> AS3 resolves
    /// <c>invent.fav[World.kolHK * 2 + slot]</c> with <c>kolHK = 12</c> (<c>UnitPlayer.as:2263</c>,
    /// <c>World.as:60</c>), i.e. favourite entries 25-28 — the same table the twelve weapon hotkeys use.
    /// So the slot number is part of the message rather than four separate message types.</para>
    ///
    /// <para>Held, like the Def key, because <c>prod</c> governs it the same way
    /// (<c>:2270-2273</c>). Note the two asymmetries with <see cref="SpellCastMessage"/>, both in the
    /// oracle: this path has <b>no <c>rat</c> guard</b> and <b>no alicorn substitution</b>.</para>
    /// </summary>
    public struct SpellHotkeyMessage
    {
        /// <summary>1-based slot, 1..4 — matching AS3's <c>keySpell1</c>..<c>keySpell4</c>.</summary>
        public int Slot;

        /// <summary>True while the slot's key is down; false on release.</summary>
        public bool IsHeld;
    }

    /// <summary>
    /// Published on <b>both</b> edges of the interact button (E): <c>true</c> when it goes down,
    /// <c>false</c> when it comes back up.
    ///
    /// <para>The release edge is not optional. Interacting is a hold in AS3 — <c>keyAction</c> is a
    /// held boolean and <c>UnitPlayer.as:2131-2135</c> abandons the action the moment it goes false.
    /// A consumer that only reads the press cannot tell "still holding" from "let go early".</para>
    /// </summary>
    public struct InteractMessage
    {
        public bool IsPressed;
    }

    /// <summary>
    /// Published when dash input is triggered.
    /// </summary>
    public struct DashMessage
    {
        public bool IsStarted;
    }

    /// <summary>
    /// Published when teleport key state changes (Q key hold/release).
    /// AS3: charge-based — hold to charge, release to execute.
    /// </summary>
    public struct TeleportMessage
    {
        public bool IsStarted;
    }

    #endregion

    #region Combat Messages

    /// <summary>
    /// Published when a weapon is fired.
    /// </summary>
    public struct WeaponFiredMessage
    {
        public string WeaponId;
        public Vector3 Position;
        public Vector3 Direction;
    }

    /// <summary>
    /// Published when a unit takes damage.
    /// </summary>
    public struct DamageTakenMessage
    {
        public float Damage;
        public Vector3 HitPoint;
        public bool IsCritical;
        public int TargetInstanceId;
    }

    /// <summary>
    /// Published when damage is dealt to a target.
    /// Similar to DamageTakenMessage but from attacker's perspective.
    /// </summary>
    public struct DamageDealtMessage
    {
        public float damage;
        public Vector3 position;
        public bool isCritical;
        public bool isMiss;
    }

    /// <summary>
    /// Published when a unit is healed.
    /// </summary>
    public struct HealMessage
    {
        public float amount;
        public Vector3 position;
    }

    /// <summary>
    /// Published when a weapon starts reloading.
    /// </summary>
    public struct WeaponReloadStartedMessage
    {
        public string WeaponId;
        public float ReloadDuration;
    }

    /// <summary>
    /// Published when a weapon finishes reloading.
    /// </summary>
    public struct WeaponReloadCompletedMessage
    {
        public string WeaponId;
    }

    /// <summary>
    /// Published when weapon durability changes.
    /// </summary>
    public struct WeaponDurabilityChangedMessage
    {
        public string WeaponId;
        public int CurrentDurability;
        public int MaxDurability;
        public float BreakingStatus;
    }

    #endregion

    #region RPG Messages

    /// <summary>
    /// Published when a character levels up.
    /// </summary>
    public struct LevelUpMessage
    {
        public int NewLevel;
        public int SkillPointsGained;
        public int PerkPointsGained;
    }

    /// <summary>
    /// Published when a skill level changes.
    /// </summary>
    public struct SkillLevelChangedMessage
    {
        public string SkillId;
        public int NewLevel;
    }

    /// <summary>
    /// Published when character stats change.
    /// </summary>
    public struct StatsChangedMessage
    {
        public int CurrentHp;
        public int MaxHp;
        public int CurrentMana;
        public int MaxMana;
    }

    /// <summary>
    /// Published when XP is gained. Matches AS3 numbEmit("+Nxp").
    /// </summary>
    public struct XpGainedMessage
    {
        public int Amount;
        public int TotalXp;
        public Vector3 Position;
    }

    /// <summary>
    /// Published when a perk rank is gained.
    /// </summary>
    public struct PerkAddedMessage
    {
        public string PerkId;
        public int Rank;
    }

    /// <summary>
    /// Published when limb trauma stage changes (1=head, 2=torso, 3=legs, 4=blood, 5=mana).
    /// </summary>
    public struct TraumaChangedMessage
    {
        public int BodyPart;
        public int Stage;
    }

    #endregion
}
