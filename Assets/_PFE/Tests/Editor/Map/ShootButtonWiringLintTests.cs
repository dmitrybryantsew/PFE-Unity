using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins the <b>wiring</b> of the shoot-to-press button — the five links between a bullet and a
    /// pressed <c>knop2</c>.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> The data was complete (definition
    /// <c>actdam</c>, placement <c>tipdam</c>), the verb existed (<c>ObjectVerbCommand</c>'s <c>dam</c>
    /// arm), and the rules exist (<see cref="PFE.Systems.Map.Actions.ShootButtonRules"/>) — and shooting
    /// a button still did nothing, because no link joined them: the object was never admitted to a
    /// presenter, the presenter never built a solid surface, and nothing called the press. Every one of
    /// those is a one-line omission that no arithmetic fixture can see.</para>
    ///
    /// <para><b>Why a source lint rather than a behavioural test.</b> The path needs a
    /// <c>GameObject</c>, a <c>BoxCollider2D</c>, a room and a live <c>AreaTriggerSystem</c>; outside the
    /// editor the first of those throws <c>SecurityException: ECall methods must be packaged into a
    /// system module</c> before an assertion is reached. A lint executes everywhere, so it is the only
    /// guard that can actually fail in this harness. <c>SourceLint.ReadStripped</c> removes comments and
    /// string literals first, so a comment that names the call cannot satisfy the check.</para>
    ///
    /// <para><b>Each assertion is one link, and the first is a positive control.</b> The method bodies
    /// are located by signature (which throws if the method is renamed), then asserted on, so a rename
    /// cannot silently drop the method from coverage.</para>
    /// </summary>
    [TestFixture]
    public sealed class ShootButtonWiringLintTests
    {
        private const string PresenterPath = "Systems/Map/Rendering/DoorPropPresenter.cs";
        private const string VisualManagerPath = "Systems/Map/Rendering/RoomObjectVisualManager.cs";
        private const string VerbPath = "Systems/Map/Actions/ObjectVerbCommand.cs";
        private const string TriggerPath = "Systems/Map/Scripting/AreaTriggerSystem.cs";

        /// <summary>
        /// Source with comments removed but <b>string literals kept</b>.
        ///
        /// <para><b>Why not <c>ReadStripped</c> everywhere.</b> That helper removes literals too, which
        /// is right for "is this call still here" but wrong for "does it still name this attribute" —
        /// the attribute name <i>is</i> the thing being asserted, and stripping it makes the check
        /// satisfiable by any <c>GetAttribute</c> call with any key. Comments are still gone, so a
        /// comment naming the attribute cannot pass the assertion either.</para>
        /// </summary>
        private static string ReadCode(string relativePath)
        {
            string fullPath = Path.Combine(
                SourceLint.ProjectRoot(),
                "Assets",
                "_PFE",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(fullPath), "Lint target missing: " + fullPath);

            string code = SourceLint.StripCommentsKeepStrings(File.ReadAllText(fullPath));

            Assert.That(code, Does.Not.Contain("///"),
                "The comment stripper did not strip — an assertion below could then be satisfied by a " +
                "comment that names the attribute it checks for.");

            return code;
        }

        /// <summary>
        /// Link 1 — <b>admission</b>. A bare <c>knop2</c> authors neither an <c>allact</c> nor a
        /// <c>&lt;scr&gt;</c>, so without this clause it falls to <c>ObjectColliderDebugPresenter</c>:
        /// drawn, with no collider and no hit surface. That is the state the challenge rooms shipped in.
        /// </summary>
        [Test]
        public void APlainActDamObjectIsAdmittedToThePresenter()
        {
            string manager = SourceLint.ReadStripped(VisualManagerPath);

            string rule = SourceLint.MethodBody(manager, "public static bool UsesDoorPresenter(");
            Assert.That(rule, Does.Contain("if (hasBulletDamageProxy) return true;"),
                "UsesDoorPresenter no longer admits an `actdam` object. A knop2 in buttons1/buttons4 " +
                "carries no allact and no <scr>, so every other clause answers false and the button is " +
                "placed with no collider — shooting it hits the wall behind it.");

            // The call site must actually supply the fact, or the new parameter defaults to false and
            // the clause is dead code that reads like a safeguard.
            string callSite = SourceLint.MethodBody(manager, "void ApplyPresenterVisual(");
            Assert.That(callSite, Does.Contain("HasBulletDamageProxy(obj)"),
                "ApplyPresenterVisual no longer passes HasBulletDamageProxy(obj) into UsesDoorPresenter " +
                "— the admission clause then always sees its default of false.");

            // And the helper must read the DEFINITION's actdam, not the placement's: knop2 authors it
            // on the AllData row, and a placement-level actdam does nothing in AS3.
            string managerCode = ReadCode(VisualManagerPath);
            string helper = SourceLint.MethodBody(managerCode, "public static bool HasBulletDamageProxy(ObjectInstance obj)");
            Assert.That(helper, Does.Contain("GetAttribute(\"actdam\""),
                "HasBulletDamageProxy no longer reads the definition's actdam attribute.");
        }

        /// <summary>
        /// Link 2 — <b>a surface to hit</b>. The collider used to exist only for a closed door box; a
        /// knop2 is not one, so a round passed straight through. This is the link that makes the proxy
        /// reachable at all.
        /// </summary>
        [Test]
        public void ABulletButtonKeepsASolidCollider()
        {
            string presenter = SourceLint.ReadStripped(PresenterPath);

            string body = SourceLint.MethodBody(presenter, "private void RefreshSolidCollider()");
            Assert.That(body, Does.Contain("HasBulletDamageProxy"),
                "RefreshSolidCollider no longer considers HasBulletDamageProxy. A knop2 is not a door " +
                "box, so the collider is never created and Projectile.OnTriggerEnter2D never fires on " +
                "the button.");

            // The button's surface must survive being pressed: AS3's VirtualUnit stays in loc.units
            // until the box dies, and a second shot is how the button is released.
            Assert.That(body, Does.Contain("!IsDestroyed"),
                "RefreshSolidCollider no longer gates on !IsDestroyed — a wrecked button would keep a " +
                "hit surface AS3 removes (Box.as:864-868).");
        }

        /// <summary>
        /// Link 3 — <b>the press</b>. A weapon's hit on a solid collider reaches
        /// <c>IDestructibleTile.ApplyDestruction</c>; that is the only call the projectile makes, so the
        /// press has to live there and has to run before the door-damage path.
        /// </summary>
        [Test]
        public void AWeaponHitPressesBeforeItDamages()
        {
            string presenter = SourceLint.ReadStripped(PresenterPath);

            string body = SourceLint.MethodBody(presenter, "public void ApplyDestruction(Vector3 worldPosition");

            int press = body.IndexOf("PressByDamage(damageType)", System.StringComparison.Ordinal);
            int damage = body.IndexOf("DamageDoor(destroyAmount", System.StringComparison.Ordinal);

            Assert.That(press, Is.GreaterThanOrEqualTo(0),
                "ApplyDestruction no longer calls PressByDamage. This is the one method Projectile " +
                "invokes on a solid collider, so without it a shot button takes damage instead of " +
                "being pressed — and a knop2 has hp 0.");

            Assert.That(damage, Is.GreaterThanOrEqualTo(0),
                "ApplyDestruction no longer calls DamageDoor — the door-destruction path was removed " +
                "rather than gated.");

            Assert.That(press, Is.LessThan(damage),
                "ApplyDestruction damages before it presses. A button must never reach DamageDoor " +
                "first: AS3's round meets the VirtualUnit, not the box (Location.as:2007-2009).");
        }

        /// <summary>
        /// Link 4 — <b>the shared body</b>. AS3's <c>command("dam")</c> and a player's press both land in
        /// <c>actOsn</c>; a second copy for bullets is the divergence that presents as "the button
        /// behaves differently when shot".
        /// </summary>
        [Test]
        public void TheBulletAndThePlayerRunTheSameActOsn()
        {
            string presenter = SourceLint.ReadStripped(PresenterPath);

            string player = SourceLint.MethodBody(presenter, "public void Interact(GameObject user)");
            Assert.That(player, Does.Contain("RunActOsn(user)"),
                "Interact no longer delegates to RunActOsn — the shared body was split again.");

            string bullet = SourceLint.MethodBody(presenter, "public bool PressByDamage(DamageType damageType)");
            Assert.That(bullet, Does.Contain("RunActOsn(null)"),
                "PressByDamage no longer calls RunActOsn. A bullet press would then have to reimplement " +
                "the toggle, the allact broadcast and the inline script.");

            // The damage-type filter must be consulted before the press, and a mismatch must still
            // return true: the round is consumed either way (Bullet.as:535 tests _loc4_ >= 0).
            Assert.That(bullet, Does.Contain("AcceptsDamageType"),
                "PressByDamage no longer applies the placement's tipdam filter.");

            // actOsn's `if(loc.prob) loc.prob.check()` (Interact.as:1319-1321) — the only thing that
            // advances a prob room for a button with no allact, which is the buttons3 shape.
            string actOsn = SourceLint.MethodBody(presenter, "private void RunActOsn(GameObject user)");
            Assert.That(actOsn, Does.Contain("CheckProbRoomFor"),
                "RunActOsn no longer pokes the prob room. The seven buttons3 buttons carry no allact, " +
                "so this call is the only thing that can tell the room a button was pressed.");
        }

        /// <summary>
        /// Link 5 — <b>the verb</b>. <c>Interact.command("dam")</c>'s three arms, and the two attribute
        /// reads that decide between them.
        /// </summary>
        [Test]
        public void TheDamageVerbHasThreeArmsAndRunsTheInlineScript()
        {
            string verbs = SourceLint.ReadStripped(VerbPath);
            string verbsCode = ReadCode(VerbPath);

            // The definition fallback for `knop` is the fix without which every knop2 takes the wrong
            // arm — the placement authors nothing.
            string knob = SourceLint.MethodBody(verbsCode, "private static bool IsKnob(ObjectInstance obj)");
            Assert.That(knob, Does.Contain("DefinitionAttribute(obj, \"knop\")"),
                "IsKnob no longer falls back to the definition. knop2 authors knop='1' there and no " +
                "shipped placement repeats it, so every bullet-button would read as a non-knob.");

            string mode = SourceLint.MethodBody(verbsCode, "private static int GetInteractionMode(ObjectInstance obj)");
            Assert.That(mode, Does.Contain("DefinitionAttribute(obj, \"inter\")"),
                "GetInteractionMode no longer falls back to the definition's inter.");

            // The third arm must run the inline script — the tutorial button's entire effect.
            Assert.That(verbs, Does.Contain("RunInlineScript"),
                "ObjectVerbCommand no longer invokes RunInlineScript. rooms_begin/room_2_1's knop2 " +
                "carries only <scr act=\"unlock\" targ=\"trMDoor\"/>; without this the door stays locked.");

            // The old name had no binder anywhere, which is how the arm became a no-op.
            Assert.That(verbs, Does.Not.Contain("ActivateObject"),
                "ObjectVerbCommand still references ActivateObject, a slot no production site ever " +
                "bound. Use RunInlineScript and bind it at the CreateFull sites.");
        }

        /// <summary>
        /// Link 6 — <b>the composition sites</b>. Both <c>CreateFull</c> callers must fill the two slots
        /// the button needs, or the verb silently does half its job.
        /// </summary>
        [Test]
        public void BothDispatcherCompositionSitesBindTheButtonCapabilities()
        {
            string presenter = SourceLint.ReadStripped(PresenterPath);
            string trigger = SourceLint.ReadStripped(TriggerPath);

            string scene = SourceLint.MethodBody(presenter, "public static ObjectActionDispatcher CreateSceneDispatcher()");
            Assert.That(scene, Does.Contain("CheckProbRoom ="),
                "The scene dispatcher no longer binds CheckProbRoom. The `dam` verb's one-shot arm is " +
                "`setAct(\"open\",1); loc.prob.check()` (Interact.as:1749-1753) — the buttons4 mechanic — " +
                "so an unbound slot lights the button and never tells the prob room.");
            Assert.That(scene, Does.Contain("RunInlineScript ="),
                "The scene dispatcher no longer binds RunInlineScript.");

            string area = SourceLint.MethodBody(trigger, "private ObjectActionDispatcher ResolveAllActDispatcher()");
            Assert.That(area, Does.Contain("RunInlineScript = OnObjectInteracted"),
                "AreaTriggerSystem no longer binds RunInlineScript to OnObjectInteracted. The runner is " +
                "per-room state this system owns, so it is the only site that can bind it directly.");
        }
    }
}
