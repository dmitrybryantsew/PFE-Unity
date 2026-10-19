using System;
using PFE.Core.Rng;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Systems.Security
{
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
        /// </summary>
        public static SecurityProfile Build(
            ObjectInstance obj,
            bool proceduralLand,
            IRngService rng,
            SecurityProfile into = null)
        {
            SecurityProfile p = into ?? new SecurityProfile();
            if (obj == null) return p;

            // ── definition row first ────────────────────────────────────────────────────────────
            if (obj.definition != null)
            {
                p.LockTip = ParseTip(obj.definition.GetAttribute("locktip", null), p.LockTip);
                p.Lock = ParseInt(obj.definition.GetAttribute("lock", null), p.Lock);
                p.SaveLock = p.Lock;   // `this.saveLock = this.lock;` (:244) — the AUTHORED value.
                p.LockLevel = ParseInt(obj.definition.GetAttribute("locklevel", null), p.LockLevel);
                p.LockHp = ParseFloat(obj.definition.GetAttribute("lockhp", null), p.LockHp);
                p.Mine = ParseInt(obj.definition.GetAttribute("mine", null), p.Mine);
                p.MineTip = ParseInt(obj.definition.GetAttribute("minetip", null), p.MineTip);
                p.LockKey = obj.definition.GetAttribute("key", p.LockKey) ?? p.LockKey;
                p.IsHackable = ParseFloat(obj.definition.GetAttribute("hack", null), 0f) > 0f;

                // `if(param2.@low.length()) this.low = param2.@low;` (:240-242) — the DEFINITION only.
                // There is no placement read of `low` anywhere in the constructor: the placement block
                // *clears* it (`:340`) and never sets it. Reading it from the placement as well would
                // resurrect the halving roll on exactly the locks the oracle exempts.
                p.Low = ParseFloat(obj.definition.GetAttribute("low", null), p.Low);
            }

            // ── placement override second ───────────────────────────────────────────────────────
            // `if(this.xml.@lock.length()) { this.lock = this.xml.@lock; ... this.low = 0; }` (:336-345)
            string placementLock = obj.GetAttribute("lock", null);
            if (placementLock != null)
            {
                p.Lock = ParseInt(placementLock, p.Lock);
                p.SaveLock = p.Lock;   // `this.saveLock = this.lock;` (:339) — re-snapshotted.
                p.Low = 0f;
                if (p.Lock == 0) p.Mine = 0;
            }

            p.LockTip = ParseTip(obj.GetAttribute("locktip", null), p.LockTip);
            p.LockLevel = ParseInt(obj.GetAttribute("locklevel", null), p.LockLevel);
            p.LockHp = ParseFloat(obj.GetAttribute("lockhp", null), p.LockHp);
            p.LockAtt = ParseFloat(obj.GetAttribute("lockatt", null), p.LockAtt);
            p.Mine = ParseInt(obj.GetAttribute("mine", null), p.Mine);
            p.MineTip = ParseInt(obj.GetAttribute("minetip", null), p.MineTip);
            p.LockKey = obj.GetAttribute("key", p.LockKey) ?? p.LockKey;

            string hack = obj.GetAttribute("hack", null);
            if (hack != null) p.IsHackable = ParseFloat(hack, 0f) > 0f;

            // NOTE: no `low` read here, deliberately — see the definition block above. `low` is a
            // definition-row attribute in the oracle (:240-242), and the placement block only clears it.

            // `if(param2.@lockch.length() == 0 || random() < lockch)` — the placement's chance that the
            // lock exists at all (:493-495). Not rolled: a placement with `lock` always has one, and
            // the F2 tab's "Re-roll" is where a tester would otherwise look for it.
            ApplyPostProcessing(p, proceduralLand, rng);

            // NOTE: `SaveLock` is deliberately NOT re-stamped here. AS3 snapshots it at :244/:339 and
            // the post-processing block (:492-557) changes `lock` without touching `saveLock` — so a
            // save records the AUTHORED difficulty while the roll uses the clamped, possibly randomised
            // one. Re-stamping after the clamps would make `saveLock` a second copy of `lock` and lose
            // the only distinction the field carries.
            return p;
        }

        /// <summary>
        /// AS3 <c>:492-557</c> — caps, the <c>low</c> halving, the procedural bonus and the
        /// <c>lockLevel</c> roll.
        /// </summary>
        private static void ApplyPostProcessing(SecurityProfile p, bool proceduralLand, IRngService rng)
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
                    if (p.Lock > 0 && p.Low <= 0f && proceduralLand && rng.NextFloat() < 0.2f)
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
