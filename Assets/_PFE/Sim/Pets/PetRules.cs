using System;

namespace PFE.Systems.Pets
{
    /// <summary>
    /// The three companions — AS3 <c>UnitPet</c> is <b>one</b> class constructed three times
    /// (<c>UnitPlayer.as:446-465</c>), so the kind is a parameter, not a subclass.
    /// </summary>
    public enum PetKind
    {
        /// <summary><c>UnitPet("phoenix")</c> — ranged flame, self-resurrecting.</summary>
        Phoenix = 0,

        /// <summary><c>UnitPet("owl")</c> — ranged laser, repaired rather than resurrected.</summary>
        Owl = 1,

        /// <summary><c>UnitPet("moon")</c> — weaponless rammer, summoned only by <c>sp_moon</c>.</summary>
        Moon = 2,
    }

    /// <summary>The unit ids the oracle uses for the three pets — <c>AllData.as:1321, :1338, :1731</c>.</summary>
    public static class PetIds
    {
        public const string Phoenix = "phoenix";
        public const string Owl = "owl";
        public const string Moon = "moon";

        /// <summary>Id → kind. Returns false for anything that is not one of the three.</summary>
        public static bool TryParse(string id, out PetKind kind)
        {
            if (string.Equals(id, Phoenix, StringComparison.Ordinal)) { kind = PetKind.Phoenix; return true; }
            if (string.Equals(id, Owl, StringComparison.Ordinal)) { kind = PetKind.Owl; return true; }
            if (string.Equals(id, Moon, StringComparison.Ordinal)) { kind = PetKind.Moon; return true; }
            kind = PetKind.Phoenix;
            return false;
        }

        public static string ToId(PetKind kind)
        {
            switch (kind)
            {
                case PetKind.Owl: return Owl;
                case PetKind.Moon: return Moon;
                default: return Phoenix;
            }
        }
    }

    /// <summary>
    /// The player's pet stats — AS3 <c>Pers.petHP/petDam/petSkin/petVulner</c>,
    /// <c>owlHP/owlDam/owlSkin/owlVulner</c>, <c>moonHP/moonDam</c> (<c>Pers.as:439-459</c>) plus
    /// <c>Unit.spellPower</c> (<c>Unit.as:316</c>).
    ///
    /// <para><b>Passed by value on purpose.</b> The oracle reads these off the live <c>Pers</c> every
    /// time <c>setLevel</c> runs; a snapshot keeps the rule testable and makes it impossible for the
    /// scaling to read a half-recalculated stat block.</para>
    /// </summary>
    public readonly struct PetStatInput
    {
        public readonly float PetHp;
        public readonly float PetDamage;
        public readonly float PetSkin;
        public readonly float PetVulnerability;
        public readonly float OwlHp;
        public readonly float OwlDamage;
        public readonly float OwlSkin;
        public readonly float OwlVulnerability;
        public readonly float MoonHp;
        public readonly float MoonDamage;
        public readonly float SpellPower;

        public PetStatInput(
            float petHp, float petDamage, float petSkin, float petVulnerability,
            float owlHp, float owlDamage, float owlSkin, float owlVulnerability,
            float moonHp, float moonDamage, float spellPower)
        {
            PetHp = petHp;
            PetDamage = petDamage;
            PetSkin = petSkin;
            PetVulnerability = petVulnerability;
            OwlHp = owlHp;
            OwlDamage = owlDamage;
            OwlSkin = owlSkin;
            OwlVulnerability = owlVulnerability;
            MoonHp = moonHp;
            MoonDamage = moonDamage;
            SpellPower = spellPower;
        }

        /// <summary>The oracle's own defaults — <c>Pers.as:439-459</c>.</summary>
        public static PetStatInput Defaults => new PetStatInput(
            petHp: 50f, petDamage: 2f, petSkin: 0f, petVulnerability: 1f,
            owlHp: 200f, owlDamage: 3f, owlSkin: 5f, owlVulnerability: 1f,
            moonHp: 70f, moonDamage: 15f, spellPower: 1f);
    }

    /// <summary>The three numbers <c>UnitPet.setLevel</c> writes, plus the max HP it derives.</summary>
    public readonly struct PetCombatStats
    {
        public readonly float MaxHp;
        public readonly float Damage;
        public readonly float Skin;
        public readonly float AllVulnerabilityMultiplier;

        public PetCombatStats(float maxHp, float damage, float skin, float allVulnerabilityMultiplier)
        {
            MaxHp = maxHp;
            Damage = damage;
            Skin = skin;
            AllVulnerabilityMultiplier = allVulnerabilityMultiplier;
        }
    }

    /// <summary>A point in the oracle's pixel space. Deliberately not a <c>Vector2</c>.</summary>
    public readonly struct PetPoint
    {
        public readonly float X;
        public readonly float Y;

        public PetPoint(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>Why <c>callPet</c> refused, in the oracle's own order (<c>UnitPlayer.as:3901-3914</c>).</summary>
    public enum PetCallRefusal
    {
        None = 0,
        /// <summary><c>noPet &gt; 0</c> — the dead pet's own cooldown.</summary>
        Cooldown = 1,
        /// <summary><c>noPet2 &gt; 0</c>, <c>!atkPoss</c> or <c>alicorn</c>.</summary>
        Locked = 2,
        /// <summary>The owl is out and dead — it has to be repaired, not re-summoned.</summary>
        OwlDead = 3,
    }

    public enum PetCallAction
    {
        None = 0,
        /// <summary><c>currentPet == id</c> — the same pet was asked for, so it goes away.</summary>
        Recall = 1,
        Summon = 2,
    }

    public readonly struct PetCallDecision
    {
        public readonly PetCallAction Action;
        public readonly PetCallRefusal Refusal;

        public PetCallDecision(PetCallAction action, PetCallRefusal refusal)
        {
            Action = action;
            Refusal = refusal;
        }

        public bool Refused => Refusal != PetCallRefusal.None;
    }

    /// <summary>Everything <c>callPet</c> reads, so the decision needs no game state.</summary>
    public readonly struct PetCallRequest
    {
        public readonly PetKind Kind;
        /// <summary><c>param2</c> — the spell's forced call bypasses every refusal.</summary>
        public readonly bool Forced;
        public readonly int NoPetTicks;
        public readonly int LockTicks;
        public readonly bool CanAttack;
        public readonly bool Alicorn;
        /// <summary>The pet currently out, or null.</summary>
        public readonly PetKind? Current;
        public readonly float OwlHp;
        /// <summary>The location's <c>petOn</c>.</summary>
        public readonly bool PetsAllowed;

        public PetCallRequest(
            PetKind kind, bool forced, int noPetTicks, int lockTicks, bool canAttack, bool alicorn,
            PetKind? current, float owlHp, bool petsAllowed)
        {
            Kind = kind;
            Forced = forced;
            NoPetTicks = noPetTicks;
            LockTicks = lockTicks;
            CanAttack = canAttack;
            Alicorn = alicorn;
            Current = current;
            OwlHp = owlHp;
            PetsAllowed = petsAllowed;
        }
    }

    /// <summary>
    /// The companion rules, transcribed from <c>UnitPet.as</c> and <c>UnitPlayer.as</c> as arithmetic.
    ///
    /// <para><b>Why this file exists.</b> Every number below was invented at least once during the
    /// port of a sibling system and caught only by a fixture pinned to the oracle's literal — the
    /// pet has three places where the obvious reading is wrong (<c>runSpeed</c> is <i>overwritten</i>,
    /// the level term is <c>level - 1</c>, and the ram costs the pet 2 % of its own damage), so the
    /// arithmetic lives here where it can be executed without a scene.</para>
    /// </summary>
    public static class PetRules
    {
        // ── identity ────────────────────────────────────────────────────────────────────────────
        // UnitPlayer.as:446-465 builds exactly three, in this order.
        public static readonly PetKind[] AllKinds = { PetKind.Phoenix, PetKind.Owl, PetKind.Moon };

        // ── UnitPet.as:28-50 — the follower's constants ─────────────────────────────────────────
        /// <summary><c>rasstGG</c> — the leash beyond which it re-picks its fly point (:30).</summary>
        public const float LeashDistance = 200f;
        /// <summary><c>rasstUnmat</c> — inside this it collides with the world again (:32).</summary>
        public const float MaterialDistance = 400f;
        /// <summary><c>rasstVisEn</c> — the target-scan radius (:34).</summary>
        public const float VisionDistance = 800f;
        /// <summary><c>rasstOut</c> — the back-off ring inside the weapon range (:38).</summary>
        public const float BackOffDistance = 100f;

        /// <summary><c>rasstWeap</c> — 200 for the phoenix, 400 for the owl and moon (:90, :95, :104).</summary>
        public static float WeaponRange(PetKind kind) => kind == PetKind.Phoenix ? 200f : 400f;

        // ── UnitPet.as:87-108 — the three flag sets ────────────────────────────────────────────
        /// <summary><c>optAutores</c> — a dead pet revives itself after the cooldown.</summary>
        public static bool AutoResurrects(PetKind kind) => kind == PetKind.Phoenix;

        /// <summary><c>optSit</c> — may perch on a <c>stay</c> prop.</summary>
        public static bool CanPerch(PetKind kind) => kind != PetKind.Moon;

        /// <summary><c>optUncall</c> — dies by being un-summoned instead of lying dead.</summary>
        public static bool UncallsOnDeath(PetKind kind) => kind == PetKind.Moon;

        /// <summary><c>optTurn</c> — faces its fly target; the moon keeps <c>storona = 1</c>.</summary>
        public static bool TurnsToTarget(PetKind kind) => kind != PetKind.Moon;

        /// <summary><c>optEnW</c> — never targets a swimming unit. True for all three (:42).</summary>
        public const bool SkipsSwimmers = true;

        /// <summary><c>knocked = 0</c> for the moon only (<c>:100</c>).</summary>
        public static bool IgnoresKnockback(PetKind kind) => kind == PetKind.Moon;

        /// <summary>
        /// <c>runSpeed = maxSpeed * 2</c> (<c>:74</c>) — <b>this overwrites the XML's own <c>run</c></b>
        /// (<c>AllData.as:1322</c> authors <c>run='15'</c>; the phoenix still runs at 10).
        /// </summary>
        public static float RunSpeed(float walkSpeed) => walkSpeed * 2f;

        /// <summary>
        /// The constructor's one vulnerability write: <c>vulner[D_NECRO] = 0.2</c> for the owl
        /// (<c>:96</c>) and <c>0.5</c> for the moon (<c>:102</c>). The phoenix's table is left alone.
        /// </summary>
        /// <returns>The multiplier to force, or <c>-1</c> for "leave the row's own table".</returns>
        public static float ConstructorNecroticVulnerability(PetKind kind)
        {
            switch (kind)
            {
                case PetKind.Owl: return 0.2f;
                case PetKind.Moon: return 0.5f;
                default: return -1f;
            }
        }

        /// <summary>
        /// <c>if(aiState &lt;= 1) maxSpeed = walkSpeed; else maxSpeed = runSpeed;</c> (<c>:816-823</c>) —
        /// the speed cap the pet flies under, chosen by state. Idle and travelling use the walk speed;
        /// attacking, ramming and recoiling use the doubled run speed.
        /// </summary>
        public static float StateSpeedCap(int aiState, float walkSpeed, float runSpeed)
            => aiState <= 1 ? walkSpeed : runSpeed;

        /// <summary>The death particle each pet emits (<c>UnitPet.as:113-127</c>).</summary>
        public static string DeathParticle(PetKind kind)
        {
            switch (kind)
            {
                case PetKind.Owl: return "orange_spark";
                case PetKind.Moon: return "blue_spark";
                default: return "green_spark";
            }
        }

        /// <summary>25 particles, all three (<c>:117, :121, :125</c>).</summary>
        public const int DeathParticleCount = 25;

        // ── UnitPlayer.as:1495-1497, 3901-3952 — the cooldowns ─────────────────────────────────
        /// <summary><c>noPet2 = 5 * 30</c> — the global summon lock, set even when the call is refused.</summary>
        public const int SummonLockTicks = 150;

        /// <summary><c>noPet = pers.petRes * 30</c> ticks (<c>UnitPet.as:517</c>).</summary>
        public static int DeathCooldownTicks(float petRes) => (int)(petRes * 30f);

        /// <summary>The effective <c>petRes</c>: 30 at every difficulty (see the doc, §2).</summary>
        public const float EffectivePetRes = 30f;

        /// <summary><c>oduplenie = 60</c> on summon, resurrect and reposition (<c>:457, :534, :706</c>).</summary>
        public const int SummonGraceTicks = 60;

        // ── UnitPet.as:199-225 — setLevel ──────────────────────────────────────────────────────
        /// <summary>
        /// <c>UnitPet.setLevel</c>. Note the <b><c>level = pers.level - 1</c></b> on the first line
        /// (<c>:201</c>): a level-1 character gets the raw <c>petHP</c>, not <c>petHP * 1.12</c>.
        /// </summary>
        public static PetCombatStats ScaleStats(PetKind kind, int playerLevel, in PetStatInput stats)
        {
            int level = playerLevel - 1;
            if (level < 0) level = 0;

            switch (kind)
            {
                case PetKind.Owl:
                    return new PetCombatStats(
                        stats.OwlHp * (1f + level * 0.10f),
                        stats.OwlDamage * (1f + level * 0.10f),
                        stats.OwlSkin,
                        stats.OwlVulnerability);

                case PetKind.Moon:
                    // The moon is the only one multiplied by spell power (:221-222).
                    return new PetCombatStats(
                        stats.MoonHp * (1f + level * 0.10f) * stats.SpellPower,
                        stats.MoonDamage * (1f + level * 0.10f) * stats.SpellPower,
                        0f,
                        1f);

                default:
                    return new PetCombatStats(
                        stats.PetHp * (1f + level * 0.12f),
                        stats.PetDamage * (1f + level * 0.10f),
                        stats.PetSkin,
                        stats.PetVulnerability);
            }
        }

        /// <summary><c>hp = (hp / maxhp) * maxhp</c> — the ratio survives a re-scale (<c>:202, :224</c>).</summary>
        public static float RescaleCurrentHp(float currentHp, float oldMaxHp, float newMaxHp)
        {
            if (oldMaxHp <= 0f) return newMaxHp;
            float ratio = currentHp / oldMaxHp;
            if (ratio < 0f) ratio = 0f;
            return ratio * newMaxHp;
        }

        /// <summary><c>hp = Math.min(100, maxhp / 2)</c> — the resurrect floor (<c>:529</c>).</summary>
        public static float ResurrectHp(float maxHp)
        {
            float half = maxHp * 0.5f;
            return half < 100f ? half : 100f;
        }

        // ── UnitPlayer.as:3901-3935 — callPet's gate, in the oracle's order ─────────────────────
        public static PetCallDecision DecideCall(in PetCallRequest request)
        {
            // `if(this.noPet > 0 && !param2)` — :3901
            if (request.NoPetTicks > 0 && !request.Forced)
            {
                return new PetCallDecision(PetCallAction.None, PetCallRefusal.Cooldown);
            }

            // `if(this.noPet2 > 0 && !param2 || !this.atkPoss || World.w.alicorn)` — :3906
            // AS3's `&&` binds tighter than `||`, so the `!param2` term guards ONLY `noPet2`.
            if ((request.LockTicks > 0 && !request.Forced) || !request.CanAttack || request.Alicorn)
            {
                return new PetCallDecision(PetCallAction.None, PetCallRefusal.Locked);
            }

            // `if(param1 == "owl" && this.currentPet == "owl" && pets["owl"].hp <= 0 && !param2)` — :3911
            if (request.Kind == PetKind.Owl
                && request.Current.HasValue && request.Current.Value == PetKind.Owl
                && request.OwlHp <= 0f
                && !request.Forced)
            {
                return new PetCallDecision(PetCallAction.None, PetCallRefusal.OwlDead);
            }

            // `if(this.currentPet == param1) currentPet = ""` — :3925. Toggling OFF happens before the
            // location's own `petOn` test, so a pet can always be dismissed in a no-pet room.
            if (request.Current.HasValue && request.Current.Value == request.Kind)
            {
                return new PetCallDecision(PetCallAction.Recall, PetCallRefusal.None);
            }

            // `else if(!loc.petOn) infoText("noPetCall")` — :3928
            if (!request.PetsAllowed)
            {
                return new PetCallDecision(PetCallAction.None, PetCallRefusal.None);
            }

            return new PetCallDecision(PetCallAction.Summon, PetCallRefusal.None);
        }

        // ── UnitPet.as:162-186 — the two heal paths ────────────────────────────────────────────
        /// <summary>
        /// <c>heal(amount, mode)</c> with <c>mode == 1</c> is the radiation path, and it
        /// <b>only heals a living phoenix</b> (<c>:164-167</c>): the owl and the moon get nothing
        /// from the player's radiation, and a dead phoenix waits for its cooldown.
        /// </summary>
        public static bool RadiationHealAllowed(PetKind kind, int sost)
            => kind == PetKind.Phoenix && sost < 3;

        /// <summary><c>drad / 15</c> — the radiation-to-pet-heal divisor (<c>UnitPlayer.as:944</c>).</summary>
        public const float RadiationHealDivisor = 15f;

        /// <summary><c>heal</c> clamps at max HP and reports whether anything changed.</summary>
        public static float HealedHp(float currentHp, float maxHp, float amount)
        {
            if (amount <= 0f) return currentHp;
            float healed = currentHp + amount;
            return healed > maxHp ? maxHp : healed;
        }

        // ── UnitPet.as:375-411 — findCel ───────────────────────────────────────────────────────
        /// <summary>
        /// <c>!(currentWeapon.damage * target.vulner[tipDamage] &lt; armour + skin + 1 - pier)</c>.
        /// A pet does not shoot what it cannot hurt — the comparison includes the <c>+ 1</c>, which is
        /// why a 0-damage weapon against 0 armour is still allowed through.
        /// </summary>
        public static bool WeaponCanHurt(float weaponDamage, float vulnerability, float armour, float skin, float piercing)
            => !(weaponDamage * vulnerability < armour + skin + 1f - piercing);

        /// <summary>A turret that has not noticed anything is not a target (<c>:389</c>).</summary>
        public static bool TurretIsTargetable(bool isTurret, int aiState) => !isTurret || aiState > 1;

        // ── UnitPet.as:779-804 — the ram ───────────────────────────────────────────────────────
        /// <summary><c>damage(dam * 0.02, D_INSIDE)</c> — the ram costs the pet 2 % of its damage.</summary>
        public static float RamSelfDamage(float damage) => damage * 0.02f;

        /// <summary>A weaponless pet rams instead of shooting (<c>:779</c>).</summary>
        public static bool RamsInsteadOfShooting(bool hasWeapon) => !hasWeapon;

        // ── UnitPet.as:303-368 — getFlyPoint ──────────────────────────────────────────────────
        /// <summary><c>x = player.X + storona * -120</c>, <c>y = player.Y - 80</c>, then the clamps
        /// <b>in the oracle's order</b> (a narrow room ends up negative, exactly as in AS3).</summary>
        public static PetPoint FollowPoint(float playerX, float playerY, int facing, float limitX, float limitY)
        {
            float x = playerX + facing * -120f;
            float y = playerY - 80f;

            if (x < 60f) x = 60f;
            if (x > limitX - 60f) x = limitX - 60f;
            if (y < 80f) y = 80f;
            if (y > limitY - 40f) y = limitY - 40f;

            return new PetPoint(x, y);
        }

        /// <summary>
        /// The perch test (<c>:331</c>): a prop is a seat when it is not a wall, is <c>stay</c>, is
        /// visible, spans the fly point, and its top edge is between 40 px above and 80 px below.
        /// </summary>
        public static bool IsPerchCandidate(bool isWall, bool stays, bool invisible, float x1, float x2, float topY, float flyX, float flyY)
        {
            if (isWall || !stays || invisible) return false;
            if (!(x1 < flyX && x2 > flyX)) return false;
            float dy = flyY - topY;
            return dy < 80f && dy > -40f;
        }

        /// <summary><c>x = floor(x/40)*40+20</c>, <c>y = floor(y/40)*40+39</c> (<c>:344-345</c>).</summary>
        public static PetPoint SnapToTileGrid(float x, float y)
            => new PetPoint((float)Math.Floor(x / 40f) * 40f + 20f, (float)Math.Floor(y / 40f) * 40f + 39f);

        /// <summary>
        /// The four grid probes, in the oracle's order: <c>(x,y)</c>, <c>(x+40·facing, y+40)</c>,
        /// <c>(x, y+40)</c>, <c>(x+40·facing, y+40)</c> (<c>:350-365</c>). Returns the first free one,
        /// or <c>null</c> so the caller falls back to the player's own feet (<c>:366-367</c>).
        /// </summary>
        public static PetPoint? ProbeGridFallback(float x, float y, int facing, Func<float, float, bool> isSolid)
        {
            if (isSolid == null) throw new ArgumentNullException(nameof(isSolid));

            if (!isSolid(x, y)) return new PetPoint(x, y);

            x += 40f * facing;
            y += 40f;
            if (!isSolid(x, y)) return new PetPoint(x, y);

            y += 40f;
            if (!isSolid(x, y)) return new PetPoint(x, y);

            x += 40f * facing;
            if (!isSolid(x, y)) return new PetPoint(x, y);

            return null;
        }

        // ── UnitPet.as:601-658 — the retarget timer ───────────────────────────────────────────
        /// <summary>
        /// <c>aiTCh = floor(random * 20) + 10</c> while the player is moving (<c>|dx| &gt; 2</c>), else
        /// <c>floor(random * 100) + 30</c>. <paramref name="roll"/> is a draw in <c>[0,1)</c>.
        /// </summary>
        public static int NextRetargetTicks(float playerDx, float roll)
        {
            if (roll < 0f) roll = 0f;
            if (roll >= 1f) roll = 0.9999999f;

            return Math.Abs(playerDx) > 2f
                ? (int)Math.Floor(roll * 20f) + 10
                : (int)Math.Floor(roll * 100f) + 30;
        }

        /// <summary><c>aiState &lt;= 1 &amp;&amp; aiTCh % 30 == 1 &amp;&amp; oduplenie &lt;= 0</c> (:745).</summary>
        public static bool ShouldAcquireTarget(int aiState, int aiTch, int oduplenie)
            => aiState <= 1 && aiTch % 30 == 1 && oduplenie <= 0;

        /// <summary><c>(aiState == 2 || aiState == 5) &amp;&amp; aiTCh % 15 == 1</c> (:752).</summary>
        public static bool ShouldRetarget(int aiState, int aiTch)
            => (aiState == 2 || aiState == 5) && aiTch % 15 == 1;

        // ── UnitPet.as:659-740 — the steering thresholds ──────────────────────────────────────
        /// <summary><c>_loc1_ &gt; rasstWeap * 0.9 || !mater</c> — close in (:680).</summary>
        public static bool ShouldCloseIn(float distance, float weaponRange, bool material)
            => distance > weaponRange * 0.9f || !material;

        /// <summary><c>_loc1_ &lt; rasstOut</c> — back off (:686).</summary>
        public static bool ShouldBackOff(float distance) => distance < BackOffDistance;

        /// <summary><c>norma(spd, Math.min(accel * 2, accel * dist / 200))</c> (:684, :735).</summary>
        public static float ApproachSpeed(float acceleration, float distance)
        {
            float scaled = acceleration * distance / 200f;
            float cap = acceleration * 2f;
            return scaled < cap ? scaled : cap;
        }

        /// <summary><c>_loc1_ &lt; 20 &amp;&amp; flyBox &amp;&amp; mater</c> — settle onto the perch (:718).</summary>
        public static bool ShouldSettle(float distance, bool hasPerch, bool material)
            => distance < 20f && hasPerch && material;

        /// <summary><c>_loc1_ &lt; 100 &amp;&amp; mater</c> — coast (:725).</summary>
        public static bool ShouldCoast(float distance, bool material) => distance < 100f && material;

        // The damping factors, named so a transcription slip is visible.
        public const float ApproachDamp = 0.85f;   // :694-695
        public const float CoastDamp = 0.95f;      // :727-728
        public const float RecoilDamp = 0.95f;     // :711-712
        public const float RecoilVelocityDamp = 0.9f; // :713-714
        public const float OverSpeedDamp = 0.8f;   // :143-144
        public const float SwimDamp = 0.9f;        // :148-149
        public const float RamRecoilDamp = 0.5f;   // :793-794

        /// <summary><c>oduplenie &gt; 30 ? 0 : 1 - oduplenie / 30</c> — the summon fade (<c>:245-256</c>).</summary>
        public static float SummonAlpha(int oduplenie)
        {
            if (oduplenie > 30) return 0f;
            if (oduplenie > 0) return 1f - oduplenie / 30f;
            return 1f;
        }

        /// <summary>
        /// <c>weaponX = X + 15 * storona, weaponY = Y - 20</c> for the phoenix; <c>11 / 18</c> for the
        /// other two (<c>:227-241</c>). The moon never holds a weapon but the same branch runs.
        /// </summary>
        public static PetPoint WeaponHoldPoint(PetKind kind, float x, float y, int facing)
        {
            if (kind == PetKind.Phoenix) return new PetPoint(x + 15f * facing, y - 20f);
            return new PetPoint(x + 11f * facing, y - 18f);
        }

        // ── UnitPhoenix.as:103-143 — taming ──────────────────────────────────────────────────
        /// <summary>Five <c>radcookie</c> feeds tame the bird (<c>:122</c>).</summary>
        public const int TameFeedsRequired = 5;

        /// <summary>
        /// One feed: the trigger goes up only when a cookie is actually spent, and the fifth feed is
        /// the one that sets <c>pet_phoenix</c> (<c>:110-128</c>).
        /// </summary>
        public static int TameFeedsAfterFeed(int currentFeeds, bool hasCookie)
            => hasCookie ? currentFeeds + 1 : currentFeeds;

        /// <summary>Whether this feed completes the taming.</summary>
        public static bool TameCompletes(int feedsAfterFeed, bool alreadyTamed)
            => !alreadyTamed && feedsAfterFeed >= TameFeedsRequired;
    }
}
