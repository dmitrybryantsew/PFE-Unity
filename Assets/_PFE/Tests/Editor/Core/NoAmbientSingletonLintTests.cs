using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Wave 2 thread E's closing guard: the <b>ambient singleton freeze</b>. Thread E migrated the
    /// report's eight hidden globals to injected instances one at a time (SoundBus, the view/elite
    /// registries, RuntimeEntityIdContext, CampaignManager, ScriptSystem); SaveManager and
    /// RoomTransitionManager were evaluated and deliberately KEPT as documented scene-singleton
    /// seams (see their class docs). This fixture freezes what remains: every
    /// <c>public static … Current/Instance/Active/Shared</c> property declaration in production
    /// source must appear on the explicit allowlist below, with a reason. A NEW ambient singleton
    /// fails the test until a reviewer adds an entry — which is the moment its dependency-hiding
    /// cost is weighed deliberately instead of by default.
    ///
    /// <para><b>Why a source lint.</b> The same ECall wall every wiring lint documents: these
    /// declarations live on MonoBehaviours and scene services that cannot be constructed offline,
    /// so no reflection-based fixture can execute here. Reading stripped source is the one form of
    /// guard that runs everywhere — and stripping matters doubly for THIS lint, because the XML
    /// docs on every allowlisted class name the static being allowed; a raw-text scan would pass on
    /// a declaration whose doc survived it.</para>
    ///
    /// <para><b>Why declarations and not usages.</b> The defect is the hidden mutable global, which
    /// exists at the declaration. Usages of an allowlisted seam (e.g. <c>DoorPropPresenter</c>
    /// reading <c>CampaignManager.Current</c>) are the seam's documented consumers — pinning each
    /// usage would forbid the very paths the allowlist exists to permit.</para>
    ///
    /// <para><b>Grammar.</b> A line declares an ambient singleton when it is accessibility +
    /// <c>static</c> + a return type + one of the four accessor names + a property body opener
    /// (<c>{</c>, <c>=></c>, or end-of-line for braced declarations on the next line). Static
    /// CLASSES (<c>GlobalDifficulty</c>, <c>ContentInternContext</c>), static FIELDS
    /// (<c>CharacterStats.CombatRng</c>) and static EVENTS are out of scope: the lint targets the
    /// "one live instance, reached from anywhere" shape specifically.</para>
    /// </summary>
    [TestFixture]
    public sealed class NoAmbientSingletonLintTests
    {
        // The four accessor names that make a static property "ambient singleton" shaped. Current /
        // Instance are the classic pair; Active is RuntimeEntityIdContext's post-thread-E facade
        // accessor (same shape, renamed); Shared catches the lazy resource-cache family.
        private static readonly string[] AccessorNames = { "Current", "Instance", "Active", "Shared" };

        // A captured declaration: where it lives and which accessor it exposes.
        private sealed class AmbientDeclaration
        {
            public string Path;   // repo-relative with forward slashes, e.g. Assets/_PFE/Core/SimLoop.cs
            public int Line;      // 1-based, in the STRIPPED source — a reviewer reads the real file
            public string Name;   // Current / Instance / Active / Shared
            public string Text;   // the stripped declaration line, for the failure message
        }

        // The allowlist. One entry per (file, accessor name); the Reason is asserted non-empty and
        // printed on failure so the review cost is always visible. Categories:
        //   [thread-E seam]     — evaluated and kept THIS thread, with the reason in the class's doc
        //   [push-at-build]     — set by GameLifetimeScope's build callback at container lifetime
        //   [scene singleton]   — scene-placed / self-creating MonoBehaviour the container cannot own
        //   [debug tooling]     — editor/overlay diagnostics, never gameplay-authoritative
        //   [null object]       — immutable do-nothing implementations of a DI seam
        //   [resource cache]    — lazy-loaded SHARED CONTENT, stateless or immutable after load
        // A future migration REMOVES its entry; the test then proves the declaration is gone.
        private static readonly (string Path, string Name, string Reason)[] Allowlist =
        {
            // ── Thread E's four kept seams ─────────────────────────────────────────────
            ("Assets/_PFE/Systems/Campaign/CampaignManager.cs", "Current",
             "[thread-E seam] AddComponent-built consumers (DoorPropPresenter, NPC units, the " +
             "FishNet session that already locates MapBridge via FindFirstObjectByType); container " +
             "tracks it in Initialize/Dispose"),
            ("Assets/_PFE/Systems/Map/Scripting/ScriptSystem.cs", "Current",
             "[thread-E seam] AddComponent NPC greeting path; container-resolved consumers were " +
             "handed the instance through RoomVisualController's pushed resolver instead"),
            ("Assets/_PFE/Core/RuntimeEntityIdContext.cs", "Active",
             "[thread-E seam] the delegating facade over the injected RuntimeEntityIdService " +
             "singleton; Bound at build so static and injected callers share one allocator"),
            ("Assets/_PFE/Systems/Map/Serialization/SaveManager.cs", "Instance",
             "[thread-E seam] menu scene has no container and births the instance that the game " +
             "scene must adopt, not duplicate; dev console relies on self-creation"),
            ("Assets/_PFE/Systems/Map/Streaming/RoomTransitionManager.cs", "Instance",
             "[thread-E seam] scene-placed with inspector wiring; consumers are physics/AddComponent/" +
             "per-unit and the boot-order lint pins the lazy find"),

            // ── Push-at-build ambient contexts (pre-date thread E, deliberate) ──────────
            ("Assets/_PFE/Core/SinglePlayerSlotContext.cs", "Current",
             "[push-at-build] GameLifetimeScope pushes the container's IPlayerSlotContext; the " +
             "AddComponent-built unit/security paths read it back"),
            ("Assets/_PFE/Core/SimAuthorityContext.cs", "Current",
             "[push-at-build] scoped write-context for HostWriteGuard, entered per tick by SimLoop; " +
             "the ambient default is the peer role"),
            ("Assets/_PFE/Systems/Map/Actions/ObjectInteractionBroadcast.cs", "Current",
             "[scene singleton] pre-existing interaction event bus, frozen pending its own " +
             "migration review"),
            ("Assets/_PFE/Systems/Security/SecurityRuntime.cs", "Instance",
             "[scene singleton] pre-existing security-scene handle the AddComponent-built cameras " +
             "read; frozen pending its own migration review"),

            // ── Debug / editor tooling (never gameplay-authoritative) ──────────────────
            ("Assets/_PFE/Core/FpsCounterOverlay.cs", "Instance",
             "[debug tooling] diagnostics overlay"),
            ("Assets/_PFE/Core/Scripting/DeveloperConsoleController.cs", "Instance",
             "[debug tooling] the developer console's own scene handle"),
            ("Assets/_PFE/Core/Scripting/DeveloperConsoleService.cs", "Instance",
             "[debug tooling] console command service, scene-created"),
            ("Assets/_PFE/Core/Scripting/PlayerDebugEditorOverlay.cs", "Instance",
             "[debug tooling] F-overlay/editor diagnostics"),
            ("Assets/_PFE/Systems/Map/Rendering/ColliderDebugOverlay.cs", "Instance",
             "[debug tooling] diagnostics overlay"),
            ("Assets/_PFE/Systems/Map/Rendering/EnemyAIDebugOverlay.cs", "Instance",
             "[debug tooling] diagnostics overlay"),
            ("Assets/_PFE/Systems/Map/Rendering/UnitHealthOverlay.cs", "Instance",
             "[debug tooling] diagnostics overlay"),
            ("Assets/_PFE/Systems/Combat/FloatingDamageOverlay.cs", "Instance",
             "[debug tooling] diagnostics overlay"),
            ("Assets/_PFE/Systems/Units/UnitBarkOverlay.cs", "Instance",
             "[debug tooling] diagnostics overlay"),
            ("Assets/_PFE/Systems/Campaign/CampaignNotes.cs", "Current",
             "[debug tooling] PipBuck notes overlay's scene handle"),
            ("Assets/_PFE/Editor/Art/OnDemand/OnDemandArtProvider.cs", "Shared",
             "[debug tooling] editor-only on-demand art cache"),

            // ── Null objects (immutable do-nothing DI seam implementations) ─────────────
            ("Assets/_PFE/Systems/Particles/IParticleSpriteFrames.cs", "Instance",
             "[null object] immutable NullParticleSpriteFrames seam default"),
            ("Assets/_PFE/Systems/Particles/IParticleTileWater.cs", "Instance",
             "[null object] immutable DryParticleTileWater seam default"),

            // ── Shared content caches (stateless/immutable after lazy load) ─────────────
            ("Assets/_PFE/Data/Definitions/IObjectDefinitionProvider.cs", "Shared",
             "[resource cache] lazy Resources object-table provider"),
            ("Assets/_PFE/Data/Definitions/UnitNameTable.cs", "Shared",
             "[resource cache] lazy name table"),
            ("Assets/_PFE/Data/Definitions/Campaign/INpcDefinitionProvider.cs", "Shared",
             "[resource cache] lazy Resources NPC-table provider"),
            ("Assets/_PFE/Entities/Units/IUnitDefinitionProvider.cs", "Shared",
             "[resource cache] lazy Resources unit-table provider"),
            ("Assets/_PFE/Systems/Weapons/IWeaponDefinitionProvider.cs", "Shared",
             "[resource cache] lazy Resources weapon-table provider"),
            ("Assets/_PFE/UI/Common/PfeTextTableProvider.cs", "Shared",
             "[resource cache] lazy text table"),
            ("Assets/_PFE/Systems/Units/ReplicCatalog.cs", "Shared",
             "[resource cache] lazy replication catalogue"),
            ("Assets/_PFE/UI/HUD/EliteNameplateView.cs", "Shared",
             "[resource cache] shared sprite asset"),
        };

        [Test]
        public void EveryAmbientSingletonDeclaration_IsAllowlistedWithAReason()
        {
            List<AmbientDeclaration> declarations = ScanProductionTree();
            var violations = new List<string>();

            foreach (AmbientDeclaration decl in declarations)
            {
                string reason = ReasonFor(decl.Path, decl.Name);
                if (reason == null)
                {
                    violations.Add($"{decl.Path}:{decl.Line}: unexpected ambient singleton " +
                                   $"'{decl.Text}' — add it to the allowlist with a reason, or " +
                                   "inject the instance instead (thread E's shape).");
                    continue;
                }

                if (reason.Length == 0)
                {
                    violations.Add($"{decl.Path}:{decl.Line}: allowlist entry for '{decl.Name}' " +
                                   "carries no reason.");
                }
            }

            Assert.IsEmpty(violations,
                "New ambient singleton declarations appeared (or the scanner broke). Each one hides " +
                "a dependency from the container and forces static cleanup in tests:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void Allowlist_EntriesMustMatchARealDeclaration()
        {
            // The absent control's mirror: an allowlist entry whose declaration was REMOVED (the
            // migration succeeded) must fail here, or the entry is a fossil that hides exactly the
            // drift this fixture exists to catch. Deleting the declaration and the entry is the
            // green end-state of every future migration.
            List<AmbientDeclaration> declarations = ScanProductionTree();

            var stale = new List<string>();
            foreach ((string path, string name, _) in Allowlist)
            {
                bool found = declarations.Exists(d =>
                    string.Equals(d.Path, path, StringComparison.Ordinal) && d.Name == name);
                if (!found)
                {
                    stale.Add($"{path} allows '{name}' but no such declaration exists — the " +
                              "migration completed; delete the allowlist entry.");
                }
            }

            Assert.IsEmpty(stale,
                "Allowlist entries without declarations:\n" + string.Join("\n", stale));
        }

        [Test]
        public void MutationControl_AStrippedCommentCannotSatisfyTheDeclarationScan()
        {
            // The allowlist above names each static in its Reason text; if the stripper stopped
            // working, a doc comment alone could... not satisfy the DECLARATION regex (it needs
            // 'public static <type> <Name>'), but the scan itself would be reading comment text.
            // Prove a comment naming the shape is not matched, with a literal.
            const string commentOnly =
                "// public static SaveManager Instance { get; }\nclass Shape { }";
            string stripped = SourceLint.StripCommentsAndStrings(commentOnly);

            Assert.That(CountDeclarations(stripped), Is.EqualTo(0),
                "A comment that names the declaration shape was matched — the stripper is broken " +
                "and the freeze is vacuous.");
        }

        [Test]
        public void MutationControl_TheScanFindsTheThreadESeams()
        {
            // A green freeze is worthless if the predicate cannot see a real declaration. The five
            // thread-E seams must be found by the exact scanner the freeze runs on.
            List<AmbientDeclaration> declarations = ScanProductionTree();
            foreach (string expected in new[]
                     {
                         "Assets/_PFE/Systems/Campaign/CampaignManager.cs",
                         "Assets/_PFE/Systems/Map/Scripting/ScriptSystem.cs",
                         "Assets/_PFE/Systems/Map/Serialization/SaveManager.cs",
                         "Assets/_PFE/Systems/Map/Streaming/RoomTransitionManager.cs",
                         "Assets/_PFE/Core/RuntimeEntityIdContext.cs",
                     })
            {
                Assert.That(declarations.Exists(d =>
                    string.Equals(d.Path, expected, StringComparison.Ordinal)), Is.True,
                    "The scanner no longer sees " + expected + " — the freeze cannot be trusted.");
            }
        }

        // ── Scanning ─────────────────────────────────────────────────────────────────────

        private static string ReasonFor(string path, string name)
        {
            for (int i = 0; i < Allowlist.Length; i++)
            {
                if (string.Equals(Allowlist[i].Path, path, StringComparison.Ordinal)
                    && Allowlist[i].Name == name)
                {
                    return Allowlist[i].Reason ?? string.Empty;
                }
            }

            return null;
        }

        /// <summary>Walks every production .cs under Assets/_PFE (Tests excluded) and captures
        /// ambient-singleton-shaped property declarations from stripped source.</summary>
        private static List<AmbientDeclaration> ScanProductionTree()
        {
            // Declarations carry the repo-relative Assets/... path the allowlist uses; GetFiles
            // returns absolute paths, so relativize against the same root before matching.
            string projectRoot = SourceLint.ProjectRoot();
            string root = Path.Combine(projectRoot, "Assets", "_PFE");
            Assert.IsTrue(Directory.Exists(root), "Production tree missing: " + root);

            var results = new List<AmbientDeclaration>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                if (relative.Contains("/Tests/"))
                {
                    continue;
                }

                string stripped = SourceLint.StripCommentsAndStrings(File.ReadAllText(file));
                foreach (AmbientDeclaration decl in FindDeclarations(relative, stripped))
                {
                    results.Add(decl);
                }
            }

            return results;
        }

        private static int CountDeclarations(string strippedSource)
        {
            return FindDeclarations("(literal)", strippedSource).Count;
        }

        private static readonly Regex DeclarationPattern = new Regex(
            @"^\s*(?:public|internal|protected)\s+(?:readonly\s+)?static\s+(?!\w+\s*\()" +
            @"(?!(?:class|struct|enum|interface)\b)[\w<>\[\]\.,]+\s+" +
            @"(Current|Instance|Active|Shared)\s*(\{|=>|\r?$)",
            RegexOptions.Compiled);

        private static List<AmbientDeclaration> FindDeclarations(string path, string strippedSource)
        {
            var found = new List<AmbientDeclaration>();
            string[] lines = strippedSource.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = DeclarationPattern.Match(lines[i]);
                if (!m.Success)
                {
                    continue;
                }

                found.Add(new AmbientDeclaration
                {
                    Path = path,
                    Line = i + 1,
                    Name = m.Groups[1].Value,
                    Text = lines[i].Trim(),
                });
            }

            return found;
        }
    }
}
