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
    /// </summary>
    [TestFixture]
    public class HoofStrikeWiringLintTests
    {
        private const string LoadoutPath = "Systems/Weapons/PlayerWeaponLoadout.cs";
        private const string AnimStatesPath = "Character/Animation/ActionAnimStates.cs";
        private const string AnimEventsPath = "Character/Animation/IAnimationState.cs";
        private const string AudioHandlerPath = "Systems/Audio/CharacterAudioEventHandler.cs";

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
    }
}
