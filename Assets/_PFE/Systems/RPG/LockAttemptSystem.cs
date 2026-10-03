using UnityEngine;
using PFE.Core.Rng;

namespace PFE.Systems.RPG
{
    /// <summary>
    /// Port of the lockpicking / terminal-hacking resolution core from AS3 <c>Interact.as</c>
    /// (<c>unlock()</c>, <c>:1000-1210</c>, and <c>getChance()</c>, <c>:1248-1262</c>).
    ///
    /// <para>
    /// This is the <b>consumer</b> that makes <c>CharacterStats.pinBreak</c>, <c>lockAtt</c>,
    /// <c>hackAtt</c>, <c>unlockMaster</c>, <c>hackerMaster</c> and <c>upChance</c> live. Before
    /// this existed the port wrote those fields and read none of them (see
    /// <c>docs/RPG_Step4_Open_Questions</c> and <c>TOPIC_lessons</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Scope note.</b> The AS3 routine also owns the container's own mutable <c>lockHP</c>,
    /// <c>unlock</c> progress counters and the <c>fiascoUnlock</c> callback. Those live on the
    /// call site's <see cref="LockState"/>, so this system stays stateless and the caller owns
    /// per-container state — the same split <see cref="SkillCheckSystem"/> uses.
    /// </para>
    /// </summary>
    public class LockAttemptSystem
    {
        /// <summary>
        /// The AS3 <c>chanceUnlock</c> table (<c>Interact.as:14</c>) — indexed by
        /// <c>(lockLevel - skillLevel) + 2</c>, clamped to <c>[-2, 4]</c>.
        /// </summary>
        private static readonly float[] ChanceUnlock = { 0.9f, 0.75f, 0.5f, 0.3f, 0.15f, 0.05f, 0.01f };

        /// <summary>
        /// The AS3 <c>chanceUnlock2</c> table (<c>Interact.as:16</c>) — used when
        /// <c>Pers.upChance &gt; 0</c>.
        /// </summary>
        private static readonly float[] ChanceUnlock2 = { 0.95f, 0.8f, 0.55f, 0.35f, 0.2f, 0.08f, 0.03f };

        private readonly CharacterStats _stats;
        private readonly IRngService _rng;
        private IRngService _fallback;

        public LockAttemptSystem(CharacterStats stats, IRngService rng = null)
        {
            _stats = stats;
            _rng = rng;
        }

        private IRngService Rng => _rng ?? (_fallback ??= new PcgRngService().GetStream(RngStream.Combat));

        /// <summary>
        /// Mutable per-container lock state. Mirrors the AS3 <c>Interact</c> instance fields
        /// <c>lock</c>, <c>lockHP</c>, <c>lockLevel</c>, <c>master</c>, <c>lockTip</c> and the local
        /// <c>lockAtt</c> (<c>Interact.as:44-52</c>), which starts at <b>-100</b> and is seeded
        /// from <c>Pers.hackAtt</c> on the first terminal attempt (<c>Interact.as:1112-1114</c>).
        ///
        /// <para>
        /// <b><c>Lock</c> and <c>LockHp</c> are two different oracle fields and must not be
        /// conflated.</b> AS3 declares <c>public var lock:int = 0</c> (<c>:44</c>) — the container's
        /// <i>difficulty</i>, a 0..99 value read by the entry guard at <c>:1004</c>
        /// (<c>else if(this.lock &lt; 100)</c>) and <c>:1009</c> (<c>if(this.lock &gt; 0)</c>) and by
        /// the <c>lock - unlock</c> delta at <c>:1015</c>. It is <b>never</b> decremented by an
        /// attempt. Separately, <c>public var lockHP:Number = 10</c> (<c>:52</c>) is the damage
        /// pool, decremented at <c>:1084</c>, <c>:1145</c>, <c>:1165</c>, tested <c>&lt;= 0</c> for
        /// <c>Opened</c> (<c>:1085</c>) and <c>&gt; 100</c> for the "unLockFailA" message
        /// (<c>:1100</c>). An earlier revision of this port had only <c>LockHp</c>, which made the
        /// guard reject any pool of 100+ as if it were an unbreakable difficulty — see
        /// <c>LockAttemptSystemTests</c>.
        /// </para>
        /// </summary>
        public class LockState
        {
            /// <summary>
            /// AS3 <c>Interact.lock</c> (<c>:44</c>) — the container's difficulty, an int in
            /// <c>0..99</c>. <c>0</c> means "already open" and <c>&gt;= 100</c> means "unbreakable";
            /// both are skipped by the entry guard (<c>:1004-1009</c>). Never decremented.
            /// </summary>
            public int Lock = 10;

            /// <summary>
            /// AS3 <c>Interact.lockHP</c> (<c>:52</c>) — the damage pool that attempts reduce.
            /// Starts at 10, defaults from <c>@lockhp</c> when the container XML carries one
            /// (<c>:247</c>, <c>:360</c>).
            /// </summary>
            public float LockHp = 10f;

            /// <summary>AS3 <c>Interact.lockLevel</c> — the level the container demands.</summary>
            public int LockLevel;

            /// <summary>
            /// AS3 <c>Interact.lockTip</c>: 1 = physical, 2 = terminal, 3 = mine,
            /// 4 = weapon repair, 5 = other repair, 6 = signal.
            /// </summary>
            public int LockTip = 1;

            /// <summary>
            /// AS3 <c>Interact.lockAtt</c> — the container's *own* attack counter, distinct from
            /// <c>Pers.lockAtt</c>. Starts at <b>-100</b> (unset) and is seeded from the player on
            /// the first terminal attempt.
            /// </summary>
            public float LockAtt = -100f;

            /// <summary>AS3 <c>Interact.master</c> — the master level recorded at interact time.</summary>
            public int Master;
        }

        /// <summary>Outcome of a single attempt, mirroring the AS3 branch structure.</summary>
        public enum AttemptOutcome
        {
            /// <summary>The roll failed — nothing happened (AS3 falls through the <c>Math.random() &lt; _loc2_</c> guard).</summary>
            Missed,
            /// <summary>Progress dealt but the lock held.</summary>
            Failed,
            /// <summary>Lock HP reached 0 — the container opened.</summary>
            Opened,
            /// <summary>Terminal attempts exhausted (AS3 <c>lockAtt</c> counted down to 1).</summary>
            TerminalLockedOut,
            /// <summary>A bobby pin snapped.</summary>
            PinBroke
        }

        /// <summary>
        /// Resolve one attempt against <paramref name="state"/>.
        /// Faithful port of <c>Interact.as:1000-1210</c> for <c>lockTip</c> 1 (physical) and
        /// 2 (terminal); other tips fall through to the plain "no stat term" damage path
        /// (<c>lockTip</c> 4/5, <c>:1138-1185</c>).
        /// </summary>
        public AttemptOutcome Attempt(LockState state, bool hasPin = true)
        {
            if (_stats == null || state == null)
            {
                Debug.LogError("[LockAttemptSystem] Missing CharacterStats or LockState.");
                return AttemptOutcome.Missed;
            }

            // AS3 Interact.as:1004-1009 — `else if(this.lock < 100) { if(this.lock > 0) ... }`.
            // This gate reads the container's *difficulty* (Interact.lock, :44), NOT the damage
            // pool (Interact.lockHP, :52). Conflating the two made a 100-point pool look like an
            // unbreakable lock; see the LockState remarks.
            if (state.Lock <= 0 || state.Lock >= 100)
            {
                return AttemptOutcome.Missed;
            }

            int skill = GetLockSkill(state.LockTip);

            // AS3 Interact.as:1010-1021 — the chance-of-hit setup, shared by tip 1 and tip 5.
            if (state.LockTip == 1 || state.LockTip == 5 || state.LockTip == 2)
            {
                float chance = 1f - GetChance(state.LockLevel - skill);

                // AS3: if the container's recorded master level is below the demanded level, the
                // roll always fires (i.e. it is *guaranteed to be attempted*, not guaranteed to win).
                if (state.Master < state.LockLevel)
                {
                    chance = 1f;
                }

                if (Rng.NextFloat() >= chance)
                {
                    return AttemptOutcome.Missed; // AS3: the outer `if (Math.random() < _loc2_)` missed
                }
            }

            // AS3 Interact.as:1035-1057 — the damage roll bounds (_loc3_ base, _loc4_ spread).
            float baseDamage = 0f;
            float spread = 2f;

            if (state.LockTip == 1 || state.LockTip == 5)
            {
                if (state.LockLevel - skill == 1)
                {
                    baseDamage = 1f;
                    spread = 3f;
                }
                else if (state.LockLevel - skill > 1)
                {
                    baseDamage = 2f;
                    spread = 4f;
                }
            }
            else if (state.LockTip == 4)
            {
                int delta = state.LockLevel - skill;
                if (delta < 0) baseDamage = 0.1f;
                else if (delta == 0) baseDamage = 0.25f;
                else if (delta == 1) baseDamage = 0.6f;
                else if (delta == 2) baseDamage = 0.85f;
                else baseDamage = 1f;
                spread = 4f;
            }

            // AS3 Interact.as:1058 — the running `_loc5_` that the branches overwrite.
            float progress = baseDamage;

            if (state.LockTip == 1)
            {
                return AttemptPhysical(state, baseDamage, spread, hasPin);
            }

            if (state.LockTip == 2)
            {
                return AttemptTerminal(state);
            }

            // AS3 lockTip 4/5: plain damage with no owner-stat term (Interact.as:1138-1185).
            progress = baseDamage + Rng.NextFloat() * spread;
            state.LockHp -= progress;
            return state.LockHp <= 0f ? AttemptOutcome.Opened : AttemptOutcome.Failed;
        }

        /// <summary>
        /// AS3 <c>Interact.as:1068-1105</c> — the physical branch. Reads
        /// <c>Pers.pinBreak</c> (pin survival) and <c>Pers.lockAtt</c> (progress multiplier).
        /// </summary>
        private AttemptOutcome AttemptPhysical(LockState state, float baseDamage, float spread, bool hasPin)
        {
            bool pinBroke = false;

            // AS3: `if (invent.pin.kol > 0)` — you may only snap a pin if you carry one.
            if (hasPin)
            {
                // AS3 Interact.as:1073: `pinBreak >= 1 || random() < pinBreak`. The body consumes
                // the pin (`minusItem("pin")`), so this guard is the probability the attempt SNAPS
                // the pin — NOT the probability it survives. Default pinBreak = 1 (Pers.as:289)
                // therefore means "a failed attempt always costs a pin"; the lockpick skill lowers
                // it (AllData.as:5588, v0='1' v1='0.5') so pins last longer.
                if (_stats.pinBreak >= 1f || Rng.NextFloat() < _stats.pinBreak)
                {
                    pinBroke = true;
                }
            }
            else
            {
                baseDamage += 2f; // AS3: no pin carried -> `_loc3_ += 2`
            }

            // AS3 Interact.as:1083: `_loc5_ = (_loc3_ + random() * _loc4_) * pers.lockAtt`
            float progress = (baseDamage + Rng.NextFloat() * spread) * _stats.lockAtt;
            state.LockHp -= progress;

            if (state.LockHp <= 0f)
            {
                return AttemptOutcome.Opened;
            }

            if (pinBroke)
            {
                return AttemptOutcome.PinBroke;
            }

            return AttemptOutcome.Failed;
        }

        /// <summary>
        /// AS3 <c>Interact.as:1107-1137</c> — the terminal branch. Uses the container's own
        /// <c>lockAtt</c> as an attempt counter, seeded from <c>Pers.hackAtt</c> on first use.
        /// </summary>
        private AttemptOutcome AttemptTerminal(LockState state)
        {
            // AS3: `if (this.lockAtt == -100) this.lockAtt = World.w.pers.hackAtt;`
            if (state.LockAtt == -100f)
            {
                state.LockAtt = _stats.hackAtt;
            }

            state.LockAtt -= 1f;

            // AS3 Interact.as:1117-1136: >10 = silent fail, >1 = "N attempts left",
            // ==1 = last attempt, <=0 = locked out.
            if (state.LockAtt > 10f)
            {
                return AttemptOutcome.Missed;
            }

            if (state.LockAtt > 1f)
            {
                return AttemptOutcome.Failed;
            }

            if (state.LockAtt == 1f)
            {
                return AttemptOutcome.Failed;
            }

            return AttemptOutcome.TerminalLockedOut;
        }

        /// <summary>
        /// AS3 <c>Interact.getChance</c> (<c>Interact.as:1248-1262</c>), verbatim. Indexes the
        /// table by <c>delta + 2</c> and picks <c>chanceUnlock2</c> when <c>Pers.upChance &gt; 0</c>.
        /// </summary>
        public float GetChance(int delta)
        {
            if (delta < -2) return 1f;
            if (delta > 4) return 0f;

            return _stats != null && _stats.upChance > 0f
                ? ChanceUnlock2[delta + 2]
                : ChanceUnlock[delta + 2];
        }

        /// <summary>
        /// The skill the lock type is checked against — AS3 <c>getLockTip()</c> as used by the
        /// <c>lock - unlock</c> comparison in <c>Interact.as:1015</c>. Mirrors
        /// <see cref="SkillCheckSystem.GetLockTip"/>'s mapping onto the port's skill ids.
        /// </summary>
        private int GetLockSkill(int lockTip)
        {
            switch (lockTip)
            {
                case 1: return _stats.lockPick;
                case 2: return _stats.hacker;
                case 3: return _stats.remine;
                case 4:
                case 5: return _stats.repair;
                case 6: return _stats.signal;
                default: return 0;
            }
        }
    }
}
