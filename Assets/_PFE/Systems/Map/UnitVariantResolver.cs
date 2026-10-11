using System.Collections.Generic;
using PFE.Core.Rng;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The oracle's <c>Location</c> context that <c>randomCid()</c> reads and the port has no source for.
    ///
    /// <para>Each field's <i>constructor</i> default is the value that makes its branch <b>not taken</b>,
    /// so a caller with no context gets the conservative arm rather than a silently wrong one.
    /// <see cref="TipEnemy"/>'s default is AS3's own (<c>Location.as:217</c>
    /// <c>public var tipEnemy:int = -1;</c>).</para>
    ///
    /// <para><b><c>default(UnitVariantContext)</c> is NOT "no context", and that is a trap.</b> A struct's
    /// default zeroes every field, and <see cref="LandY"/> <b>0 is a real land index</b> — AS3's first
    /// land — so <c>default</c> makes <c>randomCid("ranger")</c> take the <c>landY == 0</c> arm and return
    /// <c>"1"</c> without drawing, instead of rolling. Use <see cref="UnitVariantResolver.NoContext"/>
    /// at every call site that genuinely has no context; there is a test pinning the difference.</para>
    /// </summary>
    public readonly struct UnitVariantContext
    {
        /// <summary>AS3 <c>Location.biom</c> — the land's theme. See <see cref="UnitVariantResolver.NoBiome"/>.</summary>
        public readonly int Biome;

        /// <summary>AS3 <c>Location.tipEnemy</c> — the room's <c>entip</c> option. <c>-1</c> = none.</summary>
        public readonly int TipEnemy;

        /// <summary>AS3 <c>land.act.conf</c> — the land's config kind. <c>-1</c> = unknown.</summary>
        public readonly int LandConfig;

        /// <summary>AS3 <c>Location.landY</c> — the land's vertical index. <c>-1</c> = unknown.</summary>
        public readonly int LandY;

        public UnitVariantContext(int biome, int tipEnemy = -1, int landConfig = -1, int landY = -1)
        {
            Biome = biome;
            TipEnemy = tipEnemy;
            LandConfig = landConfig;
            LandY = landY;
        }
    }

    /// <summary>
    /// Turns a <i>requested</i> unit id into the id that is actually spawned, by porting the two oracle
    /// steps that sit between <c>Location.createUnit()</c> and <c>Unit.create()</c>:
    ///
    /// <list type="number">
    /// <item><c>Location.randomCid()</c> (<c>Location.as:1728-1958</c>) — picks a <b>variant number</b>
    /// for a family, scaled by the room's difficulty and (sometimes) its biome. Returns a numeric string
    /// such as <c>"0"</c>, or for one family a whole id such as <c>"scorp2"</c>.</item>
    /// <item>The id-composition half of each unit subclass — e.g. <c>UnitZombie.as:104</c>
    /// <c>id = "zombie" + this.tr</c>. That is where the family name and the variant number are joined,
    /// and it is <b>not</b> in <c>Location</c>.</item>
    /// </list>
    ///
    /// <para><b>Why this class exists at all.</b> The port was passing the room's authored id straight
    /// through to <c>UnitDefinition</c> lookup, so a room that places <c>zombie</c> spawned
    /// <c>Resources/Units/zombie.asset</c> — the <b>content-free family template</b>. In the oracle that
    /// row is a <c>&lt;unit id='zombie'&gt;</c> holding only the shared blit table, <c>fraction</c> and
    /// the sound ids; it has no <c>&lt;vis&gt;</c> art, no <c>&lt;comb hp&gt;</c> and no
    /// <c>&lt;move speed&gt;</c>. The variant rows (<c>zombie0..zombie9</c>) carry all of that. So the
    /// port's zombie drew the 1x1 white fallback square and read C# field defaults for its stats — the
    /// bug the player sees as "the zombie has no animation" is this, plus the missing animator driver
    /// (see <c>EnemyBrain.ResolveAnimState</c>).</para>
    ///
    /// <para><b>What is faithfully modelled and what is not.</b>
    /// <see cref="RandomCid"/> is a complete port of the switch: every family, every threshold, and —
    /// because the spawn stream is shared with the rest of room generation — the same <b>number of RNG
    /// draws in the same order</b>, including the draws whose branch is discarded.
    /// <see cref="ComposeId"/> only covers the families whose subclass <c>tr</c>-handling has been read
    /// against the oracle; those are listed in <see cref="CompositionRules"/> with file:line. A family
    /// that is <i>not</i> in that table is returned unchanged, which is correct for the many subclasses
    /// that ignore the cid outright (<c>UnitTrain.as:13</c> <c>id = "training"</c>,
    /// <c>UnitGutsy.as:17</c>, <c>UnitProtect.as:15</c>, <c>UnitNecros.as:15</c>, <c>UnitEqd.as:13</c>,
    /// <c>UnitSentinel.as:18</c>, ...) and is a <b>documented gap</b> for any that does not.</para>
    /// </summary>
    public static class UnitVariantResolver
    {
        /// <summary>
        /// "No biome" sentinel. AS3 biomes are <c>0..10</c> where <c>0</c> means "none", so a negative
        /// value cannot collide with a real biome and makes both the <c>== 5</c> and the <c>&gt;= 1</c>
        /// tests false — which is what an unported biome must do.
        /// </summary>
        public const int NoBiome = -1;

        /// <summary>
        /// "No context at all" — every field sentinelled. <b>Use this, not <c>default</c>.</b>
        /// <c>default(UnitVariantContext)</c> zeroes the fields, and <c>LandY == 0</c> is a real land
        /// index, so it silently picks a branch instead of declining to; see the struct's remarks.
        /// </summary>
        public static readonly UnitVariantContext NoContext = new UnitVariantContext(NoBiome);

        /// <summary>
        /// The variant-number rule for one family: how a cid string becomes a <c>tr</c>, and how
        /// <c>tr</c> becomes the final id. Mirrors the constructor of the family's AS3 subclass.
        /// </summary>
        private delegate string Compose(string cid);

        /// <summary>
        /// Families whose AS3 subclass builds its id as <c>family + tr</c> where
        /// <c>tr = int(cid)</c>. The <c>else</c> arm of each subclass — the one taken when the cid is
        /// null, which randomises <c>tr</c> — is <b>unreachable from <c>Location</c></b> for every entry
        /// here, because <see cref="RandomCid"/> has a <c>case</c> for all of them and so always returns
        /// a non-empty string. It is not modelled, deliberately: an unreachable branch cannot be tested
        /// and would read as working code.
        ///
        /// <para><b>The table must cover every <see cref="RandomCid"/> case.</b> That is an invariant, not
        /// a convention: <see cref="ResolveSpawnId"/> calls <c>RandomCid</c> for <i>every</i> family (the
        /// oracle does — <c>Location.as:1169/1185</c> calls it on both arms of <c>createUnit</c>), and a
        /// family with a <c>RandomCid</c> case but no rule here would return the raw <i>variant number</i>
        /// as if it were a unit id — <c>"2"</c> instead of <c>"pink</c>slime". The original table covered
        /// 9 of the 21 cases, and the gap was invisible because the 12 missing families were either never
        /// placed or resolved to a family-template asset that happens to exist. See
        /// <c>EveryRandomCidCase_HasACompositionRule</c>.</para>
        /// </summary>
        private static readonly Dictionary<string, Compose> CompositionRules =
            new Dictionary<string, Compose>
            {
                // UnitZombie.as:92-104 — `tr = int(param1)`; `if(tr < 0) tr = 0`; `id = "zombie" + tr`.
                ["zombie"] = cid =>
                {
                    int tr = ParseTr(cid);
                    if (tr < 0) tr = 0;
                    return "zombie" + tr;
                },

                // UnitRaider.as:132-150 — `tr = int(param1)`; `if(tr <= 0) tr = 1`;
                // `id = parentId + tr` (parentId is "raider" for this family).
                ["raider"] = cid =>
                {
                    int tr = ParseTr(cid);
                    if (tr <= 0) tr = 1;
                    return "raider" + tr;
                },

                // UnitAlicorn.as:134-144 — `tr = int(param1)`; `if(tr < 0) tr = random(1..3)`.
                // The clamp is modelled as 1 rather than as that roll, because it needs a second RNG
                // draw this signature does not carry; randomCid never returns a negative cid for
                // "alicorn", so the arm is unreachable from ResolveSpawnId anyway.
                ["alicorn"] = cid =>
                {
                    int tr = ParseTr(cid);
                    if (tr < 0) tr = 1;
                    return "alicorn" + tr;
                },

                // UnitAnt.as:43-49 — `tr = int(param1)`; `id = "ant" + tr`. No clamp.
                ["ant"] = cid => "ant" + ParseTr(cid),

                // UnitBloat.as:44-52 — `tr = int(param1)`; `if(tr < 0) tr = 0`.
                ["bloat"] = cid =>
                {
                    int tr = ParseTr(cid);
                    if (tr < 0) tr = 0;
                    return "bloat" + tr;
                },

                // UnitDron.as:47-56 — `tr = int(param1)`; `id = "dron" + tr`; `if(tr == 100) id = "dront"`.
                ["dron"] = cid =>
                {
                    int tr = ParseTr(cid);
                    return tr == 100 ? "dront" : "dron" + tr;
                },

                // UnitFish.as:29-35 — `tr = int(param1)`; `id = "fish" + tr`. No clamp.
                ["fish"] = cid => "fish" + ParseTr(cid),

                // UnitHellhound.as:26-42 — `tr = int(param1)`; `id = "hellhound" + tr`. No clamp.
                ["hellhound"] = cid => "hellhound" + ParseTr(cid),

                // UnitRoller.as:24-45 — `tr = int(param1)`; `if(tr <= 0) tr = 1`;
                // `id = "roller"`, then `if(tr >= 2) id += tr`. So tr==1 yields the bare "roller", which
                // is why the asset directory has "roller" and "roller2" but no "roller1".
                ["roller"] = cid =>
                {
                    int tr = ParseTr(cid);
                    if (tr <= 0) tr = 1;
                    return tr >= 2 ? "roller" + tr : "roller";
                },

                // ── The raider family (UnitRaider.as:119-150) ───────────────────────────────────
                // Every subclass below sets `parentId` and delegates the composition to UnitRaider's
                // constructor, which is one rule for all of them:
                //   `tr = int(cid)`; `if(tr <= 0) tr = 1`; `id = parentId + tr`.
                // `kolTrs` — the *number of variants*, used only by the null-cid roll, which is
                // unreachable from Location — differs per class (9/6/3/1/5/1) and is not modelled here.
                // `raider` itself is above; these are its five siblings.

                // UnitSlaver.as:8-14 — `parentId = "slaver"`, `kolTrs = 6`.
                ["slaver"] = cid => "slaver" + AtLeastOne(ParseTr(cid)),

                // UnitZebra.as:19 — `parentId = "zebra"`, `kolTrs = 5`.
                ["zebra"] = cid => "zebra" + AtLeastOne(ParseTr(cid)),

                // UnitRanger.as:18 — `parentId = "ranger"`, `kolTrs = 3`.
                ["ranger"] = cid => "ranger" + AtLeastOne(ParseTr(cid)),

                // UnitMerc.as:21 — `parentId = "merc"`, `kolTrs = 1`.
                ["merc"] = cid => "merc" + AtLeastOne(ParseTr(cid)),

                // UnitEncl.as:17 — `parentId = "encl"`, `kolTrs = 1`.
                ["encl"] = cid => "encl" + AtLeastOne(ParseTr(cid)),

                // ── Families that ignore the cid and stamp a constant id ────────────────────────
                // Their randomCid case still exists, so the stream must be drawn from; the value it
                // returns is discarded by the subclass. Modelled as constants rather than omitted, so
                // that a future "optimisation" which dropped the draw fails a test instead of silently
                // shifting every later placement in the room.

                // UnitProtect.as:15 — `id = "protect"`. randomCid returns "1"/"0".
                ["protect"] = _ => "protect",

                // UnitGutsy.as:17 — `id = "gutsy"`. randomCid returns "1"/"0".
                ["gutsy"] = _ => "gutsy",

                // ── The slime family ────────────────────────────────────────────────────────────
                // UnitSlime.as:21-51 — `tr = int(cid)`; `if(tr >= 10) { isMine = true; tr -= 10; }`;
                // `tr == 1 -> "cryoslime"`, `tr == 2 -> "pinkslime"`, else `"slime"`. The ids are NOT
                // `family + tr`: the directory has `slime`, `cryoslime` and `pinkslime`, and no
                // `slime0`/`slime1`/`slime2`.
                ["slime"] = SlimeId,

                // `slmine` never appears as a placed id (AllData has no `<obj id='slmine'>`); it exists
                // only as a randomCid product, and Location.createUnit:1171-1174 rewrites the *family*
                // to "slime" before creating. The cid is what carries the mine bit: randomCid("slmine")
                // returns "12"/"10", i.e. tr 12/10 -> isMine, tr -= 10 -> 2/0.
                ["slmine"] = SlimeId,

                // ── The bat family ──────────────────────────────────────────────────────────────
                // UnitBat.as:29-33 — `id = "bloodwing"`; `if(tr >= 2) id += tr`. The same shape as
                // roller, and for the same reason: the directory has `bloodwing` and `bloodwing2` but
                // no `bloodwing1`.
                ["bloodwing"] = cid =>
                {
                    int tr = ParseTr(cid);
                    return tr >= 2 ? "bloodwing" + tr : "bloodwing";
                },

                // ── The one family whose randomCid arm returns a WHOLE id ───────────────────────
                // Location.as:1953-1962 returns `"scorp" + n` (n = 1..2), not a bare number, so the
                // composition step is the identity. UnitMonstrik.as:22-29 does `id = param1`, i.e. it
                // uses the cid verbatim; its own `if(id == "scorp") id += random(1..2)` arm is
                // unreachable from Location.createUnit, which always passes the randomCid result as
                // ncid (Unit.as:858-862) and so hands the constructor "scorp1"/"scorp2", never "scorp".
                //
                // <para><b>This entry was missing, and that was the whole scorpion defect.</b> With no
                // entry the rule gate returned the requested id before RandomCid was consulted, so a
                // room that placed `scorp` spawned the id `scorp` — for which there is no
                // `Resources/Units/scorp.asset`. 45 of the 639 shipped rooms place one.</para>
                ["scorp"] = cid => string.IsNullOrEmpty(cid) ? "scorp" : cid,

                // ── The mine family ─────────────────────────────────────────────────────────────
                // Mine.as:40-95 — `id = cid` when the cid is present and `tr` is 0, which it is for a
                // placed mine (nothing authors `@tr`). randomCid("mine") returns a whole id
                // ("mine"/"hmine"/"plamine"), so composition is the identity — the same shape as scorp.
                // <b>Resolution only:</b> no `Resources/Units/{mine,hmine,plamine}.asset` exists, so a
                // placed mine still has no art; that is an importer-side gap, recorded not fixed here.
                ["mine"] = cid => string.IsNullOrEmpty(cid) ? "mine" : cid,

                // ── Alias families: the placed id is NOT the spawned id ─────────────────────────
                // This is the `Unit.create` half of the oracle (Unit.as:853-862): when randomCid has no
                // case, `cid` falls back to the definition row's `@cid`, and the subclass maps that seed
                // onto its own id. Before this table existed the port returned the placed id verbatim,
                // and each of these resolved to a `Units/<placed-id>.asset` that does not exist — a
                // collider with no sprite, which reads as "the enemy is there but draws nothing".

                // UnitTrigger.as:45-53 — `id = param1` (the `@cid`). AllData rows:
                // trplate->trigplate, trridge->trigridge, trcans->trigcans, trlaser->triglaser.
                ["trplate"] = VerbatimCid,
                ["trridge"] = VerbatimCid,
                ["trcans"] = VerbatimCid,
                ["trlaser"] = VerbatimCid,

                // UnitDamager.as:42-52 — `id = param1` when non-null, else `"damshot"`.
                // AllData: expl1->damexpl1, damgren->damgren, damshot->damshot.
                ["expl1"] = VerbatimCid,
                ["damgren"] = VerbatimCid,
                ["damshot"] = cid => string.IsNullOrEmpty(cid) ? "damshot" : cid,

                // UnitTurret.as:56-95 — the cid picks a *mount kind* and the id is `"turret" + turrettip`
                // (0 floor, 1 land, 2 wall, 3 arm). AllData cids: turret->floor(0), landturret->land(1),
                // wturret->wall(2), armturret->arm(3), hturret->hidden(0), hturret2->hidden2(0),
                // cturret->combat(0), bossturret->boss(0).
                ["turret"] = TurretId,
                ["landturret"] = TurretId,
                ["wturret"] = TurretId,
                ["armturret"] = TurretId,
                ["hturret"] = TurretId,
                ["hturret2"] = TurretId,
                ["cturret"] = TurretId,
                ["bossturret"] = TurretId,

                // ── Fixed-id classes: the placed id and the spawned id have different roots ──────
                // No cid is involved; the subclass simply stamps its own id.

                // UnitTrap.as:6 — `id = "mtrap"` (the placed row is `trap`).
                ["trap"] = _ => "mtrap",

                // UnitTransmitter.as:4 — `id = "transmitter"` (the placed row is `transm`).
                ["transm"] = _ => "transmitter",

                // UnitPonPon.as:4 — `id = "ponpon"` (the placed rows are `zebpon` and `stabpon`).
                ["zebpon"] = _ => "ponpon",
                ["stabpon"] = _ => "ponpon",

                // UnitBossUltra.as:4 / UnitBossDron.as:4 — placed rows `ultra` / `megadron`.
                ["ultra"] = _ => "bossultra",
                ["megadron"] = _ => "bossdron"
            };

        /// <summary>UnitRaider.as:146-148 — `if(tr &lt;= 0) tr = 1`.</summary>
        static int AtLeastOne(int tr) => tr <= 0 ? 1 : tr;

        /// <summary>
        /// UnitSlime.as:21-51 — the mine bit is the tens digit, then 1/2 select a themed slime.
        /// </summary>
        static string SlimeId(string cid)
        {
            int tr = ParseTr(cid);
            if (tr >= 10)
            {
                tr -= 10;
            }

            if (tr == 1)
            {
                return "cryoslime";
            }

            if (tr == 2)
            {
                return "pinkslime";
            }

            return "slime";
        }

        /// <summary>
        /// UnitTrigger.as:45-53 / UnitDamager.as:42-52 — `id = param1`, i.e. the definition's own
        /// <c>@cid</c>. Returns null for an absent cid so <see cref="ResolveSpawnId"/> can fall back to
        /// the requested id rather than inventing one.
        /// </summary>
        static string VerbatimCid(string cid) => string.IsNullOrEmpty(cid) ? null : cid;

        /// <summary>
        /// UnitTurret.as:56-95 — the cid names a mount, and the id is <c>"turret" + turrettip</c>.
        /// An unknown cid is the floor turret, exactly as the oracle's fall-through is.
        /// </summary>
        static string TurretId(string cid)
        {
            int tip = 0;
            if (cid == "land")
            {
                tip = 1;
            }
            else if (cid == "wall")
            {
                tip = 2;
            }
            else if (cid == "arm")
            {
                tip = 3;
            }

            return "turret" + tip;
        }

        /// <summary>
        /// The id to spawn for a requested one: <see cref="RandomCid"/> first, then the family's
        /// composition rule.
        ///
        /// <para><b>Argument order matches the oracle's precedence.</b> <c>UnitZombie.as:83-101</c> tests
        /// the <b>saved-object</b> <c>tr</c> first (<c>param4.tr</c>), then the <b>placement</b> node's
        /// <c>tr</c> (<c>param3.@tr</c>), and only then <c>int(cid)</c>. The port has no saved-object layer
        /// yet, so <paramref name="placementTr"/> is the only override — and it must win over the rolled
        /// cid, which is why it is tested first here.</para>
        /// </summary>
        /// <param name="requestedId">The id authored on the room's placement row.</param>
        /// <param name="difficulty">AS3 <c>Location.locDifLevel</c>, an integer level.</param>
        /// <param name="rng">The spawn stream. Null falls back to a fresh unseeded stream, which is legal
        /// for a bare test but makes the roll non-reproducible.</param>
        /// <param name="context">Biome / tipEnemy / land context, all sentinelled. See
        /// <see cref="UnitVariantContext"/>.</param>
        /// <param name="placementTr">The placement node's <c>tr</c> attribute, or null/empty for absent.
        /// AS3 tests <c>.length()</c>, i.e. presence, so empty means absent.</param>
        /// <param name="definitionCid">The definition row's <c>@cid</c>, or null/empty for absent. This is
        /// the <i>seed</i> the subclass maps onto its own id for the alias families
        /// (<c>trplate</c>→<c>trigplate</c>, <c>turret</c>→<c>turret0</c>, …). It is <b>not</b> the placed
        /// id: a room places <c>trplate</c>, the definition row says <c>cid='trigplate'</c>, and the
        /// spawned id is <c>trigplate</c>.</param>
        public static string ResolveSpawnId(
            string requestedId,
            float difficulty,
            IRngService rng,
            UnitVariantContext context = default,
            string placementTr = null,
            string definitionCid = null)
        {
            if (string.IsNullOrEmpty(requestedId))
            {
                return requestedId;
            }

            // Unit.as:853-862 — `cid = null; if(node.@cid.length()) cid = node.@cid; if(ncid != null)
            // cid = ncid;`. `node` is the DEFINITION row, so the seed is the definition's own @cid…
            string cid = definitionCid;

            // …and ncid overrides it. Location.createUnit's placement arm (Location.as:1021) passes no
            // ncid, so on that arm ncid IS the randomCid() result, which the oracle draws
            // unconditionally (Location.as:1169/1185). The draw is part of the shared spawn stream and
            // must be taken even when the placement's @tr below discards the value — skipping it shifts
            // every later placement in the room. 472 of the shipped placements author @tr, so this is a
            // live desync, not a theoretical one.
            string rolled = RandomCid(requestedId, difficulty, rng, context);
            if (!string.IsNullOrEmpty(rolled))
            {
                cid = rolled;
            }

            // UnitZombie.as:83-101 — the subclass's tr precedence is saved-object tr, then the placement
            // node's @tr (param3.@tr), then int(cid). The port has no saved-object layer, so the
            // placement's @tr is the only override, and it outranks the cid.
            string selector = string.IsNullOrEmpty(placementTr) ? cid : placementTr;

            if (!CompositionRules.TryGetValue(requestedId, out Compose compose))
            {
                // Documented gap: no rule to turn the selector into an id (see CompositionRules'
                // remarks). The draw above has still been taken, so the stream stays in step.
                return requestedId;
            }

            // A rule may decline (VerbatimCid with an absent cid). Falling back to the requested id
            // keeps the caller on the pre-fix behaviour instead of handing a null to a dictionary
            // lookup — a silent "the unit exists but has no definition" rather than a crash.
            string composed = compose(selector);
            return string.IsNullOrEmpty(composed) ? requestedId : composed;
        }

        /// <summary>
        /// Port of <c>Location.randomCid()</c> (<c>Location.as:1728-1958</c>). Returns the variant
        /// selector for <paramref name="family"/>, or <c>null</c> for a family the oracle's switch does
        /// not list (its <c>default: return null;</c>).
        ///
        /// <para><b>Draw count is part of the contract.</b> The stream is shared with the rest of room
        /// generation, so a branch that skips a draw the oracle takes desynchronises every later
        /// placement. Two consequences are load-bearing here: the comparisons use
        /// <c>rng.NextFloat()</c> directly rather than <c>rng.Chance(p)</c>, whose <c>p &lt;= 0</c> /
        /// <c>p &gt;= 1</c> early-outs would skip a draw the oracle takes; and the
        /// <c>biome == 2 &amp;&amp; …</c> arm relies on C#'s <c>&amp;&amp;</c> short-circuiting exactly as
        /// AS3's does.</para>
        /// </summary>
        public static string RandomCid(
            string family,
            float difficulty,
            IRngService rng,
            UnitVariantContext context = default)
        {
            int d = (int)difficulty;
            int biome = context.Biome;
            int tipEnemy = context.TipEnemy;
            rng ??= new PcgRngService();

            switch (family)
            {
                // Math.floor(random * n + base) == Range(base, base + n).
                case "raider":
                    if (d >= 5) return rng.Range(1, 10).ToString();
                    if (d >= 2) return rng.Range(1, 6).ToString();
                    return rng.Range(1, 3).ToString();

                case "slaver":
                    if (d >= 18) return rng.Range(1, 7).ToString();
                    if (d >= 15) return rng.Range(1, 6).ToString();
                    return rng.Range(1, 5).ToString();

                case "zebra":
                {
                    int n = d >= 15 ? rng.Range(1, 5) : rng.Range(1, 3);
                    // A SECOND draw, taken only at difficulty >= 25.
                    if (d >= 25 && rng.NextFloat() < 0.1f) n = 5;
                    return n.ToString();
                }

                case "ranger":
                    if (context.LandConfig == 7) return rng.Range(1, 4).ToString();
                    if (context.LandY == 0) return "1";
                    return rng.Range(1, 3).ToString();

                case "merc":
                    if (d >= 19) return rng.Range(1, 6).ToString();
                    // `Math.random() > 0.5` — a strict greater-than, and always a draw.
                    if (d >= 15 && rng.NextFloat() > 0.5f) return rng.Range(1, 5).ToString();
                    return rng.Range(1, 3).ToString();

                case "encl":
                    return rng.Range(1, 5).ToString();

                case "protect":
                    return tipEnemy == 7 ? "1" : "0";

                case "gutsy":
                    return tipEnemy == 7 ? "1" : "0";

                case "dron":
                {
                    if (tipEnemy == 9)
                    {
                        int n = rng.Range(1, 5);
                        if (n > 3) n = 3;
                        return n.ToString();
                    }
                    return rng.Range(1, 3).ToString();
                }

                case "roller":
                    return biome == 6 ? "2" : "1";

                case "zombie":
                {
                    int n;
                    if (biome == 5)
                    {
                        if (d >= 20 && rng.NextFloat() < 0.1f) n = 9;
                        else n = rng.Range(5, 9);
                    }
                    else if (biome >= 1 && d >= 8) n = rng.Range(0, 7);
                    else if (d >= 5) n = rng.Range(0, 5);
                    else if (d >= 2) n = rng.Range(0, 4);
                    else n = 0;
                    return n.ToString();
                }

                case "alicorn":
                    return rng.Range(1, 4).ToString();

                case "hellhound":
                    return "1";

                case "bloat":
                {
                    int n;
                    if (biome == 5) n = rng.Range(4, 7);
                    else if (d >= 10) n = rng.Range(0, 5);
                    else if (d >= 4) n = rng.Range(0, 4);
                    else if (d >= 2) n = rng.Range(0, 3);
                    else n = 0;
                    return n.ToString();
                }

                case "ant":
                {
                    int n;
                    if (biome >= 1 && d >= 6) n = rng.Range(1, 4);
                    else if (d >= 3) n = rng.Range(1, 3);
                    else n = 1;
                    return n.ToString();
                }

                case "fish":
                    return biome == 5 ? "3" : rng.Range(1, 3).ToString();

                case "slime":
                    return biome == 5 ? "2" : "0";

                case "slmine":
                    return biome == 5 ? "12" : "10";

                case "bloodwing":
                    return biome == 5 ? "2" : "1";

                case "scorp":
                {
                    int n = d >= 5 ? rng.Range(1, 3) : 1;
                    // The one family that returns a WHOLE id rather than a bare number — see the class
                    // note. It has no entry in CompositionRules for exactly that reason.
                    return "scorp" + n;
                }

                case "mine":
                    if (biome == 4) return "plamine";
                    if (biome == 2 && rng.NextFloat() < Min(d / 20f, 0.4f)) return "plamine";
                    if (rng.NextFloat() < Min(d / 20f, 0.75f)) return "mine";
                    return "hmine";

                default:
                    return null;
            }
        }

        /// <summary>
        /// AS3 <c>int(cid)</c>: a leading numeric run is parsed, anything else is <c>0</c>, and a
        /// null/empty cid is <c>0</c>. <c>int("2abc")</c> is <c>2</c> in AS3, which
        /// <see cref="int.TryParse(string, out int)"/> would reject, so the digits are taken explicitly.
        /// </summary>
        private static int ParseTr(string cid)
        {
            if (string.IsNullOrEmpty(cid))
            {
                return 0;
            }

            int i = 0;
            if (cid[0] == '+' || cid[0] == '-')
            {
                i = 1;
            }

            int start = i;
            while (i < cid.Length && cid[i] >= '0' && cid[i] <= '9')
            {
                i++;
            }

            if (i == start)
            {
                return 0;
            }

            return int.TryParse(cid.Substring(0, i), out int value) ? value : 0;
        }

        private static float Min(float a, float b) => a < b ? a : b;
    }
}
