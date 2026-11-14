using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Wiring lints for the FishNet view-layer adapter.
    ///
    /// <para><b>Why lints and not runtime tests.</b> The adapter is a <c>MonoBehaviour</c> created by
    /// <c>[RuntimeInitializeOnLoadMethod]</c> on a runtime <c>NetworkManager</c>. Offline, any
    /// <c>AddComponent</c> throws <c>SecurityException: ECall methods must be packaged into a system
    /// module</c> before the first assertion. The defects that matter most here are <b>wiring</b>
    /// defects — a missing compile entry, an authentication default left on, an unreliable channel,
    /// a snapshot never handed to the renderer — and every one of them compiles cleanly. A
    /// stripped-source lint is the only guard that can actually fail in this harness.</para>
    ///
    /// <para><b>Why compilation registration is asserted here.</b> The generated csprojs use
    /// <c>EnableDefaultItems=false</c>. A new <c>.cs</c> file that is not listed compiles zero errors
    /// and silently does not exist in the offline assembly — exactly the false-green that started
    /// this fixture. Unity regenerates csprojs on import, but the offline type-check used to verify
    /// the adapter does not.</para>
    /// </summary>
    [TestFixture]
    public class PfeNetSessionWiringLintTests
    {
        private const string CodecFile = "PfeReplicationCodec.cs";
        private const string BroadcastFile = "PfeReplicationBroadcast.cs";
        private const string WireFile = "PfeFishNetWire.cs";
        private const string SinkFile = "PfeRemoteCommandSink.cs";
        private const string ApplierFile = "PfeReplicationWorldApplier.cs";
        private const string SessionFile = "PfeReplicationSession.cs";
        private const string BodyRulesFile = "PfeRemoteBodyRules.cs";
        private const string BodyFile = "PfeRemoteBody.cs";
        private const string ResolverFile = "PfeNetLaunchResolver.cs";
        private const string WorldContractFile = "PfeWorldContract.cs";
        private const string WorldBroadcastFile = "PfeWorldContractBroadcast.cs";

        private const string CoreBridgeFile = "PfeNetLaunchData.cs";
        private const string MenuManagerFile = "MainMenuManager.cs";
        private const string MenuBootstrapperFile = "MainMenuSceneBootstrapper.cs";

        private static readonly string[] AdapterFiles =
        {
            CodecFile, BroadcastFile, WireFile, SinkFile, ApplierFile, SessionFile,
            BodyRulesFile, BodyFile, ResolverFile, WorldContractFile, WorldBroadcastFile,
        };

        private static string Root => SourceLint.ProjectRoot();

        private static string AdapterDir => Path.Combine(Root, "Assets", "PfeNet");

        private static string ReadAdapter(string file)
        {
            string full = Path.Combine(AdapterDir, file);
            Assert.IsTrue(File.Exists(full), "Adapter source missing: " + full);
            string stripped = SourceLint.StripCommentsAndStrings(File.ReadAllText(full));
            Assert.That(stripped, Does.Not.Contain("///"),
                "The stripper did not strip; every assertion below could be satisfied by a comment.");
            return stripped;
        }

        private static void AssertContains(string source, string needle, string why)
        {
            Assert.That(source, Does.Contain(needle), why);
        }

        private static void AssertAbsent(string source, string needle, string why)
        {
            Assert.That(source, Does.Not.Contain(needle), why);
        }

        private static string MenuDir => Path.Combine(Root, "Assets", "_PFE", "UI", "MainMenu");

        private static string CoreDir => Path.Combine(Root, "Assets", "_PFE", "Core");

        private static string ProjectorDir =>
            Path.Combine(Root, "Assets", "_PFE", "Systems", "Replication");

        /// <summary>The producer lives outside Assets/PfeNet, so ReadAdapter cannot reach it. A
        /// cached-null lookup inside this file is the one defect that leaves every PfeNet counter
        /// green while emitting no units at all.</summary>
        private static string ReadProjector()
        {
            return ReadSource(Path.Combine(ProjectorDir, "SimProjectileSnapshotProjector.cs"));
        }

        /// <summary>Reads a project source with comments removed but literals preserved, so lints can
        /// pin an exact expression (a bare identifier survived M40 while printing nothing).</summary>
        private static string ReadSource(string fullPath)
        {
            Assert.IsTrue(File.Exists(fullPath), "Source missing: " + fullPath);
            string stripped = SourceLint.StripCommentsKeepStrings(File.ReadAllText(fullPath));
            Assert.That(stripped, Does.Not.Contain("///"),
                "The stripper did not strip; a comment could satisfy every assertion below.");
            return stripped;
        }

        private static string ReadMenu(string file)
        {
            return ReadSource(Path.Combine(MenuDir, file));
        }

        [Test]
        public void Control_TheLintCanActuallyFail()
        {
            string source = ReadAdapter(SessionFile);
            Assert.Throws<AssertionException>(
                () => AssertContains(source, "__NO_SUCH_SYMBOL_IN_THE_ADAPTER__", "control"),
                "A lint that cannot report a missing symbol is not a guard.");
            AssertContains(source, "public sealed class PfeReplicationSession",
                "Positive control: the stripped file still contains real code.");
        }

        [Test]
        public void EveryAdapterSourceIsRegisteredInAssemblyCSharpCsproj()
        {
            // EnableDefaultItems=false: an unlisted file is a 0-error build that compiled nothing.
            string csproj = File.ReadAllText(Path.Combine(Root, "Assembly-CSharp.csproj"));
            string[] actual = Directory.GetFiles(AdapterDir, "*.cs", SearchOption.TopDirectoryOnly);
            var names = new List<string>();
            for (int i = 0; i < actual.Length; i++) names.Add(Path.GetFileName(actual[i]));

            names.Sort(StringComparer.Ordinal);
            Assert.That(names.Count, Is.EqualTo(AdapterFiles.Length),
                "The adapter file set changed; this lint must be updated rather than left to pass.");
            for (int i = 0; i < AdapterFiles.Length; i++)
            {
                string include = "Assets\\PfeNet\\" + AdapterFiles[i];
                Assert.That(csproj, Does.Contain("<Compile Include=\"" + include + "\" />"),
                    "Missing compile item — a build without this file reports 0 errors: " + include);
            }
        }

        [Test]
        public void EveryAdapterSourceHasItsOwnMetaAndTheFolderHasOneToo()
        {
            string folderMeta = Path.Combine(Root, "Assets", "PfeNet.meta");
            Assert.IsTrue(File.Exists(folderMeta), "Assets/PfeNet.meta is missing.");
            Assert.That(File.ReadAllText(folderMeta), Does.Contain("folderAsset: yes"));

            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guid = new Regex(@"^guid:\s*([0-9a-f]{32})\s*$", RegexOptions.Multiline);
            for (int i = 0; i < AdapterFiles.Length; i++)
            {
                string metaPath = Path.Combine(AdapterDir, AdapterFiles[i] + ".meta");
                Assert.IsTrue(File.Exists(metaPath), "Missing .meta: " + metaPath);
                Match match = guid.Match(File.ReadAllText(metaPath));
                Assert.That(match.Success, Is.True, "No 32-hex guid in " + metaPath);
                Assert.That(guids.Add(match.Groups[1].Value), Is.True,
                    "Duplicate adapter guid: " + match.Groups[1].Value);
            }
            Assert.That(guids.Count, Is.EqualTo(AdapterFiles.Length));
        }

        [Test]
        public void PfeNetHasNoAsmdefSoItCompilesIntoAssemblyCSharp()
        {
            // Adding an asmdef here would break the adapter: PFE.View.asmdef does not reference
            // FishNet.Runtime and asmdef edits are owner-only in this project.
            Assert.That(Directory.GetFiles(AdapterDir, "*.asmdef", SearchOption.TopDirectoryOnly),
                Is.Empty, "An asmdef would move the adapter out of Assembly-CSharp.");
        }

        [Test]
        public void SessionIsCreatedAtRuntimeAndTicksAtViewAligned()
        {
            string source = ReadAdapter(SessionFile);
            AssertContains(source,
                "[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]",
                "Without a runtime bootstrap no player build ever starts a network session.");
            AssertContains(source, "public int TickOrder => SimTickOrder.ViewAligned;",
                "A higher TickOrder is skipped by the SimLoop pause gate; the network would freeze.");
            AssertContains(source, "loop.Register(this);",
                "The session must actually be registered or SimTick is never called.");
            AssertContains(source, "UnityEngine.Object.DontDestroyOnLoad(go);",
                "The bootstrap GameObject must survive a room/scene transition.");
        }

        [Test]
        public void NetworkManagerIsCreatedInactiveWithAnEmptyPrefabCollection()
        {
            string source = ReadAdapter(SessionFile);
            string body = SourceLint.MethodBody(source, "private void Configure(PfeNetConfig config)");
            AssertContains(body, "ScriptableObject.CreateInstance<SinglePrefabObjects>()",
                "FishNet's ValidateSpawnablePrefabs fails in play mode on a null field.");
            AssertContains(body, "_manager = netGo.AddComponent<NetworkManager>();",
                "The NetworkManager must be runtime-created; no scene owns one.");
            int deactivate = body.IndexOf("netGo.SetActive(false)", StringComparison.Ordinal);
            int add = body.IndexOf("AddComponent<NetworkManager>()", StringComparison.Ordinal);
            int activate = body.IndexOf("netGo.SetActive(true)", StringComparison.Ordinal);
            Assert.That(deactivate, Is.GreaterThanOrEqualTo(0));
            Assert.That(add, Is.GreaterThan(deactivate),
                "AddComponent on an ACTIVE object runs Awake before SpawnablePrefabs is assigned.");
            Assert.That(activate, Is.GreaterThan(add),
                "Activation must run FishNet's full initialisation after the assignment.");
        }

        [Test]
        public void BothFishNetBroadcastEndpointsAreRegisteredWithoutAuthentication()
        {
            string session = ReadAdapter(SessionFile);
            AssertContains(session,
                "_manager.ServerManager.RegisterBroadcast<PfeReplicationBroadcast>(",
                "The host must listen for join requests, acks and command batches.");
            AssertContains(session, "requireAuthentication: false",
                "FishNet defaults to true; with no authenticator every client broadcast is kicked.");
            AssertContains(session,
                "_manager.ClientManager.RegisterBroadcast<PfeReplicationBroadcast>(OnClientBroadcast)",
                "The client must listen for accept, baseline chunks and snapshots.");
            AssertContains(session, "OnServerBroadcast", "Host receive handler is missing.");
            AssertContains(session, "OnClientBroadcast", "Client receive handler is missing.");
        }

        [Test]
        public void WireUsesOneReliableOrderedChannelAndNoUnreliableOne()
        {
            string wire = ReadAdapter(WireFile);
            AssertContains(wire, "requireAuthenticated: false",
                "Server-side Broadcast defaults to requiring authentication too.");
            Assert.That(Regex.Matches(wire, Regex.Escape("Channel.Reliable")).Count, Is.EqualTo(2),
                "Both host→client and client→host sends must be on the one ordered reliable channel.");
            AssertAbsent(wire, "Channel.Unreliable",
                "Phase 5 put acks, interest and commands on one ordered sequence space.");

            string broadcast = ReadAdapter(BroadcastFile);
            AssertContains(broadcast, "public struct PfeReplicationBroadcast : IBroadcast",
                "One message type keeps all envelope kinds in a single ordered stream.");
            AssertContains(broadcast, "GenericWriter<PfeReplicationBroadcast>.SetWrite(WriteBroadcast);",
                "Without an explicit writer a player build that skipped ILPP drops every packet.");
            AssertContains(broadcast, "GenericReader<PfeReplicationBroadcast>.SetRead(ReadBroadcast);",
                "Without an explicit reader the receive side cannot decode the broadcast.");
        }

        [Test]
        public void SessionWiresTheWholeV5LoopIncludingTheCommandUplink()
        {
            string source = ReadAdapter(SessionFile);
            AssertContains(source, "new SimReplicationHost(sessionId, projector, _wire, _config.ChunkSize,",
                "The host must publish snapshots from the projector.");
            AssertContains(source, "new SimReplicationClient(nonce, _wire)",
                "The client endpoint must share the same wire.");
            AssertContains(source, "_client.Join(BuildInterest(_declaredLocalHash));",
                "Without a join the host never admits the peer.");
            AssertContains(source, "_client.SendCommands(player.CommandBuffer, _config.CommandWindow);",
                "A v5 handshake that never sends intent is not two players.");
            AssertContains(source, "PfeRemoteCommandSink",
                "Admitted remote commands need a host-side consumer.");
        }

        [Test]
        public void HostBindsEachAdmittedPeerToTheConnectionItsJoinCameFrom()
        {
            string source = ReadAdapter(SessionFile);
            string body = SourceLint.MethodBody(source,
                "private void OnServerBroadcast(NetworkConnection connection, PfeReplicationBroadcast message, Channel channel)");
            AssertContains(body, "_router.BeginReceive(connectionId)",
                "Join accept and baseline chunks are emitted inside Receive, before a peer binding exists.");
            AssertContains(body, "_router.EndReceive();",
                "A leaked receive scope would route every later snapshot to the last sender.");
            AssertContains(body, "_router.Bind(after[i], connectionId)",
                "Outside Receive the wire has only PeerId to route with.");
            AssertContains(body, "_host.Receive(envelope);",
                "The host endpoint must actually consume the envelope.");
        }

        [Test]
        public void ClientAppliesTheBaselineAndTheTileJournalToTheView()
        {
            string session = ReadAdapter(SessionFile);
            AssertContains(session, "_baselineView.Add(",
                "SimReplicationClient.Receive returns void; the view must reassemble its own baseline.");
            AssertContains(session, "_baselineView.TryTakeAssembly(",
                "The assembled baseline must be handed to the renderer, not left pending.");
            AssertContains(session, "SimSnapshotSerializer.Deserialize(",
                "The view needs a decoded snapshot to apply.");
            AssertContains(session, "_applier.Apply(snapshot);",
                "Accepted state must reach the visible rooms.");
            AssertContains(session, "_applier.RefreshRooms(map);",
                "Rooms stream in and out; a stale hash table would silently skip geometry.");

            string applier = ReadAdapter(ApplierFile);
            AssertContains(applier, "query.ApplyMutations(",
                "The tile write must go through the room's own query service.");
            AssertContains(applier, "room.NotifyTilesMutated(entry.Value.ToRect());",
                "Without the notification the renderer and collider chain never rebuild.");
            AssertContains(applier, "_appliedSequences.Contains(mutation.EventSequence)",
                "Repeated unacknowledged events must not rebuild the same room every tick.");
        }

        [Test]
        public void ClientReceiveCallbackCatchesPayloadFailures()
        {
            string source = ReadAdapter(SessionFile);
            string body = SourceLint.MethodBody(source,
                "private void OnClientBroadcast(PfeReplicationBroadcast message, Channel channel)");
            AssertContains(body, "catch (Exception ex)",
                "An uncaught throw unwinds FishNet's receive loop and aborts the remaining packets.");
            AssertContains(body, "_receiveFailures++",
                "A refused payload must be counted, not silently swallowed.");
            AssertContains(body, "SimReplicationPayloadSerializer.ReadBaselineChunk(envelope.Payload)",
                "The view's baseline accumulator must be fed from the wire.");
        }

        [Test]
        public void PureSimReplicationSourcesNeverNameFishNet()
        {
            // The transport seam is ISimReplicationWire. A FishNet type in PFE.Sim would make the
            // whole pure layer unbuildable offline and untestable without the plugin.
            string dir = Path.Combine(Root, "Assets", "_PFE", "Sim", "Replication");
            string[] files = Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly);
            Assert.That(files.Length, Is.GreaterThan(0));
            for (int i = 0; i < files.Length; i++)
            {
                string stripped = SourceLint.StripCommentsAndStrings(File.ReadAllText(files[i]));
                AssertAbsent(stripped, "FishNet",
                    "PFE.Sim must stay transport-neutral: " + Path.GetFileName(files[i]));
                AssertAbsent(stripped, "NetworkManager",
                    "PFE.Sim must stay transport-neutral: " + Path.GetFileName(files[i]));
            }
        }

        // ── Remote body (F-12) ──────────────────────────────────────────────────────────

        [Test]
        public void RemoteBodyIsBuiltDisabledThenActivated()
        {
            // Every Awake in this repo resolves siblings with one non-retrying GetComponent. A
            // component added after its dependency on an ACTIVE object holds null forever, so the
            // body must be assembled while inactive and activated once at the end.
            string body = ReadAdapter(BodyFile);
            int deactivate = body.IndexOf("go.SetActive(false)", StringComparison.Ordinal);
            int firstAdd = body.IndexOf("AddComponent<", StringComparison.Ordinal);
            int activate = body.IndexOf("go.SetActive(true)", StringComparison.Ordinal);

            Assert.That(deactivate, Is.GreaterThanOrEqualTo(0), "The body must be created inactive.");
            Assert.That(firstAdd, Is.GreaterThan(deactivate),
                "The first AddComponent must happen while the object is inactive.");
            Assert.That(activate, Is.GreaterThan(firstAdd),
                "Activation must be last, so every Awake runs against a fully built object.");
            AssertContains(body, "AttachVisual(go, ctx);",
                "A body with no SpriteRenderer exists, ticks and is invisible.");
        }

        [Test]
        public void RemoteBodyIsDrivenOnlyByANewCommand()
        {
            // SetIntent consumes the command's one-shot edges. Re-applying a held command every tick
            // would re-fire jump and dash for as long as the peer stands still.
            string body = ReadAdapter(BodyFile);
            AssertContains(body, "PfeRemoteBodyRules.IsNewerCommand(command.Sequence, _lastCommandSequence)",
                "The body must refuse a command it already applied.");
            AssertContains(body, "Locomotion.SetIntent(",
                "The admitted intent must actually reach the locomotion.");
            AssertContains(body, "_lastCommandSequence = command.Sequence;",
                "The applied sequence must be recorded, or every tick looks new.");
        }

        [Test]
        public void HostBodyIsSimulatedAndClientBodyIsDisplayOnly()
        {
            string body = ReadAdapter(BodyFile);
            AssertContains(body, "if (ctx.Simulated)",
                "Only the host builds a motor and a locomotion; a client-side step would fight the "
                + "position the host just replicated.");
            AssertContains(body, "if (!_simulated || Locomotion == null) return;",
                "Drive must be a no-op on a display body.");
            AssertContains(body, "if (_simulated) return;",
                "ApplyState must be a no-op on a simulated body.");

            string session = ReadAdapter(SessionFile);
            AssertContains(session, "BuildBody((byte)after[i], simulated: true);",
                "The host must build a body for each newly admitted peer.");
            AssertContains(session, "BuildBody(PfeRemoteBodyRules.HostSlot, simulated: false);",
                "On a client the host's player is the remote avatar.");
        }

        [Test]
        public void SessionDrivesHostBodiesAndPlacesClientBodies()
        {
            string session = ReadAdapter(SessionFile);
            AssertContains(session, "DriveBodies();",
                "A host body with no driver is a statue.");
            AssertContains(session, "_sink.TryGetLatest(body.Slot, out command)",
                "The driver must read the admitted command for that body's slot.");
            AssertContains(session, "ApplyBodyStates(snapshot);",
                "A client body that never receives state never moves.");
            AssertContains(session, "local.CaptureSimState()",
                "The spawn position must come from the real rig, not an invented constant.");
        }

        [Test]
        public void RemoteBodyIdentityIsTheSharedPlayerScheme()
        {
            // Both processes must derive the same hash, or the client applies every unit to "not
            // mine" and the avatar silently never moves.
            string rules = ReadAdapter(BodyRulesFile);
            AssertContains(rules, "EntityId.CreateForPlayer(slot)",
                "The body id must be the deterministic per-slot player id.");
            AssertAbsent(rules, "CreateRuntime(",
                "A tick/counter id differs per process, so the two sides could never agree.");
            AssertAbsent(rules, "UnityEngine",
                "The rules must stay pure: the offline wall executes this file, and a UnityEngine "
                + "type would drag the whole scene into a headless fixture.");
            AssertContains(rules, "return bodySlot != localSlot;",
                "A body for the local slot duplicates the local player's hash.");
        }

        // ── Scene lifetime, diagnostics and disconnect teardown (P0) ────────────────────

        [Test]
        public void FishNetPersistsTheRuntimeManagerAndTheSessionReportsADeadGameScope()
        {
            // CORRECTION to an earlier diagnosis: the runtime NetworkManager does NOT die when
            // MainMenuScene is replaced by SampleScene. FishNet's own Awake calls
            // SetDontDestroyOnLoad() and the serialized flag defaults to true. Adding a second
            // DontDestroyOnLoad(netGo) would be a guard that cannot fail. Pin the real mechanism
            // instead, so a plugin upgrade that flips the default is caught.
            string pluginPath = Path.Combine(Root, "Assets", "FishNet", "Runtime", "Managing",
                "NetworkManager.cs");
            Assert.IsTrue(File.Exists(pluginPath), "FishNet NetworkManager.cs moved; this lint must be updated.");
            string plugin = SourceLint.StripCommentsAndStrings(File.ReadAllText(pluginPath));
            AssertContains(plugin, "class NetworkManager",
                "Positive control: the stripped file still contains real plugin code.");
            AssertContains(plugin, "private bool _dontDestroyOnLoad = true;",
                "If this default flips, the runtime-created manager dies at the first scene load.");
            AssertAbsent(plugin, "private bool _dontDestroyOnLoad = false;",
                "Absent control: the false default must not be present.");
            AssertContains(SourceLint.MethodBody(plugin, "private void SetDontDestroyOnLoad()"),
                "DontDestroyOnLoad(this);", "The persistence flag must actually be acted on.");
            AssertContains(SourceLint.MethodBody(plugin, "private void Awake()"),
                "SetDontDestroyOnLoad();", "Awake must invoke persistence during manager initialisation.");

            // The reachable defect is the OTHER side of the lifetime: this session is persistent but
            // the GameLifetimeScope that owns its SimLoop is scene-scoped. Return-to-title then New
            // Game leaves the session reporting initialized while nothing ticks it.
            string session = ReadAdapter(SessionFile);
            string update = SourceLint.MethodBody(session, "private void Update()");
            AssertContains(update, "if (_scope != null) return;",
                "Without the live-scope check the dead-scope counter would run every frame.");
            AssertContains(update, "_staleScopeFrames++;",
                "A dead game scope must be counted, not silently swallowed.");
            AssertContains(SourceLint.MethodBody(session,
                "private void EnsureInitialized()"), "_scope = scope;",
                "The scope must be cached or it can never be checked after the scene load.");
            AssertContains(SourceLint.MethodBody(session, "private void OnGUI()"),
                "_staleScopeFrames", "The counter is useless if the overlay never prints it.");
        }

        [Test]
        public void OverlayReportsEveryRemoteBodyCounterAndError()
        {
            // Wire/host/client counters alone say "nothing wrong" for a body that was never built,
            // never driven, or never placed. Each of those has a distinct counter on PfeRemoteBody.
            string session = ReadAdapter(SessionFile);
            string gui = SourceLint.MethodBody(session, "private void OnGUI()");
            AssertContains(gui, "_bodiesBuilt", "A body that was never built is otherwise invisible.");
            AssertContains(gui, "_bodyFailures", "A failed TryCreate must be counted on the panel.");
            AssertContains(gui, "_bodiesRemoved", "Teardown must be visible on the panel.");
            AssertContains(gui, "_bodyBySlot.Count",
                "The two indexes must be shown separately: a divergence is a real defect.");
            AssertContains(gui, "_lastBodyError", "The named body failure must be printed.");
            AssertContains(gui, "for (int i = 0; i < _bodies.Count; i++)",
                "Per-body rows are the only way to tell which peer is stuck.");
            AssertContains(gui, "body.AppliedCommands", "Host-side drive count is missing.");
            AssertContains(gui, "body.AppliedStates", "Client-side placement count is missing.");
            AssertContains(gui, "body.SkippedStaleCommands", "The command gate must be observable.");
            AssertContains(gui, "body.SkippedForeignStates", "The identity filter must be observable.");
            AssertContains(gui, "body.Slot", "A row without a slot cannot be matched to a peer.");

            // Pin the EXPRESSION, not just the identifier. A line that merely mentions the field
            // (`_ = _lastBodyError;`) keeps the identifier present while printing nothing, so an
            // identifier-only needle is a guard that cannot fail. Keeping string literals is the
            // other direction of the stripper and is exactly what a printed label needs.
            string guiWithStrings = SourceLint.MethodBody(
                SourceLint.StripCommentsKeepStrings(
                    File.ReadAllText(Path.Combine(AdapterDir, SessionFile))),
                "private void OnGUI()");
            AssertContains(guiWithStrings, "GUILayout.Label(",
                "Positive control: the keep-strings stripper left real OnGUI code behind.");
            AssertContains(guiWithStrings, "\"bodyError: \" + _lastBodyError",
                "The named body error must actually be rendered, not just referenced.");

            string build = SourceLint.MethodBody(session,
                "private void BuildBody(byte slot, bool simulated)");
            AssertContains(build, "_lastBodyError =",
                "If no failure path records the error, the panel prints a permanent 'none'.");
        }

        [Test]
        public void BodyRoomComesFromTheLandMapNotFromTheLocalUnitTileQuery()
        {
            // Two two-process LAN play-tests, two different dead room sources:
            //   1. local.Room => _tileQuery?.Room; the shipped player prefab never gets a tile query,
            //      so it is null forever ("no local unit/room to copy from" on BOTH peers).
            //   2. RoomStreamingManager.CurrentRoom => _presentationRoom, written only by
            //      ActivateRoom, whose first call on the normal boot path is a DOOR transition.
            //      Bodies appeared only after walking through a door, with failed=1055 / 135.
            // LandMap.currentRoom is live from boot: MapBridge spawns the player into it and
            // CaptureLocalContract() reads the same map that reported 4 rooms on both machines.
            string session = ReadAdapter(SessionFile);
            // ReadAdapter strips string literals (so a brace inside one cannot break MethodBody).
            // The overlay pin needs the literals, so use the keep-strings variant for that one line.
            string raw = SourceLint.StripCommentsKeepStrings(
                File.ReadAllText(Path.Combine(AdapterDir, SessionFile)));
            string build = SourceLint.MethodBody(session,
                "private void BuildBody(byte slot, bool simulated)");

            AssertContains(build, "RoomInstance room = CurrentRoomInstance();",
                "The body room must come from the shared resolver, not a per-call lookup that "
                + "can silently differ from the room the interest declaration names.");
            Assert.That(build.IndexOf("RoomInstance room = local != null ? local.Room : null;",
                    StringComparison.Ordinal), Is.LessThan(0),
                "Absent control: reading local.Room as the room source is the exact runtime defect "
                + "found by the first play-test.");
            AssertContains(build, "UnitController local = player;",
                "PlayerController derives from UnitController; a child lookup finds nothing on "
                + "the shipped rig and leaves the body unbuilt.");
            AssertContains(build, "_lastError.StartsWith(",
                "A body that finally builds after a room transition must clear the sticky error: "
                + "the second play-test showed bodies built=1 live=1 next to a stale bodyError. "
                + "Pin the success-block expression; `_lastBodyError =` alone also occurs on the "
                + "failure path and would pass with the clearing deleted.");

            string resolve = SourceLint.MethodBody(session,
                "private RoomInstance CurrentRoomInstance()");
            AssertContains(resolve, "LandMap map = CurrentMap();",
                "The live LandMap is the only room source proven non-null at boot on BOTH peers.");
            AssertContains(resolve, "map.currentRoom",
                "Pin the expression, not the identifier: a resolver that merely reads a map but "
                + "returns the streaming room would still leave bodies unbuildable until a door.");

            string gui = SourceLint.MethodBody(raw, "private void OnGUI()");
            AssertContains(gui, "\"room=\" + _lastRoomId + \"  source=\" + _lastRoomSource",
                "Without a room/source readout the overlay cannot say WHICH lookup was dead; "
                + "two play-tests were spent inferring it from error text alone.");

            string tick = SourceLint.MethodBody(session, "public void SimTick(int tickIndex)");
            AssertContains(tick, "BuildPendingBodies();",
                "A peer can join before the world build produces a room; the one-shot bind "
                + "callback would otherwise leave that peer without a body forever.");
            string pending = SourceLint.MethodBody(session, "private void BuildPendingBodies()");
            AssertContains(pending, "_host.JoinedPeerIds()",
                "The retry must enumerate the peers the host actually admitted.");
        }

        [Test]
        public void DisconnectResolvesThePeerBeforeUnbindAndDestroysTheBody()
        {
            string session = ReadAdapter(SessionFile);
            AssertContains(session,
                "_manager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;",
                "A teardown handler that is never subscribed cannot tear anything down.");

            string callback = SourceLint.MethodBody(session,
                "private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)");
            AssertContains(callback, "_router.TryGetPeer(connectionId, out peerId)",
                "A stopped connection names only a connection id; the body is indexed by peer slot.");
            AssertContains(callback, "_router.Unbind(connectionId);",
                "The router binding for a stopped connection must be released.");
            AssertContains(callback,
                "PfeRemoteBodyRules.TryRemovalSlot(peerId, _localSlot, out slot)",
                "A raw (byte)peerId cast would delete the wrong body for peer 0 or peer > 255.");
            AssertContains(callback, "RemoveBody(slot);",
                "PfeRemoteBody.Destroy() has no caller unless the callback removes the body.");

            int resolve = callback.IndexOf("_router.TryGetPeer", StringComparison.Ordinal);
            int unbind = callback.IndexOf("_router.Unbind", StringComparison.Ordinal);
            Assert.That(resolve, Is.GreaterThanOrEqualTo(0));
            Assert.That(unbind, Is.GreaterThan(resolve),
                "Unbind erases _peerByConnection: resolving afterwards always fails and the body "
                + "survives its own disconnect forever.");
        }

        [Test]
        public void BodyRemovalClearsBothIndexesAndClientStopTearsDownAllBodies()
        {
            string session = ReadAdapter(SessionFile);
            string remove = SourceLint.MethodBody(session, "private void RemoveBody(byte slot)");
            AssertContains(remove, "_bodyBySlot.Remove(slot);",
                "A stale index entry blocks any rejoin with the same slot.");
            AssertContains(remove, "_bodies.RemoveAt(i);",
                "A body left in the list keeps receiving Drive/ApplyState calls.");
            AssertContains(remove, "body.Destroy();",
                "Destruction must actually reach the GameObject.");
            AssertContains(remove, "_bodiesRemoved++;", "Removal must be counted.");

            string removeAll = SourceLint.MethodBody(session, "private void RemoveAllBodies()");
            AssertContains(removeAll, "body.Destroy();", "Every display body must be destroyed.");
            AssertContains(removeAll, "_bodies.Clear();", "The list must be cleared.");
            AssertContains(removeAll, "_bodyBySlot.Clear();", "The slot index must be cleared.");

            AssertContains(SourceLint.MethodBody(session,
                "private void OnClientConnectionState(ClientConnectionStateArgs args)"),
                "RemoveAllBodies();",
                "A client whose host vanished would otherwise keep a statue forever.");
            AssertContains(SourceLint.MethodBody(session, "private void OnDestroy()"),
                "RemoveAllBodies();", "Bodies must not outlive the session that owns them.");
        }

        // ── P1: menu-driven LAN launch ────────────────────────────────────────────────────

        [Test]
        public void ConnectIsAppendedToTheScreenEnumAndHasAShowMethod()
        {
            string manager = ReadMenu(MenuManagerFile);
            Match block = Regex.Match(manager, @"enum MainMenuScreen\s*\{[^}]*\}");
            Assert.That(block.Success, Is.True, "The MainMenuScreen enum disappeared.");
            int about = block.Value.IndexOf("About", StringComparison.Ordinal);
            int connect = block.Value.IndexOf("Connect", StringComparison.Ordinal);
            Assert.That(about, Is.GreaterThanOrEqualTo(0));
            Assert.That(connect, Is.GreaterThan(about),
                "Connect must be APPENDED: these values are serialized on inspector fields, so "
                + "inserting it earlier silently renumbers Options/Mods/About.");

            AssertContains(manager, "public void ShowConnect()",
                "A screen with no show method cannot be reached from the navigation buttons.");
            AssertContains(SourceLint.MethodBody(manager, "public void ShowConnect()"),
                "OpenScreen(MainMenuScreen.Connect);", "The show method must open the new screen.");
        }

        [Test]
        public void ConnectScreenIsRegisteredInTheRuntimeGeneratedMenu()
        {
            string menu = ReadMenu(MenuBootstrapperFile);
            AssertContains(menu, "CanvasGroup connectPanel = BuildConnectPanel(overlayRoot);",
                "The Connect panel must be built with every other runtime screen.");
            AssertContains(menu, "{ MainMenuScreen.Connect, connectPanel }",
                "An unregistered screen falls back to Title with a warning.");
            AssertContains(menu, "\"Connect (LAN)\"", "The navigation item itself is missing.");
            AssertContains(menu, "_menuManager.ShowConnect()",
                "The Connect button must open the Connect screen.");

            string panel = SourceLint.MethodBody(menu,
                "CanvasGroup BuildConnectPanel(RectTransform parent)");
            AssertContains(panel, "PfeNetLaunchRole.Host",
                "A panel with no Host choice cannot start a host session.");
            AssertContains(panel, "PfeNetLaunchRole.Client",
                "A panel with no Join choice cannot start a client session.");
            AssertContains(panel, "_connectAddressInput = MakeInput(",
                "Join needs an address field; falling back to loopback hides a LAN misconfiguration.");
            AssertContains(panel, "_connectPortInput = MakeInput(",
                "The port must be editable, or both machines cannot be told to agree.");
            AssertContains(panel, "PfeNetLaunchRules.DefaultPort",
                "The default port must come from the shared rule, not a second literal.");
            AssertContains(panel, "OnConnectStartPressed",
                "A panel whose Start button is not wired does nothing.");
            AssertContains(panel, "PfeNetLaunchData.ClearPending()",
                "Cancel must discard a request whose scene load never happened.");
        }

        [Test]
        public void ConnectStartWritesTheBridgeBeforeItLoadsTheGameScene()
        {
            string menu = ReadMenu(MenuBootstrapperFile);
            string start = SourceLint.MethodBody(menu, "void OnConnectStartPressed()");
            AssertContains(start, "PfeNetLaunchData.TryRequest(_connectRole, _connectAddressInput?.text, port)",
                "The static bridge is the ONLY seam from PFE.View to the Assembly-CSharp adapter.");
            AssertContains(start, "_connectStatusText.text = \"Cannot start: \" + PfeNetLaunchData.LastError",
                "A refused request must be visible in the panel, not a button that does nothing.");
            AssertContains(start, "return;", "A refused request must not load the game scene.");
            AssertContains(start, "GameBootData.SetPendingNewGame(pending);",
                "A LAN start is still a new-game boot; the game scene needs its settings.");
            AssertContains(start, "SceneLoader.LoadSceneAsync(SampleSceneName",
                "The session watcher only sees the request once the game scene is loading.");

            int request = start.IndexOf("PfeNetLaunchData.TryRequest", StringComparison.Ordinal);
            int scene = start.IndexOf("SceneLoader.LoadSceneAsync", StringComparison.Ordinal);
            Assert.That(request, Is.GreaterThanOrEqualTo(0));
            Assert.That(scene, Is.GreaterThan(request),
                "The bridge must be written BEFORE the scene transition starts.");
            int refusal = start.IndexOf("Cannot start: ", StringComparison.Ordinal);
            Assert.That(refusal, Is.GreaterThan(request),
                "The refusal must be checked after validating, not before.");
        }

        [Test]
        public void NormalNewGameClearsAnyPendingLanLaunch()
        {
            string menu = ReadMenu(MenuBootstrapperFile);
            AssertContains(SourceLint.MethodBody(menu, "void OnStartPressed()"),
                "PfeNetLaunchData.ClearPending();",
                "An ordinary single-player start must not inherit a LAN request whose scene load failed.");
        }

        [Test]
        public void BootstrapCreatesAMenuWatcherAndCommandLineWins()
        {
            string session = ReadAdapter(SessionFile);
            string bootstrap = SourceLint.MethodBody(session, "private static void Bootstrap()");
            AssertContains(bootstrap, "PfeNetLaunchResolver.TryResolveCommandLine(",
                "The command-line play-test path must still be resolved first.");
            AssertContains(bootstrap, "UnityEngine.Object.DontDestroyOnLoad(go);",
                "The watcher must survive the MainMenuScene -> SampleScene transition.");
            AssertContains(bootstrap, "go.AddComponent<PfeReplicationSession>()",
                "Without a live component there is no Update() to poll the bridge.");
            AssertContains(bootstrap, "PfeNetLaunchData.ClearPending();",
                "An explicit switch is an operator override; a stale menu request must be discarded.");
            AssertContains(bootstrap, "session.Configure(cli);", "The CLI path must still configure.");
            AssertContains(bootstrap, "session._awaitMenu = true;",
                "A built player with no switch needs a watcher, not an early return.");

            int clear = bootstrap.IndexOf("PfeNetLaunchData.ClearPending()", StringComparison.Ordinal);
            int configure = bootstrap.IndexOf("session.Configure(cli)", StringComparison.Ordinal);
            Assert.That(clear, Is.GreaterThanOrEqualTo(0));
            Assert.That(configure, Is.GreaterThan(clear),
                "Clearing after Configure would let a stale request start a second session.");

            string poll = SourceLint.MethodBody(session, "private void PollMenuLaunch()");
            AssertContains(poll, "if (!_awaitMenu) return;",
                "Once a config owns the process, a second menu request must be ignored.");
            AssertContains(poll, "PfeNetLaunchData.ConsumePending()",
                "Peek would leave the request pending and start the session again on the next frame.");
            AssertContains(poll, "PfeNetLaunchResolver.TryResolveMenu(request, out config, out reason)",
                "A raw request must be validated before its port is cast to ushort.");
            AssertContains(poll, "_awaitMenu = false;", "The watcher must disarm after one launch.");
            AssertContains(poll, "Configure(config);",
                "Resolving a request without configuring starts no transport at all.");

            string update = SourceLint.MethodBody(session, "private void Update()");
            AssertContains(update, "if (_config == null)",
                "A dormant session must not try to initialise against a menu scene with no container.");
            AssertContains(update, "PollMenuLaunch();", "The watcher has no caller.");
        }

        [Test]
        public void OverlayNamesTheLaunchSourceAndThePendingBridgeValue()
        {
            string raw = SourceLint.StripCommentsKeepStrings(
                File.ReadAllText(Path.Combine(AdapterDir, SessionFile)));
            string gui = SourceLint.MethodBody(raw, "private void OnGUI()");
            AssertContains(gui, "\"source=\" + _launchSource",
                "Pin the expression, not the identifier: `_ = _launchSource;` would keep the name "
                + "while printing nothing.");
            AssertContains(gui, "PfeNetLaunchData.HasPending",
                "The overlay must be able to say whether a menu request was left unconsumed.");
        }

        [Test]
        public void LaunchBridgeIsRegisteredOnBothSidesOfTheAssemblyBoundary()
        {
            string view = File.ReadAllText(Path.Combine(Root, "PFE.View.csproj"));
            AssertContains(view, "<Compile Include=\"Assets\\_PFE\\Core\\PfeNetLaunchData.cs\" />",
                "EnableDefaultItems=false: an unlisted file is a 0-error build that compiled nothing.");
            string assembly = File.ReadAllText(Path.Combine(Root, "Assembly-CSharp.csproj"));
            AssertContains(assembly, "<Compile Include=\"Assets\\PfeNet\\PfeNetLaunchResolver.cs\" />",
                "The resolver must be part of the adapter assembly.");
            string wall = File.ReadAllText(Path.Combine(Root, "AgentVerifyWall.csproj"));
            AssertContains(wall, "<Compile Include=\"Assets\\PfeNet\\PfeNetLaunchResolver.cs\" />",
                "An uncompiled resolver would leave its behaviour tests unexecutable.");

            string bridge = ReadSource(Path.Combine(CoreDir, CoreBridgeFile));
            AssertAbsent(bridge, "FishNet",
                "PFE.View.asmdef does not reference FishNet.Runtime; the bridge must stay plain data.");
            AssertAbsent(bridge, "PfeReplicationSession",
                "PFE.View cannot reference the predefined Assembly-CSharp.");

            var guid = new Regex(@"^guid:\s*([0-9a-f]{32})\s*$", RegexOptions.Multiline);
            Match coreMeta = guid.Match(File.ReadAllText(Path.Combine(CoreDir, CoreBridgeFile + ".meta")));
            Match resolverMeta = guid.Match(File.ReadAllText(Path.Combine(AdapterDir, ResolverFile + ".meta")));
            Assert.That(coreMeta.Success, Is.True, "The bridge .meta has no guid.");
            Assert.That(resolverMeta.Success, Is.True, "The resolver .meta has no guid.");
            Assert.That(coreMeta.Groups[1].Value, Is.Not.EqualTo(resolverMeta.Groups[1].Value),
                "Two assets sharing a guid makes Unity silently drop one of them.");
        }

        // ── P2: host world / campaign contract wiring ──────────────────────────────────

        [Test]
        public void WorldContractIsPureAndCompiledIntoTheWallButNotItsFishNetBroadcast()
        {
            string pure = ReadAdapter(WorldContractFile);
            AssertAbsent(pure, "FishNet",
                "The pure contract must be executable in an offline harness with no FishNet reference.");
            AssertAbsent(pure, "UnityEngine",
                "A Unity dependency here would make every comparison rule untestable offline.");
            AssertAbsent(pure, "ComputeFnv1a64",
                "A second room hash would never agree with the projector's RoomKey.");
            AssertContains(pure, "SimSnapshotProjectionRules.FoldRoomSetSignature",
                "Reuse the project's canonical order-independent fold, do not invent a digest.");

            string assembly = File.ReadAllText(Path.Combine(Root, "Assembly-CSharp.csproj"));
            AssertContains(assembly, "<Compile Include=\"Assets\\PfeNet\\PfeWorldContract.cs\" />",
                "EnableDefaultItems=false: an unlisted file is a 0-error build that compiled nothing.");
            AssertContains(assembly, "<Compile Include=\"Assets\\PfeNet\\PfeWorldContractBroadcast.cs\" />",
                "The FishNet broadcast must be in the adapter assembly.");

            string wall = File.ReadAllText(Path.Combine(Root, "AgentVerifyWall.csproj"));
            AssertContains(wall, "<Compile Include=\"Assets\\PfeNet\\PfeWorldContract.cs\" />",
                "Without this compile item the world fixtures would execute nothing.");
            AssertAbsent(wall, "<Compile Include=\"Assets\\PfeNet\\PfeWorldContractBroadcast.cs\" />",
                "Absent control: the FishNet-dependent broadcast must stay OUT of the offline wall.");
        }

        [Test]
        public void WorldContractBroadcastHasAnExplicitSerializerAndAnUnauthenticatedSend()
        {
            string broadcast = ReadSource(Path.Combine(AdapterDir, WorldBroadcastFile));
            AssertContains(broadcast, "public struct PfeWorldContractBroadcast : IBroadcast",
                "A separate broadcast keeps world metadata out of the versioned Sim envelope stream.");
            AssertContains(broadcast, "GenericWriter<PfeWorldContractBroadcast>.SetWrite(WriteBroadcast)",
                "Without a custom writer FishNet drops the packet with only a log line.");
            AssertContains(broadcast, "writer.WriteUInt8ArrayAndSize(value.Bytes)",
                "Pin the writer expression, not just the class name.");
            AssertContains(broadcast, "reader.ReadUInt8ArrayAndSizeAllocated()",
                "The reader must mirror the writer's framing.");

            string session = ReadSource(Path.Combine(AdapterDir, SessionFile));
            AssertContains(SourceLint.MethodBody(session, "private void Configure(PfeNetConfig config)"),
                "PfeWorldContractBroadcastSerializers.Register();",
                "Configuring only the envelope serializer leaves the world contract undeliverable.");

            string publish = SourceLint.MethodBody(session, "private void PublishPendingWorldContracts()");
            AssertContains(publish, "requireAuthenticated: false",
                "FishNet's Broadcast is requireAuthenticated (RegisterBroadcast is requireAuthentication). "
                + "The default true drops the packet when there is no authenticator.");
            AssertContains(publish, "_manager.ServerManager.Broadcast(peer,",
                "The contract must be addressed to the peer that just joined.");
            AssertContains(publish, "PfeWorldContractRules.ShouldPublish(_config.IsHost, published, _localContract)",
                "Pin the expression: a host-only, once-per-peer gate is the whole publication rule.");
            AssertContains(publish, "_contractSentConnectionIds.Add(connectionId)",
                "Without remembering the peer, every tick would re-send the same contract.");
        }

        [Test]
        public void HostCapturesItsLiveWorldFromTheCanonicalRoomKeyAndCampaignState()
        {
            string session = ReadSource(Path.Combine(AdapterDir, SessionFile));
            string capture = SourceLint.MethodBody(session, "private PfeWorldContract CaptureLocalContract()");
            AssertContains(capture, "SimSnapshotProjectionRules.RoomKey(room.id)",
                "Pin the expression: the contract must name rooms exactly as every SimRoomDelta does.");
            AssertContains(capture, "CampaignManager.Current",
                "The host's campaign state is the state the client must follow.");
            AssertContains(capture, "campaign.CurrentLandId.CurrentValue",
                "A missing land id makes every campaign comparison silently agree.");
            AssertContains(capture, "campaign.MissionId", "Mission identity is part of the contract.");
            AssertContains(capture, "campaign.CurrentCheckpoint", "Checkpoint identity is part of the contract.");
            AssertContains(capture, "GlobalDifficulty.Value", "Difficulty is part of the contract.");
            AssertContains(capture, "PfeWorldContractRules.NormalizeRoomKeys(keys.ToArray())",
                "An unsorted room set would compare unequal on two identical maps.");
            AssertContains(capture, "if (keys.Count == 0) return null;",
                "A zero-room capture must read as 'not built yet', never as an empty world.");

            string join = SourceLint.MethodBody(session,
                "private void OnServerBroadcast(NetworkConnection connection, PfeReplicationBroadcast message, Channel channel)");
            AssertContains(join, "_pendingContractPeers.Add(connection)",
                "A peer that never gets queued never receives a contract.");
            AssertContains(SourceLint.MethodBody(session, "public void SimTick(int tickIndex)"),
                "PublishPendingWorldContracts();",
                "Publishing only at join would freeze a half-built host world forever.");
        }

        [Test]
        public void ClientComparesBeforeItAppliesAndDisconnectResetsTheVerdict()
        {
            string session = ReadSource(Path.Combine(AdapterDir, SessionFile));
            string recv = SourceLint.MethodBody(session,
                "private void OnClientWorldContract(PfeWorldContractBroadcast message, Channel channel)");
            AssertContains(recv, "PfeWorldContractCodec.TryDeserialize(message.Bytes, out contract, out reason)",
                "A raw byte[] must be bounds-checked before it is trusted.");
            AssertContains(recv, "_hostContract = contract;", "The received contract must be retained.");
            AssertContains(recv, "RefreshWorldVerdict();", "Receiving without comparing verifies nothing.");

            string compare = SourceLint.MethodBody(session, "private void RefreshWorldVerdict()");
            AssertContains(compare, "PfeWorldContractRules.Compare(_hostContract, _localContract, out _worldReason)",
                "Pin the expression, not the identifier: `_ = _hostContract;` would keep the name.");

            string apply = SourceLint.MethodBody(session,
                "private void OnClientBroadcast(PfeReplicationBroadcast message, Channel channel)");
            AssertContains(apply, "PfeWorldContractRules.CanApplyHostState(_worldVerdict)",
                "A foreign map must never receive RoomHash-addressed tile/loot/unit state.");
            AssertContains(apply, "_worldAppliesSkipped++",
                "A silent block is indistinguishable from 'no snapshots arrived'.");
            int gate = apply.IndexOf("CanApplyHostState", StringComparison.Ordinal);
            int applier = apply.IndexOf("_applier.Apply(snapshot)", StringComparison.Ordinal);
            Assert.That(gate, Is.GreaterThanOrEqualTo(0));
            Assert.That(applier, Is.GreaterThan(gate),
                "The gate must run BEFORE the applier, not after it.");

            AssertContains(SourceLint.MethodBody(session, "public void SimTick(int tickIndex)"),
                "PfeWorldContractRules.ShouldRetryLocalCapture(_worldVerdict, _tickCount)",
                "A contract arriving before the local world build must not stay 'no local world' forever.");

            string stopped = SourceLint.MethodBody(session,
                "private void OnClientConnectionState(ClientConnectionStateArgs args)");
            AssertContains(stopped, "_hostContract = null;",
                "The verdict belongs to the dead host.");
            AssertContains(stopped, "_worldVerdict = PfeWorldVerdict.Unknown;",
                "A WorldMismatch from a dead host would block every snapshot from the next one.");

            string disconnect = SourceLint.MethodBody(session,
                "private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)");
            AssertContains(disconnect, "_contractSentConnectionIds.Remove(connectionId);",
                "A rejoin must receive a fresh contract.");

            string init = SourceLint.MethodBody(session, "private void EnsureInitialized()");
            AssertContains(init, "RegisterBroadcast<PfeWorldContractBroadcast>(OnClientWorldContract)",
                "An unregistered handler means FishNet discards the contract on arrival.");
            AssertContains(SourceLint.MethodBody(session, "private void OnDestroy()"),
                "UnregisterBroadcast<PfeWorldContractBroadcast>(OnClientWorldContract)",
                "A surviving handler would keep receiving after the session is destroyed.");
        }

        [Test]
        public void OverlayNamesTheWorldVerdictAndTheBlockedApplyCount()
        {
            string raw = SourceLint.StripCommentsKeepStrings(
                File.ReadAllText(Path.Combine(AdapterDir, SessionFile)));
            string gui = SourceLint.MethodBody(raw, "private void OnGUI()");
            AssertContains(gui, "\"world=\" + PfeWorldContractRules.Describe(_worldVerdict)",
                "Pin the expression: `_ = _worldVerdict;` would keep the identifier while printing nothing.");
            AssertContains(gui, "\"world contract rx=\" + _worldContractsReceived",
                "A contract that never arrived must be visible on the panel.");
            AssertContains(gui, "\"worldReason: \" + _worldReason",
                "The named reason is the only thing that says WHICH room or campaign field diverged.");
            AssertContains(gui, "RoomCount(_hostContract)",
                "Room counts distinguish 'no contract' from 'empty world'.");
            AssertContains(gui, "54f + 54f",
                "Three extra rows need their own height or the last body/row is clipped.");
        }

        // ── P3 / F-12 proper: local-player projection ────────────────────────────────────

        [Test]
        public void LocalPlayerIsStampedWithTheSharedIdentityAndALiveRoomQuery()
        {
            // The third two-process play-test: transport, commands and world contract all green,
            // client body state=0 foreign=28. CollectUnits() skips a unit without HasEntityId and
            // the projector attributes a unit by ReferenceEquals(controller.Room, room); the shipped
            // player prefab has neither. NPCs were the 28 foreign records. No compile or offline
            // behaviour test can see a MonoBehaviour's missing SetTileQuery call — only this lint.
            string session = ReadAdapter(SessionFile);
            string tick = SourceLint.MethodBody(session, "public void SimTick(int tickIndex)");
            AssertContains(tick, "EnsureBootRoomActive();",
                "Without a boot activation the projector has zero rooms and emits zero unit deltas.");
            AssertContains(tick, "EnsureLocalPlayerNetworkIdentity();",
                "The id/room stamp must be retried every tick: player and room are both built late.");
            AssertContains(tick, "PfeRemoteBodyRules.TryLocalPlayerSlot(false, _client.PeerId, out resolvedSlot)",
                "A client's local slot is the host-assigned peer id, not a hardcoded 1.");
            AssertContains(tick, "_projectedLocalPlayers = CountProjectedLocalPlayers();",
                "A green ProjectedUnitCount full of NPCs must not stand in for the avatar readback.");

            string init = SourceLint.MethodBody(session, "private void EnsureInitialized()");
            AssertContains(init, "_projector = projector;",
                "The container already resolved the projector; dropping it loses the only readback.");

            string stamp = SourceLint.MethodBody(session,
                "private void EnsureLocalPlayerNetworkIdentity()");
            AssertContains(stamp, "player.AssignEntityId(EntityId.CreateForPlayer(slot),",
                "The id must come from the SAME per-slot scheme the peer body uses, or the hashes differ.");
            Assert.That(session.IndexOf("EntityId.CreateRuntime(", StringComparison.Ordinal),
                Is.LessThan(0),
                "Absent control: a runtime/minted id differs per process and can never match a body.");
            AssertContains(stamp, "player.SetTileQuery(new UnifiedTileQueryService(room));",
                "UnitController.Room is _tileQuery?.Room; without a query the player is attributed to no room.");
            AssertContains(stamp, "PfeRemoteBodyRules.NeedsRoomRebind(_boundRoomId, roomId)",
                "A door transition must rebind the query or the player stays in the deactivated room.");
            AssertContains(stamp, "ReferenceEquals(_boundPlayer, player)",
                "A scene reload replaces the player: an unchanged room id still needs a fresh query.");
            AssertContains(stamp, "PfeRemoteBodyRules.TryLocalPlayerSlot(_config.IsHost, peerId, out slot)",
                "An unjoined client must refuse to stamp rather than claim the host's identity.");

            string boot = SourceLint.MethodBody(session, "private void EnsureBootRoomActive()");
            AssertContains(boot, "streaming.ActivateRoom(room);",
                "MapBridge creates the streaming manager at boot but never activates it; ActiveRooms "
                + "stays empty until a door transition, so the projector emits no room deltas at all.");
            AssertContains(boot, "ActiveRoomsContain(streaming, room)",
                "Activation must be a no-op when a transition already won the race.");
            AssertContains(boot, "_bootRoomActivated) return;",
                "Once-only: RoomTransitionManager owns activation after boot.");
            string resolve = SourceLint.MethodBody(session, "private RoomStreamingManager ResolveStreaming()");
            AssertContains(resolve, "if (_streaming == null)",
                "A one-shot lookup from EnsureInitialized caches null: MapBridge builds the manager "
                + "at the END of a multi-second boot coroutine.");
            AssertContains(resolve, "FindFirstObjectByType<RoomStreamingManager>()",
                "Pin the expression, not just the cached field.");
        }

        [Test]
        public void ClientBodiesOnlyAcceptUnitsFromTheRoomTheCameraFollows()
        {
            // SimUnitState.Px/Py are room-local pixels. The old receiver offered EVERY unit in
            // EVERY room delta to EVERY display body, so a peer in another room was drawn at its
            // own local coordinates inside the room the camera happened to follow.
            string session = ReadAdapter(SessionFile);
            string raw = SourceLint.StripCommentsKeepStrings(
                File.ReadAllText(Path.Combine(AdapterDir, SessionFile)));
            string apply = SourceLint.MethodBody(session,
                "private void ApplyBodyStates(in SimSnapshot snapshot)");
            AssertContains(apply, "ulong localRoomHash = CurrentRoomHash();",
                "The receiver must name its own room with the same RoomKey the delta carries.");
            AssertContains(apply, "ulong roomHash = snapshot.Rooms[r].RoomHash;",
                "Without reading the delta's room hash there is nothing to compare against.");
            AssertContains(SourceLint.MethodBody(raw,
                    "private void ApplyBodyStates(in SimSnapshot snapshot)"),
                "if (!PfeRemoteBodyRules.CanPlaceBodyInRoom(roomHash, localRoomHash))",
                "Pin the NEGATED gate: mentioning the rule without branching on it is not a guard.");
            AssertContains(apply, "continue;",
                "Foreign-room records must be skipped, not merely counted.");
            AssertContains(apply, "_skippedForeignRooms += units.Length;",
                "A silently skipped room is indistinguishable from a lost packet.");
            AssertContains(apply, "_bodies[b].SetRemoteVisible(false);",
                "Hide before show: a peer that leaves the room must not leave a statue behind.");
            AssertContains(apply, "body.ApplyState(units[u]);",
                "The matching record must still reach the body.");

            string body = ReadAdapter(BodyFile);
            string place = SourceLint.MethodBody(body, "public void ApplyState(in SimUnitState state)");
            AssertContains(place, "SetRemoteVisible(true);",
                "Only a successful identity+position match may turn the display body back on.");
            string visible = SourceLint.MethodBody(body, "public void SetRemoteVisible(bool visible)");
            AssertContains(visible, "if (_simulated || _root == null) return;",
                "A simulated host body is authority, not a projection; deactivating it stops locomotion.");
            AssertContains(visible, "root.SetActive(visible);",
                "Pin the expression: a method that only reads activeSelf changes nothing.");
        }

        [Test]
        public void OverlayReportsProjectionIdentityAndForeignRoomReadbacks()
        {
            string raw = SourceLint.StripCommentsKeepStrings(
                File.ReadAllText(Path.Combine(AdapterDir, SessionFile)));
            string gui = SourceLint.MethodBody(raw, "private void OnGUI()");
            AssertContains(gui, "\"localPlayer=\" + _localSlot + \"  stamps=\" + _identityStamps",
                "stamps=0 with a live player is the identity failure from the third play-test.");
            AssertContains(gui, "\"  localProj=\" + _projectedLocalPlayers + \"  roomSkip=\" + _skippedForeignRooms",
                "localProj is the only row that says the LOCAL PLAYER reached the wire.");
            AssertContains(gui, "_projector.ProjectedUnitCount",
                "A projector that emits only NPCs still looks healthy on every transport counter.");
            AssertContains(gui, "_projector.ProjectedPlayerUnitCount",
                "The projector's own ownership readback belongs next to the local-hash readback.");
            AssertContains(gui, "54f + 60f",
                "Three projection rows need their own height or the last body row is clipped.");
            AssertContains(gui, "\"projTick=\" + (_projector != null ? _projector.ProjectionCount : 0)",
                "projTick separates 'producer never ticked' from 'producer ticked and found nothing'.");
            AssertContains(gui, "\"  stream=\" + (_projector != null && _projector.HasStreaming ? \"yes\" : \"no\")",
                "stream=no is the cached-null streaming manager from the fourth play-test.");
            AssertContains(gui, "\"  fault=\" + CurrentProjectionFault()",
                "Pin the expression: printing a constant would keep the label while saying nothing.");
            AssertContains(gui, "\"  badHash=0x\" + _applier.LastUnknownRoomHash.ToString(\"X16\")",
                "unknownRoom=36923 must name WHICH room the host is addressing.");
        }

        [Test]
        public void ProjectorRetriesTheStreamingManagerInsteadOfCachingNull()
        {
            // Fourth play-test: bootAct=1 (the SESSION's retrying resolver found the manager and
            // activated the boot room) next to projUnits=0 projPlayers=0 localProj=0. The projector
            // is an IAutoRegisteredSimTickable attached by SimTickRegistrar at container build, so its
            // first tick runs while MapBridge is still building the world; MapBridge creates the
            // RoomStreamingManager at the END of that coroutine. A `_streamingResolved` flag pins the
            // first null lookup for the whole session and every snapshot has zero room deltas.
            string projector = ReadProjector();
            string resolve = SourceLint.MethodBody(projector,
                "private RoomStreamingManager ResolveStreaming()");
            AssertContains(resolve, "if (_streaming == null)",
                "Unity's overloaded == null re-arms the lookup after a scene unload destroys the cache.");
            AssertContains(resolve, "UnityEngine.Object.FindFirstObjectByType<RoomStreamingManager>()",
                "Pin the expression: a field that is never assigned is not a lookup.");
            AssertAbsent(resolve, "_streamingResolved",
                "A one-shot resolved flag is the exact defect: it caches the boot-time null forever.");

            string rawProjector = SourceLint.StripCommentsAndStrings(
                File.ReadAllText(Path.Combine(ProjectorDir, "SimProjectileSnapshotProjector.cs")));
            AssertAbsent(rawProjector, "_streamingResolved",
                "Absent control: no other method may reintroduce the one-shot cache.");
            AssertContains(rawProjector, "public bool HasStreaming => _streaming != null;",
                "The overlay needs a liveness readback that a zero counter cannot counterfeit.");
            AssertContains(rawProjector, "public int ActiveRoomCount => _rooms.Count;",
                "Zero rooms with a live manager is a different failure from a dead manager.");
            AssertContains(rawProjector, "public int IdentifiedUnitCount => _units.Count;",
                "Separates 'the registry is empty' from 'units could not be attributed to a room'.");

            string session = ReadAdapter(SessionFile);
            string fault = SourceLint.MethodBody(session,
                "private PfeRemoteBodyRules.ProjectionFault CurrentProjectionFault()");
            AssertContains(fault, "PfeRemoteBodyRules.ClassifyProjection(",
                "Classify in one pure rule so the ORDER of the checks is testable offline.");
            AssertContains(fault, "_projector.HasStreaming",
                "The FIRST chain input must be the manager's liveness, not a unit counter.");
            AssertContains(fault, "_projector.ActiveRoomCount",
                "A live manager with no active room is a different fix.");
            AssertContains(fault, "_projector.IdentifiedUnitCount",
                "Missing entity ids are a player/NPC stamp defect, not a streaming defect.");
            AssertContains(fault, "_projector.ProjectedUnitCount",
                "Units that existed but reached no delta must be named.");
        }
    }
}
