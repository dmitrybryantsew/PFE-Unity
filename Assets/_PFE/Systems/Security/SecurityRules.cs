using System;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Systems.Security
{
    /// <summary>
    /// The land-level numbers <see cref="Build"/> needs, which the object itself does not carry.
    ///
    /// <para><b>Why a struct and not three more parameters.</b> They all come from the same place
    /// (<c>RoomDifficulty</c>, written once per room by <c>Land.setLocDif</c>) and they are all
    /// difficulty scalars rather than per-object data, so a named group makes a call site read as
    /// "build with this land's difficulty" instead of three bare floats.</para>
    /// </summary>
    public readonly struct SecurityBuildContext
    {
        /// <summary>AS3 <c>loc.land.rnd</c> — the +2..+3 bonus (<c>Interact.as:505-508</c>).</summary>
        public readonly bool ProceduralLand;

        /// <summary>AS3 <c>loc.locksLevel</c> — <c>locDifLevel * 0.7</c> (<c>Land.as:984</c>).</summary>
        public readonly float LocksLevel;

        /// <summary>AS3 <c>loc.mechLevel</c> — <c>locDifLevel / 4</c> (<c>Land.as:985</c>).</summary>
        public readonly float MechLevel;

        /// <summary>AS3 <c>loc.locDifLevel</c> — scales the trap's blast (<c>Interact.as:546</c>).</summary>
        public readonly float LocDifLevel;

        public SecurityBuildContext(
            bool proceduralLand = false,
            float locksLevel = 0f,
            float mechLevel = 0f,
            float locDifLevel = 0f)
        {
            ProceduralLand = proceduralLand;
            LocksLevel = locksLevel;
            MechLevel = mechLevel;
            LocDifLevel = locDifLevel;
        }

        /// <summary>
        /// Authored-difficulty context — every scalar zero, so the rolls collapse to the authored
        /// value. This is what an offline fixture wants; a live room must pass its own numbers.
        /// </summary>
        public static SecurityBuildContext Authored => default;
    }

    /// <summary>
    /// The lockpicking / terminal-hacking arithmetic — a line-by-line port of
    /// <c>serv/Interact.as</c> (<c>:492-557</c> construction, <c>:889-1263</c> the attempt,
    /// <c>:1484-1545</c> the runa bypass, <c>:1697-1718</c> the <c>hack</c>/<c>unlock</c> commands).
    ///
    /// <para><b>Why this is a separate, Unity-free class.</b> The same reason
    /// <c>NoiseMath</c>/<c>StealthMath</c>/<c>LurkMath</c> are: the oracle's control flow here is
    /// <i>inverted</i> relative to how it reads, and the only way to prove a port got it right is to
    /// assert it offline against a scripted RNG. See <c>SecurityRulesTests</c>.</para>
    ///
    /// <para><b>The inversion, stated once.</b> <c>Interact.act()</c> computes
    /// <c>_loc2_ = 1 - getChance(lock - skill)</c> and takes the <c>if (random() &lt; _loc2_)</c> branch
    /// on a <b>hit</b>. That branch is the <b>failure</b>: it drains <c>lockHP</c>, snaps a pin, and at
    /// <c>lockHP &lt;= 0</c> calls <c>setAct("lock",102)</c> — the lock <b>seizes</b>. The <c>else</c>
    /// branch is the success and calls <c>setAct("lock",101)</c> — the lock <b>opens</b>
    /// (<c>:1005-1244</c>). A port that opens the lock when the pool empties has the mechanic exactly
    /// backwards, and it will still look plausible in a one-attempt test.</para>
    ///
    /// <para><b>Consequence for balance:</b> failing at a lock is not free. Every failed attempt
    /// damages the lock, and a lock that takes too many failures is lost for the rest of the run. That
    /// is why <c>Pers.lockAtt</c> is a <i>penalty multiplier</i> that a perk <b>lowers</b>
    /// (<c>AllData.as:5587</c>, <c>v0='1' v1='0.75'</c>) rather than a progress bonus.</para>
    /// </summary>
    public static class SecurityRules
    {
        /// <summary>AS3 <c>Interact.chanceUnlock</c> (<c>:14</c>) — success by <c>delta + 2</c>.</summary>
        public static readonly float[] ChanceUnlock = { 0.9f, 0.75f, 0.5f, 0.3f, 0.15f, 0.05f, 0.01f };

        /// <summary>AS3 <c>Interact.chanceUnlock2</c> (<c>:16</c>) — the table <c>Pers.upChance</c> swaps
        /// in.</summary>
        public static readonly float[] ChanceUnlock2 = { 0.95f, 0.8f, 0.55f, 0.35f, 0.2f, 0.08f, 0.03f };

        /// <summary>AS3 <c>Interact.maxLockLvl</c> (<c>:182</c>).</summary>
        public const int MaxLockLevel = 24;

        /// <summary>AS3 <c>Interact.maxMechLvl</c> (<c>:184</c>) — the cap for every non-pick tip and
        /// for the mine.</summary>
        public const int MaxMechanicalLevel = 7;

        /// <summary>
        /// AS3 <c>Interact.explRadius:Number = 260</c> (<c>:66</c>) — the trap blast's radius in
        /// <b>pixels</b>, used by <c>explosion()</c> when a defusal goes wrong (<c>:1419-1427</c>).
        /// </summary>
        public const float TrapBlastRadiusPixels = 260f;

        /// <summary>AS3's <c>setAct("lock",101)</c> — unlocked.</summary>
        public const int SaveLockOpen = 101;

        /// <summary>AS3's <c>setAct("lock",102)</c> — seized. The lock is set to 100, which every entry
        /// guard refuses.</summary>
        public const int SaveLockSeized = 102;

        /// <summary>The <c>lock</c> value a seized lock carries — <c>Interact.as:818-819</c>.</summary>
        public const int SeizedLockValue = 100;

        // =====================================================================================
        // Chance
        // =====================================================================================

        /// <summary>
        /// AS3 <c>Interact.getChance(delta)</c> (<c>:1248-1263</c>), verbatim.
        /// <paramref name="delta"/> is <b><c>lock − skill</c></b> (positive = harder than the actor).
        /// </summary>
        public static float GetChance(int delta, bool upChance)
        {
            if (delta < -2) return 1f;
            if (delta > 4) return 0f;

            int index = delta + 2;
            return upChance ? ChanceUnlock2[index] : ChanceUnlock[index];
        }

        // =====================================================================================
        // Skill selection
        // =====================================================================================

        /// <summary>
        /// The skill a lock tip is rolled against. The values are <b>already resolved</b> by
        /// <c>CharacterStats.GetLockTip</c> — including the <b>-100</b> sentinel that a lockpick without
        /// the tool answers (<c>Pers.as:2318-2322</c>), which the caller's <c>unlock &gt; -99</c> gate
        /// turns into "no possibility" (<c>Interact.as:1009</c>, <c>:1234</c>).
        /// </summary>
        public static int SkillFor(SecurityLockTip tip, in SecurityActor actor)
        {
            switch (tip)
            {
                case SecurityLockTip.Pick: return actor.SkillPick;
                case SecurityLockTip.Terminal: return actor.SkillTerminal;
                case SecurityLockTip.Mine: return actor.SkillMine;
                // AS3 `getLockTip(6)` answers `Pers.signal` (Pers.as:2340-2343) — a DIFFERENT skill from
                // the mine's `remine`, trained separately. Folding the two together would make a
                // demolition expert automatically a signal expert.
                case SecurityLockTip.Signal: return actor.SkillSignal;
                case SecurityLockTip.RepairWeapon:
                case SecurityLockTip.RepairPart: return actor.SkillRepair;
                default: return 0;
            }
        }

        /// <summary>
        /// AS3 <c>Pers.getLockMaster(tip)</c> (<c>Pers.as:2356-2367</c>). Everything but a pick and a
        /// terminal answers <b>100</b>, i.e. the master gate never fires for it.
        /// </summary>
        public static int MasterFor(SecurityLockTip tip, in SecurityActor actor)
        {
            switch (tip)
            {
                case SecurityLockTip.Pick: return actor.MasterPick;
                case SecurityLockTip.Terminal: return actor.MasterTerminal;
                default: return 100;
            }
        }

        // =====================================================================================
        // Entry guards
        // =====================================================================================

        /// <summary>
        /// Whether a press should roll at all, and if not, why. The order is the oracle's:
        /// seized first (<c>:1004</c> <c>lock &lt; 100</c>), then the key (<c>:980</c>), then
        /// "no possibility" (<c>:1009</c>).
        /// </summary>
        public static SecurityAttemptKind Classify(SecurityProfile profile, in SecurityActor actor, bool hasKey)
        {
            if (profile == null) return SecurityAttemptKind.None;

            // `else if(this.lock < 100)` — a seized lock is refused before anything else.
            if (profile.Lock >= SeizedLockValue) return SecurityAttemptKind.None;

            // `lock == 0` means "not locked": actOsn() runs directly (`:1240-1243`).
            if (profile.Lock <= 0) return SecurityAttemptKind.None;

            // A key is used without a roll, and is NOT consumed (`:980-1004`).
            if (!string.IsNullOrEmpty(profile.LockKey) && hasKey) return SecurityAttemptKind.KeyUsed;

            // `locktip == 0` is key-only: UnitPlayer.actAction refuses to even arm the timer
            // (`:1970-1973`), so reaching here means no key.
            if (profile.LockTip == SecurityLockTip.Key) return SecurityAttemptKind.NeedKey;

            if (SkillFor(profile.LockTip, actor) <= -99) return SecurityAttemptKind.NoSkill;

            return SecurityAttemptKind.None;
        }

        // =====================================================================================
        // The attempt
        // =====================================================================================

        /// <summary>
        /// One press on a lock — AS3 <c>Interact.act()</c>'s lock half (<c>:1005-1244</c>).
        ///
        /// <para><b>RNG call order is load-bearing</b> and matches the oracle:
        /// (1) the failure roll, (2) the pin-survival roll when a pin is carried, (3) the damage
        /// roll. A scripted RNG that returns one value cannot tell them apart, which is why the tests
        /// use a queue.</para>
        ///
        /// <para>Mutates <paramref name="profile"/> — the pool, the attempt counter and the seized
        /// flag all live on the object, exactly as they live on <c>Interact</c>.</para>
        /// </summary>
        public static SecurityAttemptResult Resolve(
            SecurityProfile profile,
            in SecurityActor actor,
            IRngService rng,
            bool hasPin = true,
            bool hasKey = false)
        {
            var result = new SecurityAttemptResult { Kind = SecurityAttemptKind.None };
            if (profile == null) return result;

            SecurityAttemptKind gate = Classify(profile, actor, hasKey);
            if (gate != SecurityAttemptKind.None)
            {
                result.Kind = gate;
                switch (gate)
                {
                    case SecurityAttemptKind.KeyUsed:
                        ApplyUnlocked(profile);
                        result.MessageKey = profile.LockTip == SecurityLockTip.Terminal
                            ? "unTermLock"
                            : "unLockKey";
                        break;
                    case SecurityAttemptKind.NeedKey:
                        result.MessageKey = "noKey";
                        break;
                    case SecurityAttemptKind.NoSkill:
                        result.MessageKey = "noPoss";
                        break;
                }

                return result;
            }

            int skill = SkillFor(profile.LockTip, actor);
            int master = MasterFor(profile.LockTip, actor);
            int delta = profile.Lock - skill;
            bool upChance = actor.UpChance > 0f;

            result.Delta = delta;
            result.SuccessChance = GetChance(delta, upChance);

            // `_loc2_` is the FAILURE probability.
            float failChance = 1f - result.SuccessChance;

            // `if(this.master < this.lockLevel) _loc2_ = 1;` (:1017-1020) — a quality tier above your
            // mastery is a guaranteed failure, NOT a guaranteed attempt.
            if (master < profile.LockLevel)
            {
                failChance = 1f;
                result.SuccessChance = 0f;
                result.MasterGate = true;
            }

            // (1) the failure roll
            if (rng.NextFloat() < failChance)
            {
                switch (profile.LockTip)
                {
                    case SecurityLockTip.Pick:
                    case SecurityLockTip.RepairPart:
                        ResolvePickFailure(profile, actor, rng, delta, result, hasPin);
                        break;
                    case SecurityLockTip.Terminal:
                        ResolveTerminalFailure(profile, actor, result);
                        break;
                    case SecurityLockTip.RepairWeapon:
                        ResolveRepairFailure(profile, delta, rng, result);
                        break;
                    default:
                        // Tip 0/3/6 cannot reach here (Classify refuses 0; 3 is the mine path; 6 is a
                        // mine tip, not a lock tip). Treated as a plain damage roll so a data typo is
                        // visible rather than silent.
                        ResolveRepairFailure(profile, delta, rng, result);
                        break;
                }

                return result;
            }

            // (2) success — `setAct("lock",101)` (:1185).
            ApplyUnlocked(profile);
            result.Kind = SecurityAttemptKind.Opened;
            result.MessageKey = profile.LockTip switch
            {
                SecurityLockTip.Terminal => "unTermLock",
                SecurityLockTip.RepairWeapon => "unRepLock",
                SecurityLockTip.RepairPart => "unFixLock",
                _ => "unLock",
            };
            return result;
        }

        /// <summary>
        /// AS3 <c>:1014-1109</c> — the pick/part failure branch. Damage band, pin, pool.
        /// </summary>
        private static void ResolvePickFailure(
            SecurityProfile profile,
            in SecurityActor actor,
            IRngService rng,
            int delta,
            SecurityAttemptResult result,
            bool hasPin)
        {
            // `_loc3_ = 0; _loc4_ = 2;` then the band by delta (:1021-1030).
            float baseDamage = 0f;
            float spread = 2f;
            if (delta == 1) { baseDamage = 1f; spread = 3f; }
            else if (delta > 1) { baseDamage = 2f; spread = 4f; }

            bool pinBroke = false;

            // `if(World.w.invent.pin.kol > 0)` — the branch only exists when a pin is carried; without
            // one the damage gains a flat +2 instead (:1079-1082).
            if (profile.LockTip == SecurityLockTip.Pick && hasPin && actor.Pins > 0)
            {
                // (2) the pin-survival roll: `pinBreak >= 1 || random() < pinBreak` SNAPS the pin.
                if (actor.PinBreak >= 1f || rng.NextFloat() < actor.PinBreak)
                {
                    pinBroke = true;
                }
            }
            else if (profile.LockTip == SecurityLockTip.Pick)
            {
                baseDamage += 2f;
            }

            // (3) the damage roll: `_loc5_ = (_loc3_ + random() * _loc4_) * pers.lockAtt` (:1083).
            float damage = (baseDamage + rng.NextFloat() * spread) * actor.LockAtt;
            profile.LockHp -= damage;

            result.Damage = damage;
            result.LockHpAfter = profile.LockHp;
            result.PinBroke = pinBroke;

            if (profile.LockHp <= 0f)
            {
                ApplySeized(profile);
                result.Kind = SecurityAttemptKind.Jammed;
                result.MessageKey = profile.LockTip == SecurityLockTip.RepairPart
                    ? "unFixZhopa"
                    : "unLockZhopa";
                return;
            }

            if (pinBroke)
            {
                result.Kind = SecurityAttemptKind.PinBroke;
                result.MessageKey = "unLockFailP";
                return;
            }

            // `else if(this.lockHP > 100) "unLockFailA"` — a pool that big absorbs the damage with
            // nothing to show for it (:1100-1103).
            if (profile.LockHp > 100f)
            {
                result.Kind = SecurityAttemptKind.FailedNoVisibleDamage;
                result.MessageKey = "unLockFailA";
                return;
            }

            result.Kind = SecurityAttemptKind.Failed;
            result.MessageKey = profile.LockTip == SecurityLockTip.RepairPart ? "unFixFail" : "unLockFail";
        }

        /// <summary>
        /// AS3 <c>:1110-1141</c> — the terminal failure branch. No damage pool; the container's own
        /// <c>lockAtt</c> is an <b>attempt counter</b>, seeded from <c>Pers.hackAtt</c> on first use.
        /// </summary>
        private static void ResolveTerminalFailure(
            SecurityProfile profile,
            in SecurityActor actor,
            SecurityAttemptResult result)
        {
            // `if(this.lockAtt == -100) this.lockAtt = World.w.pers.hackAtt;` (:1112-1115)
            if (Mathf.Approximately(profile.LockAtt, -100f))
            {
                profile.LockAtt = actor.HackAtt;
            }

            profile.LockAtt -= 1f;
            result.LockAttAfter = profile.LockAtt;

            // `> 10` is the "the system is too complex to even report" band (:1117-1120). It exists
            // because `lockatt` can be authored above the player's hackAtt.
            if (profile.LockAtt > 10f)
            {
                result.Kind = SecurityAttemptKind.FailedNoVisibleDamage;
                result.MessageKey = "unTermLockFail2";
                return;
            }

            if (profile.LockAtt > 1f)
            {
                result.Kind = SecurityAttemptKind.Failed;
                result.MessageKey = "unTermLockFail";
                return;
            }

            if (Mathf.Approximately(profile.LockAtt, 1f))
            {
                result.Kind = SecurityAttemptKind.Failed;
                result.MessageKey = "unTermLockFail1";
                return;
            }

            // `<= 0` — `setAct("lock",102)`, the terminal is blocked permanently (:1131-1140).
            ApplySeized(profile);
            result.Kind = SecurityAttemptKind.LockedOut;
            result.MessageKey = "unLockBlock";
        }

        /// <summary>
        /// AS3 <c>:1142-1181</c> — the repair branches. Same damage band as the pick, but no pin and no
        /// <c>lockAtt</c> multiplier; a weapon repair additionally <b>discharges</b> electricity at the
        /// player.
        /// </summary>
        private static void ResolveRepairFailure(
            SecurityProfile profile,
            int delta,
            IRngService rng,
            SecurityAttemptResult result)
        {
            float baseDamage;
            float spread = 4f;
            if (delta < 0) baseDamage = 0.1f;
            else if (delta == 0) baseDamage = 0.25f;
            else if (delta == 1) baseDamage = 0.6f;
            else if (delta == 2) baseDamage = 0.85f;
            else baseDamage = 1f;

            float damage = baseDamage + rng.NextFloat() * spread;
            profile.LockHp -= damage;

            result.Damage = damage;
            result.LockHpAfter = profile.LockHp;

            if (profile.LockHp <= 0f)
            {
                ApplySeized(profile);
                result.Kind = SecurityAttemptKind.Jammed;
                result.MessageKey = profile.LockTip == SecurityLockTip.RepairWeapon
                    ? "unRepZhopa"
                    : "unFixZhopa";
                return;
            }

            result.Kind = SecurityAttemptKind.Failed;
            result.MessageKey = profile.LockTip == SecurityLockTip.RepairWeapon
                ? "unRepFail"
                : "unFixFail";
        }

        /// <summary><c>setAct("lock",101)</c> (<c>:810-815</c>).</summary>
        public static void ApplyUnlocked(SecurityProfile profile)
        {
            if (profile == null) return;
            profile.SaveLock = SaveLockOpen;
            profile.Lock = 0;
            profile.Mine = 0;
        }

        /// <summary><c>setAct("lock",102)</c> (<c>:816-832</c>) — the pool is spent or the attempts are
        /// gone; the lock is 100 forever.</summary>
        public static void ApplySeized(SecurityProfile profile)
        {
            if (profile == null) return;
            profile.SaveLock = SaveLockSeized;
            profile.Lock = SeizedLockValue;
        }

        // =====================================================================================
        // Mines — resolved BEFORE the lock (Interact.as:911)
        // =====================================================================================

        /// <summary>
        /// AS3 <c>Interact.act()</c>'s mine branch (<c>:911-979</c>).
        ///
        /// <para><b>Two independent probabilities, and the disaster is the smaller one.</b>
        /// <c>_loc1_ = (mine − skill + 1) × 0.15</c> is the chance of the disaster (alarm or
        /// detonation), <c>_loc2_ = (mine − skill + 2) × 0.2</c> the chance of a plain failure. A
        /// disaster also fires <c>fiascoRemine</c>, which for <c>minetip == 6</c> is
        /// <see cref="InteractAlarm"/> — the signal trap.</para>
        /// </summary>
        public static SecurityAttemptResult ResolveMine(SecurityProfile profile, in SecurityActor actor, IRngService rng)
        {
            var result = new SecurityAttemptResult { Kind = SecurityAttemptKind.None };
            if (profile == null || profile.Mine <= 0) return result;

            int skill = SkillFor(profile.MineTip == 6 ? SecurityLockTip.Signal : SecurityLockTip.Mine, actor);
            int delta = profile.Mine - skill;
            result.Delta = delta;

            // `if(mine > unlock) _loc1_ = (mine - unlock + 1) * 0.15;` — note the guard: a mine at or
            // below your skill has NO disaster chance, only a failure chance.
            float disasterChance = delta > 0 ? (delta + 1) * 0.15f : 0f;
            float failChance = (delta + 2) * 0.2f;

            result.SuccessChance = Mathf.Clamp01(1f - Mathf.Max(disasterChance, failChance));

            float roll = rng.NextFloat();

            if (roll < disasterChance)
            {
                profile.Mine = 0;
                result.Kind = SecurityAttemptKind.Jammed;
                result.MessageKey = profile.MineTip == 6 ? "signalZhopa" : "remineZhopa";
                return result;
            }

            if (roll < failChance)
            {
                // The mine stays armed; nothing changes.
                result.Kind = SecurityAttemptKind.Failed;
                result.MessageKey = profile.MineTip == 6 ? "signalFail" : "remineFail";
                return result;
            }

            profile.Mine = 0;
            result.Kind = SecurityAttemptKind.Opened;
            result.MessageKey = profile.MineTip == 6 ? "signalOff" : "remine";
            return result;
        }

        /// <summary>The message key <see cref="ResolveMine"/> raises when a signal trap goes wrong.</summary>
        public const string InteractAlarm = "alarm";

        // =====================================================================================
        // Runa / reboot — the bypass for a lock you cannot make
        // =====================================================================================

        /// <summary>
        /// AS3 <c>Interact.needRuna</c> (<c>:1484-1509</c>) — whether the alternate route should be
        /// offered. It is offered only when the actor <b>cannot</b> make the roll
        /// (<c>lock − skill &gt; 1 || lockLevel &gt; master</c>) and carries the item.
        /// </summary>
        public static bool NeedBypass(SecurityProfile profile, in SecurityActor actor)
        {
            if (profile == null || profile.Lock <= 0 || profile.Lock >= SeizedLockValue) return false;

            // `if(this.mineTip == 6 && this.mine > 0) return 1;` — a live signal trap is bypassable by
            // the runa too (:1486-1489). Handled first by the caller; kept here for completeness.
            if (profile.MineTip == 6 && profile.Mine > 0) return true;

            int skill = SkillFor(profile.LockTip, actor);
            int master = MasterFor(profile.LockTip, actor);
            bool outmatched = profile.Lock - skill > 1 || profile.LockLevel > master;
            if (!outmatched) return false;

            return profile.LockTip switch
            {
                SecurityLockTip.Pick => actor.Runa > 0,
                SecurityLockTip.Terminal => actor.Reboot > 0,
                _ => false,
            };
        }

        /// <summary>
        /// AS3 <c>Interact.useRuna</c> (<c>:1511-1545</c>) — consumes the item and clears the lock.
        /// Returns the message key, or null when nothing was consumed.
        /// </summary>
        public static string ApplyBypass(SecurityProfile profile, ref SecurityActor actor)
        {
            if (profile == null) return null;

            if (profile.LockTip == SecurityLockTip.Pick && profile.Lock > 0 && actor.Runa > 0)
            {
                ApplyUnlocked(profile);
                actor.Runa -= 1;
                return "useRuna";
            }

            if (profile.LockTip == SecurityLockTip.Terminal && profile.Lock > 0 && actor.Reboot > 0)
            {
                ApplyUnlocked(profile);
                actor.Reboot -= 1;
                return "useReboot";
            }

            return null;
        }

        // =====================================================================================
        // The `hack` verb — what a hack_lock terminal broadcasts
        // =====================================================================================

        /// <summary>
        /// AS3 <c>Interact.command("hack")</c> (<c>:1699-1710</c>): clears the lock <b>and</b> the mine,
        /// then runs <c>actOsn()</c> when the object is <c>once</c>. Refuses unless <c>is_hack</c>, which
        /// is why a terminal cannot open an arbitrary crate.
        /// </summary>
        public static bool ApplyHackCommand(SecurityProfile profile)
        {
            if (profile == null || !profile.IsHackable) return false;

            profile.Mine = 0;
            ApplyUnlocked(profile);
            return true;
        }

        // =====================================================================================
        // Construction — Interact's constructor, :186-597
        // =====================================================================================

        /// <summary>
        /// The authored <c>key</c> this lock answers to — <c>Interact.as:350-352</c>
        /// (<c>if(this.xml.@key.length()) this.lockKey = this.xml.@key;</c>), placement over definition.
        ///
        /// <para><b>Why this is read from the object every time rather than carried on the profile.</b>
        /// Every other lock field is <i>resolved</i> — <c>lock</c> is clamped and randomised
        /// (<c>:505-516</c>), so it must be snapshotted once and saved or the same door changes
        /// difficulty while the player walks away. <c>key</c> is not resolved at all: it is a constant
        /// from the room asset. Snapshotting it therefore bought nothing and cost correctness, because
        /// <c>SecurityRuntime.StoreProfile</c> never wrote it and <c>LoadProfile</c> built a fresh
        /// profile from the stored fields — so <b>the key was discarded on the very first read</b>,
        /// including the read inside the same call that had just built it.</para>
        ///
        /// <para><b>What that looked like in play.</b> <c>nio</c>'s <c>door3</c> is
        /// <c>lock="20" locktip="0" key="key_sewer"</c>; <c>locktip="0"</c> makes it key-only, so it can
        /// never be picked. The boss died, dropped <c>key_sewer</c>, the player took it
        /// (<c>[WorldItemPickup] Took 1 × 'key_sewer'</c>) — and the door still answered
        /// <c>[Security] door3: noKey</c>, because the profile it consulted had forgotten which key it
        /// wanted. Holding the right key could not change anything: <c>Classify</c> only reports
        /// <c>KeyUsed</c> when <c>LockKey</c> is non-empty (<c>:135</c>), so an empty key fell through
        /// to the key-only refusal (<c>:139</c>).</para>
        /// </summary>
        public static string ReadLockKey(ObjectInstance obj)
        {
            if (obj == null) return string.Empty;

            // Definition first, placement second — the same precedence as the rest of Build. A
            // placement only wins where it actually authors the attribute, which is what the
            // null-fallback form of GetAttribute gives.
            string key = obj.definition != null ? obj.definition.GetAttribute("key", null) : null;
            key = obj.GetAttribute("key", key);
            return key ?? string.Empty;
        }

        /// <summary>
        /// AS3 <c>Interact.at_once</c> — whether one press is the whole interaction, so the
        /// <c>actOsn()</c> that opens the object runs in the <i>same</i> press.
        ///
        /// <para><b>Read from the definition row only, and that is the oracle's shape.</b>
        /// <c>at_once</c> is assigned in exactly one place — <c>if(param2.@once.length())</c>
        /// (<c>:296-299</c>) — and <c>param2</c> is the <c>AllData</c> row. The placement block
        /// (<c>:321-468</c>) has no <c>@once</c> read at all, so a placement that authors <c>once</c>
        /// changes nothing. Measured: 4 of the 202 <c>&lt;obj&gt;</c> rows author it
        /// (<c>door4</c>, <c>enclpole</c>, <c>robocell</c>, <c>alarm</c>).</para>
        ///
        /// <para><b>Why the security layer needs it.</b> The mine branch ends the press — but for a
        /// <c>once</c> object it falls through to <c>actOsn()</c> first (<c>:935-938</c> after a
        /// disaster, <c>:969-972</c> after a success). <c>alarm</c> is the only row that is both mined
        /// and <c>once</c>, and for it "open" means "disabled" — which is exactly what defusing the
        /// signal trap is for. Treating every mine press as terminal made the player press twice.</para>
        /// </summary>
        public static bool ReadAtOnce(ObjectInstance obj)
        {
            if (obj == null) return false;

            string raw = obj.definition != null ? obj.definition.GetAttribute("once", null) : null;
            return ParseFloat(raw, 0f) > 0f;
        }

        /// <summary>
        /// Build a profile from an object's <b>definition</b> row overridden by its <b>placement</b>,
        /// then rolled once, exactly as <c>Interact</c>'s constructor does.
        ///
        /// <para><b>Order matters and is the oracle's.</b> The definition is read first
        /// (<c>:208-320</c>, <c>param2</c>), the placement second (<c>:321-468</c>, <c>this.xml</c>),
        /// and each read is guarded by <c>.length()</c> — so a placement only wins where it actually
        /// authors the attribute. Then the post-processing block (<c>:492-557</c>) clamps and
        /// <i>randomises</i>.</para>
        ///
        /// <para><b>The randomisation is why this must run once and be saved.</b> <c>lock</c> can gain
        /// up to +3 on a procedural land and <c>lockLevel</c> is rolled from <c>lock</c>
        /// (<c>:505-516</c>). Re-rolling on every room rebuild would make a lock change difficulty
        /// while the player walks away and back.</para>
        ///
        /// <para><b>Two of these rolls decide whether the thing exists at all</b>, and the port used to
        /// skip both: <c>lockch</c> (<c>:219</c>) is the probability that this placement has a lock, and
        /// <c>minech</c> (<c>:253-258</c>) the probability that it has a trap. Without them every
        /// <c>door1</c> was locked (the row authors <c>lockch='0.3'</c>) and every <c>safe</c> was mined
        /// (<c>minech='0.6'</c>). Those two numbers are the difference between a game where a lock is an
        /// obstacle you meet and one where it is a tax you pay on every door.</para>
        /// </summary>
        public static SecurityProfile Build(
            ObjectInstance obj,
            in SecurityBuildContext context,
            IRngService rng,
            SecurityProfile into = null)
        {
            SecurityProfile p = into ?? new SecurityProfile();
            if (obj == null) return p;

            // `var _loc5_:* = true; if(this.xml && this.xml.@set.length()) _loc5_ = false;` (:195-199).
            // A placement that authors `set` is a hand-placed override, and the oracle then takes the
            // *unrandomised* `floor(authored)` arm for both the lock and the mine. No shipped placement
            // carries `set` (measured: 0 in 640 room assets), but the branch is one line and its absence
            // would be invisible until someone authored one.
            bool roll = string.IsNullOrEmpty(obj.GetAttribute("set", null));

            // ── locktip, first ─────────────────────────────────────────────────────────────────
            // `:200-207`. It must be resolved BEFORE the lock roll, because the roll branches on it:
            // `lockTip == 1 || 2` uses `locksLevel`, everything else `mechLevel`. The old port read the
            // placement's `locktip` after rolling, so a placement that changed the tip changed the
            // skill but not the difficulty band it was rolled in.
            MapObjectDefinition definition = obj.definition;
            p.LockTip = ParseTip(definition?.GetAttribute("locktip", null), p.LockTip);
            p.LockTip = ParseTip(obj.GetAttribute("locktip", null), p.LockTip);

            // ── definition row first ────────────────────────────────────────────────────────────
            if (definition != null)
            {
                // `if(param2.@lock.length())` (:214) — a row with no `lock` has none, which is why
                // 161 of the 202 imported rows are never locked no matter how many times they are
                // placed.
                string authoredLockRaw = definition.GetAttribute("lock", null);
                if (authoredLockRaw != null)
                {
                    float authored = ParseFloat(authoredLockRaw, 0f);

                    if (roll)
                    {
                        // `if(param2.@lockch.length() == 0 || random() < lockch)` (:219) — **the
                        // probability that this particular placement has a lock at all**. This is the
                        // line that made "every door is locked" in the port: `door1` authors
                        // `lockch='0.3'`, `door1a` 0.6, `door1b` 0.8, so in the oracle most wooden
                        // doors are simply not locked. Skipping the roll gave every one of them a lock.
                        string lockChance = definition.GetAttribute("lockch", null);
                        bool hasLock = lockChance == null || rng.NextFloat() < ParseFloat(lockChance, 0f);

                        if (hasLock)
                        {
                            if (p.LockTip == SecurityLockTip.Pick || p.LockTip == SecurityLockTip.Terminal)
                            {
                                // `floor(lock + (0.3 + random()) * loc.locksLevel)` (:223)
                                p.Lock = Mathf.FloorToInt(
                                    authored + (0.3f + rng.NextFloat()) * context.LocksLevel);
                            }
                            else
                            {
                                // `floor(lock + random() * loc.mechLevel)` (:227)
                                p.Lock = Mathf.FloorToInt(authored + rng.NextFloat() * context.MechLevel);
                            }
                        }

                        // `if(random() < lock - floor(lock)) lock += 1;` (:230-233).
                        //
                        // <para><b>This sits OUTSIDE the `lockch` gate, and that is the oracle, not a
                        // slip.</b> A row authored `lock='1.3'` therefore gives a "no lock" door a lock
                        // of 1 with 30 % probability — the fractional part survives the miss. Reproducing
                        // it is the whole point of the branch: a port that moved it inside the gate
                        // would make those doors reliably open, which is a different game.</para>
                        if (rng.NextFloat() < authored - Mathf.Floor(authored))
                        {
                            p.Lock += 1;
                        }
                    }
                    else
                    {
                        p.Lock = Mathf.FloorToInt(authored);   // `:237`
                    }

                    p.SaveLock = p.Lock;   // `this.saveLock = this.lock;` (:244)
                }

                // `if(param2.@low.length()) this.low = param2.@low;` (:240-242) — the DEFINITION only.
                // There is no placement read of `low` anywhere in the constructor: the placement block
                // *clears* it (`:340`) and never sets it. Reading it from the placement as well would
                // resurrect the halving roll on exactly the locks the oracle exempts.
                p.Low = ParseFloat(definition.GetAttribute("low", null), p.Low);
                p.LockLevel = ParseInt(definition.GetAttribute("locklevel", null), p.LockLevel);
                p.LockHp = ParseFloat(definition.GetAttribute("lockhp", null), p.LockHp);

                // ── the trap: `if(param2.@mine.length())` (:249-281) ────────────────────────────
                //
                // Two independent gates, and the port had neither. `minech` is the chance the trap
                // exists at all (`safe` 0.6, `wallsafe` 0.75), and the level is rolled. Reading `mine`
                // straight off the row — which is what the port did — gave **every** safe a live
                // explosive, so opening one was always a two-stage problem instead of sometimes one.
                string authoredMineRaw = definition.GetAttribute("mine", null);
                if (authoredMineRaw != null)
                {
                    int authoredMine = ParseInt(authoredMineRaw, 0);

                    if (roll)
                    {
                        string mineChance = definition.GetAttribute("minech", null);
                        bool armed = mineChance == null || rng.NextFloat() < ParseFloat(mineChance, 0f);

                        if (armed)
                        {
                            if (mineChance != null)
                            {
                                // `floor(random() * (mine + random()*mechLevel + 1))` (:257)
                                p.Mine = Mathf.FloorToInt(
                                    rng.NextFloat() *
                                    (authoredMine + rng.NextFloat() * context.MechLevel + 1f));
                            }
                            else
                            {
                                // `floor(mine + random()*mechLevel)` (:262)
                                p.Mine = Mathf.FloorToInt(
                                    authoredMine + rng.NextFloat() * context.MechLevel);
                            }

                            // `if(mine >= 2 && random() < 0.25) --mine;` (:264-267)
                            if (p.Mine >= 2 && rng.NextFloat() < 0.25f)
                            {
                                p.Mine -= 1;
                            }
                        }
                    }
                    else
                    {
                        p.Mine = authoredMine;   // `:271`
                    }

                    // `if(param2.@minetip.length()) … else if(param2.@inter != "3" && random() < 0.4)
                    //  minetip = 6;` (:273-280). The draw is taken whenever the row authors `mine`,
                    // even when the `minech` roll left the trap disarmed — the oracle's branch does not
                    // depend on `mine > 0`.
                    string authoredMineTip = definition.GetAttribute("minetip", null);
                    if (authoredMineTip != null)
                    {
                        p.MineTip = ParseInt(authoredMineTip, p.MineTip);
                    }
                    else if (!string.Equals(definition.GetAttribute("inter", null), "3", StringComparison.Ordinal)
                             && rng.NextFloat() < 0.4f)
                    {
                        p.MineTip = 6;
                    }
                }

                // `key` is read by ReadLockKey below, not here — it is the one lock field that is
                // re-derived from the object on every read instead of being snapshotted onto the
                // profile. See ReadLockKey for why.
                p.IsHackable = ParseFloat(definition.GetAttribute("hack", null), 0f) > 0f;
            }

            // ── placement override second ───────────────────────────────────────────────────────
            // `if(this.xml.@lock.length()) { this.lock = this.xml.@lock; ... this.low = 0; }` (:336-345)
            string placementLock = obj.GetAttribute("lock", null);
            if (placementLock != null)
            {
                p.Lock = Mathf.FloorToInt(ParseFloat(placementLock, p.Lock));
                p.SaveLock = p.Lock;   // `this.saveLock = this.lock;` (:339) — re-snapshotted.
                p.Low = 0f;
                if (p.Lock == 0) p.Mine = 0;
            }

            p.LockLevel = ParseInt(obj.GetAttribute("locklevel", null), p.LockLevel);
            p.LockHp = ParseFloat(obj.GetAttribute("lockhp", null), p.LockHp);
            p.LockAtt = ParseFloat(obj.GetAttribute("lockatt", null), p.LockAtt);
            p.Mine = ParseInt(obj.GetAttribute("mine", null), p.Mine);
            p.MineTip = ParseInt(obj.GetAttribute("minetip", null), p.MineTip);
            p.LockKey = ReadLockKey(obj);

            string hack = obj.GetAttribute("hack", null);
            if (hack != null) p.IsHackable = ParseFloat(hack, 0f) > 0f;

            // NOTE: no `low` read here, deliberately — see the definition block above. `low` is a
            // definition-row attribute in the oracle (:240-242), and the placement block only clears it.

            ApplyPostProcessing(p, context, rng);

            // NOTE: `SaveLock` is deliberately NOT re-stamped here. AS3 snapshots it at :244/:339 and
            // the post-processing block (:492-557) changes `lock` without touching `saveLock` — so a
            // save records the AUTHORED difficulty while the roll uses the clamped, possibly randomised
            // one. Re-stamping after the clamps would make `saveLock` a second copy of `lock` and lose
            // the only distinction the field carries.
            return p;
        }

        /// <summary>
        /// AS3 <c>:492-557</c> — caps, the <c>low</c> halving, the procedural bonus, the
        /// <c>lockLevel</c> roll and the trap's blast damage.
        /// </summary>
        private static void ApplyPostProcessing(SecurityProfile p, in SecurityBuildContext context, IRngService rng)
        {
            if (p.Lock < SeizedLockValue)
            {
                if (p.LockTip == SecurityLockTip.Pick || p.LockTip == SecurityLockTip.Terminal)
                {
                    // `if(this.low > 0 && random() < this.low) this.lock = ceil(this.lock * 0.5);`
                    if (p.Low > 0f && rng.NextFloat() < p.Low)
                    {
                        p.Lock = Mathf.CeilToInt(p.Lock * 0.5f);
                    }

                    if (p.Lock > MaxLockLevel) p.Lock = MaxLockLevel;

                    // `if(lock > 0 && low <= 0 && land.rnd && prob == null && random() < 0.2)
                    //     lock += floor(random()*2) + 2;`   (:505-508)
                    if (p.Lock > 0 && p.Low <= 0f && context.ProceduralLand && rng.NextFloat() < 0.2f)
                    {
                        p.Lock += Mathf.FloorToInt(rng.NextFloat() * 2f) + 2;
                    }

                    // `if(lock > 2 && lockLevel == 0) lockLevel = round(random()*(lock-2)/3.2)` capped 5
                    if (p.Lock > 2 && p.LockLevel == 0)
                    {
                        p.LockLevel = Mathf.RoundToInt(rng.NextFloat() * (p.Lock - 2) / 3.2f);
                        if (p.LockLevel > 5) p.LockLevel = 5;
                    }

                    p.AllDif = p.Lock + p.LockLevel * 2;
                }
                else
                {
                    if (p.Lock > MaxMechanicalLevel) p.Lock = MaxMechanicalLevel;
                    p.AllDif = p.Lock * 3;
                }
            }

            if (p.Mine > MaxMechanicalLevel) p.Mine = MaxMechanicalLevel;

            if (p.Mine > 0)
            {
                // `if(mineTip == 6) fiascoRemine = alarm; else { damage = mine*50*(0.8+random()*0.4)
                //  *(1 + loc.locDifLevel*0.1); fiascoRemine = explosion; }` (:540-548).
                //
                // A signal trap has no blast — its disaster is the alarm. An explosive trap's damage
                // is rolled HERE, once, and stored: re-rolling it at detonation would make the same
                // safe hurt differently on a second visit.
                if (p.MineTip != 6)
                {
                    p.TrapDamage = p.Mine * 50f * (0.8f + rng.NextFloat() * 0.4f) *
                                   (1f + context.LocDifLevel * 0.1f);
                }

                p.AllDif += p.Mine * 2;
            }
        }

        // =====================================================================================
        // Parsing helpers — the importer stores everything as strings
        // =====================================================================================

        private static SecurityLockTip ParseTip(string raw, SecurityLockTip fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return int.TryParse(raw, out int value) ? (SecurityLockTip)value : fallback;
        }

        private static int ParseInt(string raw, int fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return int.TryParse(raw, out int value) ? value : fallback;
        }

        private static float ParseFloat(string raw, float fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value)
                ? value
                : fallback;
        }
    }
}
