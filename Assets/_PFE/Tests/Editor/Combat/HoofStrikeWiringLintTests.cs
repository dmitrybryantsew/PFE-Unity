using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>wiring</b> of the three defects a play-test found on 2026-11-01: a kick that could
    /// not open a wooden barricade, a second (sword) sound over every hoof strike, and the moving
    /// platform hum playing on levitation.
    ///
    /// <para><b>Why lints and not behavioural tests.</b> All three live in MonoBehaviours — the
    /// loadout that builds the punch weapon, the animation states the driver ticks, and the audio
    /// handler the event sink calls. Outside the editor each of them needs a <c>GameObject</c> and a
    /// component graph, and throws <c>ECall methods must be packaged into a system module</c> before
    /// reaching an assertion. The arithmetic those wirings feed is pinned behaviourally elsewhere:
    /// <see cref="PFE.Tests.Editor.Map.DoorDestructionMathTests"/> for "30 destroy opens a 30 hp
    /// septum in one hit", and the offline <c>hoofcontroller</c> harness for "the kick carries the
    /// owner's <c>kickDestroy</c> and the punch carries the row's 4". What none of them can see is
    /// <i>whether the source is ever handed over</i>, which is exactly what broke.</para>
    ///
    /// <para><b>Every assertion is paired with a positive control</b> taken from the same extracted
    /// region, so a lint cannot pass because the extractor returned nothing.</para>
    ///
    /// <para><b>A second round of play-test reports (2026-11-04) is pinned here too, sections 4–6.</b>
    /// They are the same theme from the other end: what a hoof strike <i>does</i> when it lands. The
    /// kick still could not break a barricade (the payload reached the hit volume one tick too late),
    /// a door answered a hoof with silence while the wall beside it thudded, and breaking one woke the
    /// whole room. All three are call-order or call-presence questions inside MonoBehaviours, so all
    /// three are lints. The arithmetic they feed is pinned behaviourally:
    /// <see cref="PFE.Tests.Editor.Loot.MapObjectSoundRuleTests"/> for the die-sound gate,
    /// <c>MeleeTileSweepMathTests</c> for the sweep, and the offline <c>hooflint</c> harness for the
    /// owner's <c>kickDestroy</c>.</para>
    /// </summary>
    [TestFixture]
    public class HoofStrikeWiringLintTests
    {
        private const string LoadoutPath = "Systems/Weapons/PlayerWeaponLoadout.cs";
        private const string AnimStatesPath = "Character/Animation/ActionAnimStates.cs";
        private const string AnimEventsPath = "Character/Animation/IAnimationState.cs";
        private const string AudioHandlerPath = "Systems/Audio/CharacterAudioEventHandler.cs";
        private const string UnarmedControllerPath = "Sim/Weapons/Controllers/UnarmedWeaponController.cs";
        private const string HitVolumePath = "Entities/Weapons/MeleeHitVolume.cs";
        private const string DoorPresenterPath = "Systems/Map/Rendering/DoorPropPresenter.cs";
        private const string EnemyBrainPath = "Entities/Enemies/Core/EnemyBrain.cs";
        private const string SecurityPath = "Systems/Security/SecurityRuntime.cs";

        // ── 1. The barricade: the hooves must get the owner's stats ─────────────

        /// <summary>
        /// <c>BuildPunchWeapon</c> must resolve the owner's <c>CharacterStats</c> <b>before</b> it
        /// constructs the controller.
        ///
        /// <para><b>The defect.</b> <c>UnarmedWeaponController</c> takes an <c>IWeaponStatSource</c> by
        /// reference and reads it live, but a source that is null at construction time stays null for
        /// the object's whole life. The hooves are built in <c>Awake</c>, before any equip and before
        /// <c>Construct</c> runs, so every owner term fell back to its "no owner" value:
        /// <c>KickDestroy</c> 0, which left the kick carrying the row's <c>destroy='4'</c>
        /// (<c>AllData.as:25</c>) instead of <c>Pers.kickDestroy</c> 30 (<c>Pers.as:335</c>). The
        /// septum barricade in <c>rooms_begin/room_1_0</c> is <c>hp='30' thre=0</c>
        /// (<c>AllData.as:4858</c>), so one kick should open it and instead took eight.</para>
        /// </summary>
        [Test]
        public void BuildPunchWeapon_ResolvesTheOwnerStats_BeforeConstructingTheController()
        {
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(LoadoutPath), "private void BuildPunchWeapon()");

            Assert.That(body, Does.Contain("CreateHoofStrike"),
                "positive control: this has to be BuildPunchWeapon's real body");

            int resolved = body.IndexOf("ResolveOwnerStatSource", System.StringComparison.Ordinal);
            int created = body.IndexOf("CreateHoofStrike", System.StringComparison.Ordinal);

            Assert.That(resolved, Is.GreaterThanOrEqualTo(0),
                "the punch controller must be handed the owner's CharacterStats — without it " +
                "kickDestroy is 0 and the kick keeps the row's destroy='4'");
            Assert.That(resolved, Is.LessThan(created),
                "the source has to be resolved BEFORE the controller is constructed: the constructor " +
                "captures the reference, so a later resolve does not reach an existing controller");
        }

        /// <summary>
        /// <c>TickPunch</c> must re-resolve when the source is still missing, because
        /// <c>PlayerController.Awake</c> is what adds <c>CharacterStats</c> and component <c>Awake</c>
        /// order is not guaranteed. Without this the unarmed player — the one who never calls
        /// <see cref="Equip"/> at all — keeps a null source for the entire run.
        /// </summary>
        [Test]
        public void TickPunch_ReResolvesALateArrivingStatSource()
        {
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(LoadoutPath), "private void TickPunch()");

            Assert.That(body, Does.Contain("_punchController"),
                "positive control: this has to be TickPunch's real body");
            Assert.That(body, Does.Contain("ResolveOwnerStatSource"),
                "a source added after Awake must still reach the hooves");
            Assert.That(body, Does.Contain("BuildPunchWeapon"),
                "…and the controller has to be rebuilt for it to take effect");
        }

        /// <summary>
        /// <c>Equip</c> is the other place a source can appear. It must rebuild the hooves too, or
        /// equipping a rifle would give the gun the stats and leave the kick without them.
        /// </summary>
        [Test]
        public void Equip_PropagatesANewStatSourceToTheHooves()
        {
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(LoadoutPath),
                "public void Equip(WeaponDefinition def)");

            Assert.That(body, Does.Contain("_current = _factory.Create"),
                "positive control: this has to be Equip's real body");
            Assert.That(body, Does.Contain("ResolveOwnerStatSource"),
                "Equip must resolve the owner stats (AS3 Weapon.setPers runs at equip time)");
            Assert.That(body, Does.Contain("BuildPunchWeapon"),
                "and rebuild the hooves, which were built in Awake with whatever source existed then");
        }

        // ── 2. The second sound: the hooves play one sample, not two ────────────

        /// <summary>
        /// The punch and kick animation states must not fire <c>MeleeImpact</c>.
        ///
        /// <para><b>The defect.</b> Both fired it on a guessed frame (8 and 6), and the audio handler
        /// mapped it to <c>blade</c>. <c>WKick</c> plays exactly one sample — <c>Snd.ps("m_big")</c>
        /// for the kick, <c>Snd.ps("m_med")</c> for the punch (<c>WKick.as:81</c>, <c>:105</c>) — at the
        /// frame the swing fires (<c>rapid - 8</c>), and the controller already plays it. So every
        /// strike rang with the right sample and a sword impact over it. The landing sound a swing
        /// makes is a separate, legitimate layer: the swing is a <c>Bullet</c> in the oracle, so
        /// <c>Bullet.sound()</c> adds the material sample when it connects — that is
        /// <c>MeleeHitVolume.PlayImpactSound</c>, not this event.</para>
        /// </summary>
        [Test]
        public void TheHoofAnimStates_FireNoMeleeImpactEvent()
        {
            string source = SourceLint.ReadStripped(AnimStatesPath);

            // Positive control: other action states still fire their own events.
            Assert.That(source, Does.Contain("FireEvent(AnimEvent.Death"),
                "positive control: the file must still be firing animation events at all");
            Assert.That(source, Does.Not.Contain("MeleeImpact"),
                "the strike sample is the controller's (m_big / m_med); a second, guessed impact " +
                "frame adds the `blade` sample on top of it");
        }

        /// <summary>
        /// …and the event id itself is gone, so it cannot be re-wired by habit.
        /// </summary>
        [Test]
        public void TheMeleeImpactEventId_IsNotDeclared()
        {
            string source = SourceLint.ReadStripped(AnimEventsPath);

            Assert.That(source, Does.Contain("DoubleJump"),
                "positive control: this has to be the AnimEvent constant table");
            Assert.That(source, Does.Not.Contain("MeleeImpact"),
                "a declared-but-unfired id invites exactly the re-wiring this fixture forbids");
        }

        // ── 3. Levitation: not the moving-platform hum ─────────────────────────

        /// <summary>
        /// <c>Jump</c> and <c>DoubleJump</c> must not be mapped to a sound id — they fall through to
        /// the null default.
        ///
        /// <para><b>The defect.</b> Both mapped to <c>move</c>, which is the <i>moving platform</i>
        /// hum, not a whoosh: <c>Interact.as:1869</c> plays it from <c>sound("move")</c> and
        /// <c>:1931</c>/<c>:1939</c> start the same sample at offset 0 and 5500 ms, so it is over five
        /// seconds long. The levitation perk <i>is</i> the double jump (<c>Pers.isDJ</c>, granted by
        /// <c>beginLevit</c>), which is why the platform hum was heard while levitating.</para>
        ///
        /// <para><b>Why silence rather than another id.</b> <c>UnitPlayer</c>'s complete set of
        /// scripted samples is dash / electro / al_armor / bale_e (<c>UnitPlayer.as:1883</c>,
        /// <c>:3364</c>, <c>:3986</c>, <c>:4198</c>) — no jump and no levitation cue — and the sound
        /// library has no id for one. Any other choice would be invented.</para>
        /// </summary>
        [Test]
        public void MapEventToSound_LeavesJumpAndDoubleJumpSilent()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(AudioHandlerPath),
                "private static string MapEventToSound(string eventId)");

            Assert.That(body, Does.Contain("AnimEvent.Land"),
                "positive control: this has to be the event → sound switch");
            Assert.That(body, Does.Not.Contain("AnimEvent.Jump"),
                "jump / double-jump must fall through to null; mapping them to `move` plays the " +
                "five-second moving-platform hum on every levitation");
            Assert.That(body, Does.Not.Contain("AnimEvent.DoubleJump"),
                "the levitation jump is the one the player reported");
        }

        // ── 4. The kick that could not break: the payload must precede the window ──

        /// <summary>
        /// <c>Shoot</c> must stamp the strike's <c>DamageContext</c> on the hit volume <b>before</b>
        /// it opens the window.
        ///
        /// <para><b>The defect.</b> The controller publishes its plan with <c>_plans.Add</c> and then
        /// sweeps the first line in the <i>same</i> flash frame, while
        /// <c>PlayerWeaponLoadout.ConsumePendingPlans</c> only forwards a plan's payload to the volume
        /// after <c>TickPunch</c> returns. So the first line of every strike swept with the
        /// <i>previous</i> strike's payload, and the first strike of a run swept with none at all —
        /// <c>SweepTilesForImpact</c> returns early on <c>!_hasDamageContext</c>. For the back kick
        /// that first line is the low one (30 px, <c>WKick.as:59-65</c>), the only line that can meet
        /// a floor-standing wall, and the one-tile-per-swing latch is consumed there. A kick after a
        /// punch therefore spent its one tile hit on the punch's <c>destroy=4</c> instead of
        /// <c>Pers.kickDestroy</c> 30, and the opening kick of a run did nothing.</para>
        ///
        /// <para><b>Why this is a lint.</b> <c>MeleeOwnerStatTests</c> can now count the bind order,
        /// but it has never executed offline: its <c>MakeUnarmed</c> allocates a
        /// <c>WeaponDefinition</c> ScriptableObject and every one of its tests dies at fixture setup
        /// with <c>ECall methods must be packaged into a system module</c> (measured 2026-11-04:
        /// 0 passed / 18 failed). The assertion it carries is the right one and stays; this lint is
        /// the half that can actually go red in the harness.</para>
        /// </summary>
        [Test]
        public void Shoot_StampsTheDamageContext_BeforeItOpensTheStrikeWindow()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(UnarmedControllerPath), "private void Shoot()");

            Assert.That(body, Does.Contain("_plans.Add"),
                "positive control: this has to be UnarmedWeaponController.Shoot's real body");

            int stamped = body.IndexOf("SetDamageContext", System.StringComparison.Ordinal);
            int armed = body.IndexOf("SetActive(true)", System.StringComparison.Ordinal);

            Assert.That(armed, Is.GreaterThanOrEqualTo(0),
                "positive control: Shoot still has to open the strike window at all");
            Assert.That(stamped, Is.GreaterThanOrEqualTo(0),
                "the controller must stamp its own payload: it binds its first line in the same tick " +
                "it publishes, and the loadout's forward only runs at the end of that tick");
            Assert.That(stamped, Is.LessThan(armed),
                "the payload has to be on the volume BEFORE the window opens — otherwise the first " +
                "line of the swing sweeps with the previous strike's context (or none), and for the " +
                "kick that first line is the low one, the only one that can reach a wall");
        }

        /// <summary>
        /// The loadout must look for the hit volume <b>inside inactive objects</b>.
        ///
        /// <para><b>The defect.</b> <c>MeleeHitVolume.Awake</c> calls <c>SetActive(false)</c>, and
        /// <c>GetComponentInChildren&lt;T&gt;()</c> skips inactive objects by default. The lookup was
        /// therefore an <c>Awake</c>-order coin flip: when the volume's <c>Awake</c> ran first, the
        /// field stayed null for the whole run — no melee damage, no tile damage, no impact audio, and
        /// no <c>Construct</c> injection into the volume.</para>
        /// </summary>
        [Test]
        public void ResolveMeleeHitVolume_LooksInsideInactiveObjects()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(LoadoutPath), "private MeleeHitVolume ResolveMeleeHitVolume()");

            Assert.That(body, Does.Contain("GetComponentInChildren<MeleeHitVolume>"),
                "positive control: this has to be the resolver's real body");
            Assert.That(body, Does.Contain("includeInactive: true"),
                "the volume disables its own GameObject in Awake, so a default lookup misses it");

            string whole = SourceLint.ReadStripped(LoadoutPath);

            // Positive control on the absence check below: the bare form is used for the *other*
            // children in this same file, so a green "not found" cannot mean the pattern is inert.
            Assert.That(whole, Does.Contain("GetComponentInChildren<WeaponPresenter>()"),
                "positive control: the bare form still appears in this file for other components");
            Assert.That(whole, Does.Not.Contain("GetComponentInChildren<MeleeHitVolume>()"),
                "a bare lookup here is the Awake-order coin flip that leaves _meleeHitVolume null " +
                "for the whole run");
        }

        /// <summary>
        /// The premise the resolver above works around: the volume really does disable itself.
        /// </summary>
        [Test]
        public void TheHitVolume_DisablesItsOwnGameObjectInAwake()
        {
            string source = SourceLint.ReadStripped(HitVolumePath);

            Assert.That(source, Does.Contain("public void SetActive(bool active)"),
                "positive control: this has to be MeleeHitVolume");

            string body = SourceLint.MethodBody(source, "private void Awake()");

            Assert.That(body, Does.Contain("SetActive(false)"),
                "if this ever stops being true, the includeInactive:true in the loadout is harmless " +
                "but the comment explaining it is a lie — update both together");
        }

        // ── 5. The silent door: it has to declare what it is made of ─────────────

        /// <summary>
        /// <c>DoorPropPresenter.Initialize</c> must stamp a surface material, and must do it after the
        /// object instance is in hand.
        ///
        /// <para><b>The defect.</b> In AS3 a door is not a separate hit target: its cells are
        /// <c>phis == 1</c> tiles stamped by <c>Box.initDoor</c>, so a blade reaches
        /// <c>Location.hitTile</c> → <c>Bullet.sound(tile.mat)</c> and gets the <i>door's own</i>
        /// material — <c>door1</c> is <c>mat='3'</c> → <c>hit_wood</c>, <c>door1b</c> is
        /// <c>mat='1'</c> → <c>hit_metal</c>. The port has no collider on those cells;
        /// <c>_solidCollider</c> stands in, and a bare <c>BoxCollider2D</c> answers "no material",
        /// which <c>ImpactSoundResolver</c> reads as "silent". Hitting a closed door made no sound at
        /// all while the wall beside it thudded.</para>
        /// </summary>
        [Test]
        public void DoorInitialize_StampsTheSurfaceMaterial_AfterTheInstanceIsAssigned()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(DoorPresenterPath), "public void Initialize(");

            Assert.That(body, Does.Contain("RefreshSolidCollider"),
                "positive control: this has to be Initialize's real body");

            int assigned = body.IndexOf("_objectInstance = obj", System.StringComparison.Ordinal);
            int stamped = body.IndexOf("StampSurfaceMaterial", System.StringComparison.Ordinal);

            Assert.That(assigned, Is.GreaterThanOrEqualTo(0),
                "positive control: Initialize still stores the object instance");
            Assert.That(stamped, Is.GreaterThanOrEqualTo(0),
                "without a TileSurface the door resolves to SurfaceMaterial.Default and every hit on " +
                "it is silent while the adjacent wall thuds");
            Assert.That(stamped, Is.GreaterThan(assigned),
                "StampSurfaceMaterial early-returns on a null _objectInstance, so calling it before " +
                "the assignment is silent — the same defect in a new place");
        }

        /// <summary>
        /// …and the material comes from the AS3 <c>mat</c> index through the shared table, not from a
        /// second, hand-rolled mapping.
        /// </summary>
        [Test]
        public void StampSurfaceMaterial_UsesTheSharedAs3MaterialTable()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(DoorPresenterPath), "private void StampSurfaceMaterial()");

            Assert.That(body, Does.Contain("TileSurface"),
                "positive control: this has to be the stamping method");
            Assert.That(body, Does.Contain("TileSurface.FromAs3Material"),
                "MapObjectDefinition.materialId is the raw <obj mat='…'> value, and the raw numbering " +
                "disagrees with MaterialType from 2 upward — TileMaterialRules owns that table and " +
                "FromAs3Material is its only wrapper");
            Assert.That(body, Does.Contain("definition.materialId"),
                "the source of the index has to be the authored attribute");
        }

        // ── 6. The loud barricade: one sample and one 800 px shout, not the room alarm ──

        /// <summary>
        /// <c>Die</c> must play the authored <c>die='…'</c> sample under the oracle's damage-type gate.
        ///
        /// <para><b>The defect.</b> <c>MapObjectSoundRule.DieSound</c> had read that attribute since it
        /// was written and had <b>no caller</b> — every shipped door authors
        /// <c>die='break_wood'</c> / <c>die='break_metal'</c>, and a barricade the player spent three
        /// kicks on vanished in silence. AS3 plays it in <c>Box.die()</c> under
        /// <c>if(param1 &gt;= 0 &amp;&amp; param1 &lt; 10)</c> (<c>Box.as:822</c>) and then
        /// <c>if(this.sndDie != "")</c> (<c>:850-853</c>), where <c>param1</c> is the killing hit's
        /// damage type; that conjunction lives in
        /// <see cref="PFE.Tests.Editor.Loot.MapObjectSoundRuleTests"/> so it can be pinned without an
        /// engine.</para>
        /// </summary>
        [Test]
        public void DoorDie_PlaysTheAuthoredSample_UnderTheOracleGate()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(DoorPresenterPath), "private void Die(DamageType damageType)");

            Assert.That(body, Does.Contain("isDestroyed = true"),
                "positive control: this has to be Die's real body");

            Assert.That(body, Does.Contain("MapObjectSoundRule.DieSound"),
                "the die attribute is authored on every shipped door and had no caller");
            Assert.That(body, Does.Contain("PlaysOnDie"),
                "the oracle only plays it for damage types 0..9 (Box.as:848)");
            Assert.That(body, Does.Contain("SoundBus.Current?.Play"),
                "reading the id is not playing it");
        }

        /// <summary>
        /// …and it must make exactly the one shout the oracle makes, at 800 px, not the alarm's 1500.
        ///
        /// <para><b>The defect.</b> <c>Box.die()</c> emits a single
        /// <c>loc.budilo(X, Y - scY / 2, noiseDie)</c> with <c>noiseDie = 800</c>
        /// (<c>Box.as:96</c>, <c>:855-858</c>). Nothing else in the oracle alerts on a tile or a box
        /// dying — <c>makeNoise</c> is called from <c>Interact</c>, <c>Unit</c> and <c>Weapon</c> only.
        /// Two details of that call are load-bearing: it sits <b>outside</b> the
        /// <c>if(param1 &gt;= 0 &amp;&amp; param1 &lt; 10)</c> band (<c>:822</c>), so a prop killed by an
        /// exotic damage type is silent but still alerts; and the whole door branch it lives in is
        /// gated on <c>if(this.door &gt; 0)</c> (<c>:802</c>), so it is a <i>door</i> shout.
        ///
        /// <para>The port had no such call and instead raised a full <i>room alarm</i> on any first
        /// sighting in a room with an armed panel, which pulses <c>budilo(…, 1500)</c> every 30 ticks
        /// for 240 ticks (<c>Interact.as:638-647</c>): kicking a hole in a barricade let one guard see
        /// through it and woke everything within 15 units.</para>
        /// </summary>
        [Test]
        public void DoorDie_MakesOneRoomBoundedShout_NotTheAlarmPulse()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(DoorPresenterPath), "private void Die(DamageType damageType)");

            Assert.That(body, Does.Contain("isDestroyed = true"),
                "positive control: this has to be Die's real body");
            Assert.That(body, Does.Contain("SecurityRuntime.NotifyBoxDestroyed"),
                "Box.as:855-858 is the one shout a broken box makes");

            string security = SourceLint.ReadStripped(SecurityPath);
            Assert.That(security, Does.Contain("RoomAlarm.BudiloRadius"),
                "positive control: this has to be the alerting code");

            // Not MethodBody: NotifyBoxDestroyed is expression-bodied, so a brace match would run on
            // into the next method. Read the declaration through its semicolon instead.
            int decl = security.IndexOf("NotifyBoxDestroyed(", System.StringComparison.Ordinal);
            Assert.That(decl, Is.GreaterThanOrEqualTo(0),
                "positive control: the shout has to still be declared here");

            int end = security.IndexOf(';', decl);
            Assert.That(end, Is.GreaterThan(decl), "no semicolon after the declaration");
            Assert.That(security.Substring(decl, end - decl), Does.Contain("BoxDeathNoiseRadius"),
                "the radius has to be the box's own constant, not the alarm's");

            Assert.That(security, Does.Contain("BoxDeathNoiseRadius = 800f"),
                "Box.as:96 authors noiseDie = 800; the alarm pulse is 1500 (RoomAlarm.BudiloRadius), " +
                "so reusing it here wakes everything within 15 units instead of 8");
        }

        /// <summary>
        /// The brain may raise the alarm only for a unit that authored a <c>scrAlarm</c>.
        ///
        /// <para><b>The oracle's condition</b> is <c>scrAlarm != null</c>
        /// (<c>UnitRaider.as:800-806</c>), built in <c>Unit.as:1566-1586</c> from
        /// <c>mapxml.@alarm</c> or a <c>&lt;scr eve="alarm"&gt;</c> child. Only <b>three</b> shipped
        /// placements author one — <c>&lt;obj id="ranger" alarm="rangerStory3"&gt;</c> in
        /// <c>RoomsMbase</c>, and a <c>&lt;scr eve="alarm"&gt;</c> on the raider in <c>Rooms</c>'
        /// <c>room_3_1</c> and the slaver in <c>RoomsSerial</c>' <c>room_6_0</c>. The port's
        /// substitute, "any first sighting in a room with an armed panel", is not a substitution but a
        /// different game.</para>
        /// </summary>
        [Test]
        public void UpdateSenses_RaisesTheAlarmOnlyForAUnitThatAuthoredOne()
        {
            string source = SourceLint.ReadStripped(EnemyBrainPath);
            string body = SourceLint.MethodBody(source, "protected virtual void UpdateSenses(int tickIndex)");

            Assert.That(body, Does.Contain("_sensors.Evaluate"),
                "positive control: this has to be UpdateSenses' real body");

            int call = body.IndexOf("NotifySighting", System.StringComparison.Ordinal);
            Assert.That(call, Is.GreaterThanOrEqualTo(0),
                "positive control: the call has to still be there — this lint is about its guard");

            // The guard is the `if (` that immediately precedes the call. Reformatting the condition is
            // fine; dropping the flag from it is not.
            int guard = body.LastIndexOf("if (", call, System.StringComparison.Ordinal);
            Assert.That(guard, Is.GreaterThanOrEqualTo(0),
                "the alarm call must sit inside a conditional");

            string condition = body.Substring(guard, call - guard);
            Assert.That(condition, Does.Contain("_hasAuthoredAlarmScript"),
                "without this the room alarm is raised by the first sighting of any hostile in a room " +
                "with an armed panel, which pulses budilo(…, 1500) eight times — that is the " +
                "'breaking a barricade alerted the whole level' report");

            Assert.That(source, Does.Contain("_hasAuthoredAlarmScript = PlacementAuthorsAlarmScript"),
                "and the flag has to be resolved from the placement, not guessed");
        }
    }
}
