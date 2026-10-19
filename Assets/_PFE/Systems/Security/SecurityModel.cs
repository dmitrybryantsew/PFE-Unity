using System;

namespace PFE.Systems.Security
{
    /// <summary>
    /// AS3 <c>Interact.lockTip</c> — <b>which verb</b> the lock answers to.
    ///
    /// <para><b>This is not a difficulty.</b> The difficulty is <c>lock</c> and the damage pool is
    /// <c>lockhp</c>; the tip selects the skill, the message set and the failure consequence
    /// (<c>Interact.as:732-776</c> reads it for the prompt text, <c>:1014-1181</c> for the roll).
    /// Conflating the three is the mistake <c>LockAttemptSystem</c> already had to be corrected for
    /// once.</para>
    /// </summary>
    public enum SecurityLockTip
    {
        /// <summary>
        /// <c>locktip='0'</c> — key only. AS3 refuses the attempt outright
        /// (<c>UnitPlayer.as:1970-1973</c>: <c>if(lockTip == 0) return;</c>) and the only way through
        /// is <c>lockKey</c>.
        /// </summary>
        Key = 0,

        /// <summary><c>locktip='1'</c> (the default, <c>Interact.as:46</c>) — physical lockpicking.</summary>
        Pick = 1,

        /// <summary><c>locktip='2'</c> — a terminal; the "hack" verb. Failure burns attempts instead of
        /// damaging a pool (<c>Interact.as:1110-1141</c>).</summary>
        Terminal = 2,

        /// <summary><c>locktip='3'</c> — a mine. Handled by the mine path, not the lock path.</summary>
        Mine = 3,

        /// <summary><c>locktip='4'</c> — a damaged weapon/power node; failure discharges electricity
        /// (<c>Interact.as:1142-1161</c>).</summary>
        RepairWeapon = 4,

        /// <summary><c>locktip='5'</c> — a broken part (<c>Interact.as:1162-1181</c>).</summary>
        RepairPart = 5,

        /// <summary><c>locktip='6'</c> — a signal trap; failure raises the room alarm instead of
        /// exploding (<c>Interact.as:540-543</c>).</summary>
        Signal = 6,
    }

    /// <summary>
    /// What one press did. <b>The mapping is the whole point of this type</b>, because the oracle's
    /// control flow is easy to read backwards.
    ///
    /// <para><c>Interact.act()</c> rolls <c>Math.random() &lt; _loc2_</c> where <c>_loc2_</c> is
    /// <b>1 − the success chance</b> (<c>:1016</c>), and the branch that runs on a hit is the
    /// <i>failure</i> branch — the one that damages <c>lockHP</c>, snaps a pin and can set the lock to
    /// <c>102</c> (seized). The <c>else</c> branch is the success: <c>setAct("lock",101)</c> clears the
    /// lock (<c>:1183-1229</c>).</para>
    /// </summary>
    public enum SecurityAttemptKind
    {
        /// <summary>Nothing was attempted: the object is not locked, is already open, or is seized.</summary>
        None = 0,

        /// <summary>The roll failed and the pool absorbed it — "unLockFail" (<c>:1106</c>).</summary>
        Failed = 1,

        /// <summary>The roll failed and the pool reached 0 — the lock is <b>seized forever</b>
        /// ("unLockZhopa", <c>:1085-1094</c>). This is the outcome the old port called "opened".</summary>
        Jammed = 2,

        /// <summary>The roll succeeded — the lock is cleared ("unLock", <c>:1188</c>).</summary>
        Opened = 3,

        /// <summary>Terminal attempts exhausted — "unLockBlock" (<c>:1135</c>), also permanent.</summary>
        LockedOut = 4,

        /// <summary>The actor has no lockpicking ability at all — <c>unlock &lt;= -99</c>, "noPoss"
        /// (<c>:1234</c>).</summary>
        NoSkill = 5,

        /// <summary>The actor does not carry the key this lock names — the key branch is simply not
        /// taken (<c>:980</c>).</summary>
        NeedKey = 6,

        /// <summary>A key was used. <b>The key is not consumed</b> — AS3's key branch
        /// (<c>:980-1004</c>) only calls <c>setAct("lock",101)</c>.</summary>
        KeyUsed = 7,

        /// <summary>A pin snapped and the pool survived it — "unLockFailP" (<c>:1097</c>).</summary>
        PinBroke = 8,

        /// <summary>The failure was silent because the pool is above 100 — "unLockFailA"
        /// (<c>:1100-1103</c>).</summary>
        FailedNoVisibleDamage = 9,
    }

    /// <summary>
    /// The outcome of one attempt, with every number the UI and the F2 readout need.
    /// </summary>
    public struct SecurityAttemptResult
    {
        public SecurityAttemptKind Kind;

        /// <summary><c>lock − skill</c>, the value both the chance table and the damage band read
        /// (<c>Interact.as:1016</c>, <c>:1021-1030</c>).</summary>
        public int Delta;

        /// <summary><c>getChance(delta)</c> — the probability this attempt would succeed, before the
        /// roll. <c>0</c> when the master gate forces a failure.</summary>
        public float SuccessChance;

        /// <summary>Damage dealt to <c>lockHP</c> this attempt. 0 on success and on a terminal.</summary>
        public float Damage;

        /// <summary><c>lockHP</c> after the attempt.</summary>
        public float LockHpAfter;

        /// <summary><c>lockAtt</c> after the attempt (terminals only).</summary>
        public float LockAttAfter;

        /// <summary>True when the attempt snapped a bobby pin.</summary>
        public bool PinBroke;

        /// <summary>True when <c>master &lt; lockLevel</c> forced the failure (<c>Interact.as:1017-1020</c>).</summary>
        public bool MasterGate;

        /// <summary>The AS3 localisation key for this outcome — what <c>gui.infoText</c> prints.</summary>
        public string MessageKey;

        public bool Succeeded => Kind == SecurityAttemptKind.Opened || Kind == SecurityAttemptKind.KeyUsed;
        public bool Terminal => Kind == SecurityAttemptKind.Jammed || Kind == SecurityAttemptKind.LockedOut;
    }

    /// <summary>
    /// The mutable security state of <b>one map object</b> — AS3's <c>Interact</c> instance fields
    /// (<c>Interact.as:38-108</c>), resolved from the definition row and then overridden by the
    /// placement, in that order (<c>:200-320</c>).
    ///
    /// <para><b>Five numbers that are routinely confused.</b>
    /// <list type="bullet">
    /// <item><see cref="Lock"/> — <c>lock</c>, the <b>difficulty</b> (0..24 for a pick, 0..7 for a
    /// terminal). <c>0</c> = open, <c>&gt;= 100</c> = seized/unbreakable. Never decremented by an
    /// attempt.</item>
    /// <item><see cref="LockLevel"/> — <c>locklevel</c>, a <b>quality tier</b> (0..5) that the actor's
    /// <c>unlockMaster</c>/<c>hackerMaster</c> must meet. Meeting it is a precondition, not a
    /// bonus.</item>
    /// <item><see cref="LockHp"/> — <c>lockhp</c>, the <b>damage pool</b>. Failure drains it and
    /// draining it seizes the lock.</item>
    /// <item><see cref="LockAtt"/> — the terminal's <b>attempt counter</b>, seeded from
    /// <c>Pers.hackAtt</c> on first use (<c>:1112-1114</c>).</item>
    /// <item><see cref="Mine"/> — <c>mine</c>, a trap <i>on</i> the object; it is resolved before the
    /// lock (<c>:911</c>) and its failure consequence depends on <see cref="MineTip"/>.</item>
    /// </list></para>
    /// </summary>
    [Serializable]
    public class SecurityProfile
    {
        /// <summary><c>lock</c> — difficulty. 0 means "not locked"; &gt;= 100 means "seized".</summary>
        public int Lock;

        /// <summary><c>saveLock</c> — the value <c>save()</c> writes; 101 = unlocked, 102 = seized
        /// (<c>Interact.as:803-833</c>). Kept because a save must round-trip the distinction.</summary>
        public int SaveLock;

        /// <summary><c>locklevel</c> — the quality tier compared against the master skill.</summary>
        public int LockLevel;

        /// <summary><c>locktip</c> — which verb this lock answers to.</summary>
        public SecurityLockTip LockTip = SecurityLockTip.Pick;

        /// <summary><c>lockhp</c> — the damage pool. Default 10 (<c>Interact.as:52</c>).</summary>
        public float LockHp = 10f;

        /// <summary><c>lockatt</c> — the terminal attempt counter, or the object's own override of the
        /// damage roll. <b>-100 is AS3's "unset"</b> (<c>:50</c>), not zero.</summary>
        public float LockAtt = -100f;

        /// <summary>
        /// <c>low</c> — AS3 <c>Interact.low</c> (<c>:56</c>), the probability that a lock is built at
        /// <b>half</b> its authored difficulty (<c>:497-500</c>). A placement that authors its own
        /// <c>lock</c> clears it (<c>:340</c>), which is why an overridden lock is never halved.
        /// </summary>
        public float Low;

        /// <summary>
        /// <c>allDif</c> — the <b>derived</b> difficulty score AS3 saves and shows
        /// (<c>:519</c> <c>lock + lockLevel*2</c>, <c>:530</c> <c>lock*3</c>, <c>:551</c>
        /// <c>+= mine*2</c>). Not read by any roll; it exists so a UI can rank two locks.
        /// </summary>
        public int AllDif = -1;

        /// <summary><c>mine</c> — trap level on the object, 0 for none.</summary>
        public int Mine;

        /// <summary>
        /// <c>Interact.damage</c> when the trap is a real explosive — the blast this object's mine
        /// carries, resolved <b>once</b> at construction (<c>Interact.as:546</c>:
        /// <c>mine * 50 * (0.8 + random()*0.4) * (1 + locDifLevel*0.1)</c>).
        ///
        /// <para><b>Why it is stored and not recomputed at detonation.</b> The oracle rolls it in the
        /// constructor, so it is part of the object's resolved state exactly like <c>lock</c> and
        /// <c>lockhp</c> — a rebuild that re-rolled it would make the same trap hurt differently after
        /// a room reload. <c>0</c> means "no blast": either there is no mine, or <c>minetip == 6</c>
        /// (a signal trap raises the alarm instead of exploding, <c>:540-543</c>).</para>
        /// </summary>
        public float TrapDamage;

        /// <summary><c>minetip</c> — 6 means "signal": a failed defusal raises the alarm instead of
        /// exploding (<c>:540-543</c>).</summary>
        public int MineTip = 3;

        /// <summary><c>key</c> — the inventory id that opens this without a roll.</summary>
        public string LockKey = string.Empty;

        /// <summary><c>hack</c> &gt; 0 — the object can be cleared by the <c>hack</c> verb, which is what
        /// a <c>hack_lock</c> terminal broadcasts (<c>Interact.as:1699-1710</c>).</summary>
        public bool IsHackable;

        /// <summary>True once the lock is seized — <c>lock &gt;= 100</c> and unrecoverable.</summary>
        public bool Jammed => Lock >= 100;

        /// <summary>True when there is a lock to work at all.</summary>
        public bool IsLocked => Lock > 0 && Lock < 100;

        public SecurityProfile Clone()
        {
            return (SecurityProfile)MemberwiseClone();
        }
    }

    /// <summary>
    /// The actor's half of the roll — the <c>Pers</c> fields AS3 reads inside
    /// <c>Interact.act()</c>, snapshotted so the rules stay free of <c>CharacterStats</c>.
    ///
    /// <para><b>The skills are already resolved, not raw stats.</b> <see cref="SkillPick"/> is
    /// <c>Pers.getLockTip(1)</c> — i.e. <b>-100</b> when the actor has no lockpicking tool
    /// (<c>Pers.as:2318-2322</c>) — because that sentinel is what the <c>unlock &gt; -99</c> gate reads
    /// (<c>Interact.as:1009</c>). Keeping the raw rank here and re-deriving the sentinel inside the
    /// rules would put the same mapping in two places, and <c>CharacterStats.GetLockTip</c> already
    /// owns it.</para>
    /// </summary>
    public struct SecurityActor
    {
        /// <summary><c>getLockTip(1)</c> — the lockpick rank, or <b>-100</b> without the tool.</summary>
        public int SkillPick;

        /// <summary><c>getLockTip(2)</c> — the hacker rank.</summary>
        public int SkillTerminal;

        /// <summary><c>getLockTip(3)</c> — the demolitions rank.</summary>
        public int SkillMine;

        /// <summary><c>getLockTip(6)</c> — the signal rank. A different skill from
        /// <see cref="SkillMine"/> (<c>Pers.as:2328-2343</c>).</summary>
        public int SkillSignal;

        /// <summary><c>getLockTip(4)</c> / <c>getLockTip(5)</c> — the repair rank.</summary>
        public int SkillRepair;

        /// <summary><c>getLockMaster(1)</c> — <c>Pers.unlockMaster</c> (<c>Pers.as:2358-2361</c>).</summary>
        public int MasterPick;

        /// <summary><c>getLockMaster(2)</c> — <c>Pers.hackerMaster</c>.</summary>
        public int MasterTerminal;

        public float LockAtt;
        public float PinBreak;
        public float UpChance;

        /// <summary><c>Pers.hackAtt</c> — the terminal attempt budget (<c>Pers.as:299</c>).</summary>
        public int HackAtt;

        /// <summary><c>Pers.security</c> — the level a hacked robot is reprogrammed at
        /// (<c>Interact.as:1585</c>).</summary>
        public int Security;

        /// <summary>Bobby pins carried (<c>invent.items["pin"].kol</c>, <c>:1071</c>).</summary>
        public int Pins;

        /// <summary><c>runa</c> carried — the item that bypasses a pick you cannot make
        /// (<c>:1500</c>).</summary>
        public int Runa;

        /// <summary><c>reboot</c> carried — the same bypass for a terminal (<c>:1504</c>).</summary>
        public int Reboot;

        /// <summary><c>Pers.lockPickTime</c> — the base hold for one attempt, in ticks
        /// (<c>CharacterStats.lockPickTime</c>, default 30).</summary>
        public int LockPickTime;
    }

    /// <summary>
    /// A single line in the security log the F2 tab renders. The port has no <c>gui.infoText</c>
    /// strip (<c>UnitNPCController</c> logs the key instead), so the log <b>is</b> the feedback channel
    /// for this subsystem — and it is what makes "the lock did not open" distinguishable from "the
    /// press never arrived".
    /// </summary>
    public struct SecurityLogEntry
    {
        public int Tick;
        public string ObjectId;
        public string MessageKey;
        public string Detail;
        public SecurityAttemptKind Kind;
        public int Delta;
        public float Chance;
        public float Damage;
    }
}
