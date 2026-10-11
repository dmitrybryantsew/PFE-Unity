using System;
using System.Collections.Generic;
using PFE.Core.Rng;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// A faithful port of <c>LootGen</c>'s roll machinery — <c>getRandom</c> (<c>LootGen.as:117-168</c>),
    /// <c>newLoot</c> (<c>:170-297</c>), and the two table dispatchers <c>lootCont</c> (<c>:312-781</c>)
    /// and <c>lootDrop</c> (<c>:783-1069</c>).
    ///
    /// <para><b>Pure.</b> No Unity types, no statics. Everything the oracle kept in
    /// <c>private static loc/nx/ny/is_loot/lootBroken</c> (<c>:14-22</c>) is passed in and returned. Those
    /// statics are a re-entrancy bug rather than a design — a nested roll overwrites the outer roll's
    /// state — so the port does not reproduce them; it passes a <see cref="LootContext"/> instead.</para>
    ///
    /// <para><b>Tables are code, not data.</b> Each branch of <c>lootCont</c>/<c>lootDrop</c> is an
    /// <c>if/else if</c> arm containing <c>while</c> loops whose trip count is a fresh random draw,
    /// perk gates (<c>pers.freel</c>, <c>pers.barahlo</c>), room gates (<c>land.act.biom</c>,
    /// <c>itemsTip</c>, <c>land.act.id</c>), hero gates, either/or arms
    /// (<c>if(!newLoot(a)) newLoot(b)</c>), critter spawns and <c>replic</c> calls. A serialised entry
    /// list cannot hold any of that without inventing a DSL, so the transcription is C# — one method per
    /// key, each carrying its oracle line range. The <i>data</i> half of the system is the pool set
    /// (<see cref="LootPoolSet"/>), which genuinely is derived data.</para>
    ///
    /// <para><b>Completeness is asserted, not assumed.</b> Every one of the 29 container keys and 47 unit
    /// keys is transcribed; <see cref="IsKnownContainerKey"/> / <see cref="IsKnownUnitKey"/> let a test
    /// prove that, and an unknown key is recorded in <see cref="LootContext.UnmatchedKeys"/> rather than
    /// silently producing nothing — which is exactly how <c>cont="*"</c> stayed broken for years (Q1).</para>
    /// </summary>
    public static class LootRoller
    {
        /// <summary>AS3 <c>getRandom</c>'s <c>param2</c> default — "no level filter" (<c>LootGen.as:117</c>).</summary>
        public const float NoLevelFilter = -100f;

        /// <summary>AS3 <c>getRandom</c>'s <c>param3</c> default — "no worth filter" (<c>LootGen.as:117</c>).</summary>
        public const int NoWorthFilter = -100;

        /// <summary>AS3 <c>LootGen.isrnd()</c>'s default probability (<c>LootGen.as:1079</c>).</summary>
        public const float ReplicGate = 0.5f;

        /// <summary>Prefix used when a roll is recorded as having failed to resolve a pool entry
        /// (AS3 traces <c>"Ошибка при генерации лута тип:"</c> and returns false, <c>LootGen.as:222-226</c>).</summary>
        public const string UnresolvedPrefix = "unresolved:";

        // ── per-call state, replacing the oracle's private statics (LootGen.as:14-22) ──────────────────

        private sealed class RollState
        {
            public LootContext Ctx;
            public IRngService Rng;
            public float Nx;
            public float Ny;
            public bool LootBroken;
            public int IsLoot;
            public string TableKey;
        }

        // ── public entry points ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>LootGen.lootCont(loc, nx, ny, cont, lootBroken, allDif)</c> (<c>LootGen.as:312</c>).
        /// Returns AS3's <c>is_loot &gt; 0</c> — whether any <b>item</b> was produced. Note a container
        /// that only spawns a critter (<c>bloat</c>) therefore reports <b>false</b> and shows "itsEmpty".
        /// </summary>
        /// <param name="contKey">The resolved <c>cont</c> string. <c>"*"</c> is not a table — see
        /// <see cref="ContainerRule.HasLootTable"/>.</param>
        public static bool RollContainer(string contKey, LootContext ctx, float nx = 0f, float ny = 0f)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var s = Begin(ctx, nx, ny, ctx.LootBroken, contKey);

            switch (contKey)
            {
                case "ammo": ContAmmo(s); break;
                case "metal": ContMetal(s); break;
                case "bomb": ContBomb(s); break;
                case "expl": ContExpl(s); break;
                case "bigexpl": ContBigExpl(s); break;
                case "wbattle": ContWBattle(s); break;
                case "case": ContCase(s); break;
                case "wbig": ContWBig(s); break;
                case "robocell": ContRobocell(s); break;
                case "instr": ContInstr(s); break;
                case "instr2": ContInstr2(s); break;
                case "trash": ContTrash(s); break;
                case "fridge": ContFridge(s); break;
                case "food": ContFood(s); break;
                case "med": ContMed(s); break;
                case "med2": ContMed2(s); break;
                case "table": ContTable(s); break;
                case "filecab": ContFilecab(s); break;
                case "cup": ContCup(s); break;
                case "bloat": ContBloat(s); break;
                case "book": ContBook(s); break;
                case "term":
                case "info": ContTerm(s); break;
                case "cryo": ContCryo(s); break;
                case "chest": ContChest(s); break;
                case "safe": ContSafe(s); break;
                case "specweap": ContSpecWeap(s); break;
                case "specalc": ContSpecAlc(s); break;
                case "speclp": ContSpecLp(s); break;
                default:
                    ctx.UnmatchedKeys.Add(contKey ?? string.Empty);
                    break;
            }

            return s.IsLoot > 0;
        }

        /// <summary>
        /// AS3 <c>LootGen.lootDrop(loc, nx, ny, cont, hero)</c> (<c>LootGen.as:783</c>). Note it forces
        /// <c>lootBroken = false</c> (<c>:789</c>) — a corpse cannot be "broken open".
        /// </summary>
        public static bool RollUnitDrop(string dropKey, LootContext ctx, float nx = 0f, float ny = 0f)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var s = Begin(ctx, nx, ny, false, dropKey);

            switch (dropKey)
            {
                case "scorp": DropScorp(s); break;
                case "slime": DropSlime(s); break;
                case "pinkslime": DropPinkSlime(s); break;
                case "raider": DropRaider(s); break;
                case "alicorn1":
                case "alicorn2":
                case "alicorn3": DropAlicorn(s); break;
                case "ranger1":
                case "ranger2":
                case "ranger3": DropRanger(s); break;
                case "encl2":
                case "encl3":
                case "encl4": DropEnclave(s); break;
                case "hellhound1": DropHellhound(s); break;
                case "zombie": DropZombie(s); break;
                case "zombie4": DropZombie4(s); break;
                case "zombie5": DropZombie5(s); break;
                case "zombie6": DropZombie6(s); break;
                case "zombie7": DropZombie7(s); break;
                case "zombie8": DropZombie8(s); break;
                case "zombie9": DropZombie9(s); break;
                case "bloodwing": DropBloodwing(s); break;
                case "bloodwing2": DropBloodwing2(s); break;
                case "bloat0": DropBloat0(s); break;
                case "bloat1": DropBloat1(s); break;
                case "bloat2": DropBloat2(s); break;
                case "bloat3": DropBloat3(s); break;
                case "bloat4": DropBloat4(s); break;
                case "rat": DropRat(s); break;
                case "molerat": DropMoleRat(s); break;
                case "fish1": DropFish1(s); break;
                case "fish2": DropFish2(s); break;
                case "ant1": DropAnt1(s); break;
                case "ant2": DropAnt2(s); break;
                case "ant3": DropAnt3(s); break;
                case "necros": DropNecros(s); break;
                case "ebloat": DropEBloat(s); break;
                case "turret": DropTurret(s); break;
                case "turret1": DropTurret1(s); break;
                case "robobrain": DropRobobrain(s); break;
                case "protect": DropProtect(s); break;
                case "gutsy": DropGutsy(s); break;
                case "eqd": DropEqd(s); break;
                case "sentinel": DropSentinel(s); break;
                case "vortex":
                case "spritebot":
                case "roller": DropScrapOnly(s); break;
                default:
                    ctx.UnmatchedKeys.Add(dropKey ?? string.Empty);
                    break;
            }

            return s.IsLoot > 0;
        }

        /// <summary>
        /// AS3 <c>LootGen.lootId(loc, nx, ny, id, kol, imp, cont, lootBroken)</c> (<c>LootGen.as:299</c>) —
        /// one placed item, by explicit id. <c>kol</c> is passed straight through, so a caller that
        /// supplies it bypasses the row's own <c>@kol</c>.
        /// </summary>
        public static bool RollPlacedItem(string itemId, LootContext ctx, int kol = -1, int imp = 0,
                                         float nx = 0f, float ny = 0f)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var s = Begin(ctx, nx, ny, ctx.LootBroken, "placed:" + itemId);
            // LootGen.as:309 — tip is "" so Item runs itemTip() and resolves the tip from the id.
            return NewLoot(s, 1f, string.Empty, itemId, kol, imp);
        }

        /// <summary>
        /// The first half of <c>Interact.loot()</c> (<c>Interact.as:1973-2000</c>): a container's own
        /// <c>&lt;item&gt;</c> children, rolled <b>before</b> the table and with <c>imp</c> forced to 1
        /// (or 2 for an item carrying an <c>imp</c> attribute) — which is what exempts them from the
        /// per-run limit check (<c>LootGen.as:261</c>).
        /// </summary>
        public static PlacedItemsResult RollPlacedItems(IReadOnlyList<PlacedLootItem> items, LootContext ctx,
                                                       float nx = 0f, float ny = 0f)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (items == null || items.Count == 0) return new PlacedItemsResult(false, false);

            var s = Begin(ctx, nx, ny, ctx.LootBroken, "placed");
            bool anyPlaced = false;
            bool anyImportant = false;
            for (int i = 0; i < items.Count; i++)
            {
                var placed = items[i];
                int kol = placed.Kol ?? 1;              // Interact.as:1979-1986
                int imp = placed.Important ? 2 : 1;     // Interact.as:1987-1995
                if (imp == 2) anyImportant = true;

                NewLoot(s, 1f, string.Empty, placed.Id, kol, imp);
                // Interact.as:1997 sets the flag per item regardless of whether the roll succeeded.
                anyPlaced = true;
            }
            return new PlacedItemsResult(anyPlaced, anyImportant);
        }

        /// <summary>One <c>&lt;item&gt;</c> child on a container's placement XML (<c>Interact.as:1975</c>).</summary>
        public readonly struct PlacedLootItem
        {
            public readonly string Id;
            /// <summary>AS3 <c>@kol</c>. <c>null</c> = absent, which the caller maps to 1.</summary>
            public readonly int? Kol;
            /// <summary>AS3 the presence of an <c>@imp</c> attribute.</summary>
            public readonly bool Important;

            public PlacedLootItem(string id, int? kol = null, bool important = false)
            {
                Id = id;
                Kol = kol;
                Important = important;
            }
        }

        /// <summary>What <see cref="RollPlacedItems"/> found. <c>AnyPlaced</c> mirrors
        /// <c>_loc4_</c> and <c>AnyImportant</c> mirrors <c>_loc5_ == 2</c> (<c>Interact.as:1997, 2024</c>).</summary>
        public readonly struct PlacedItemsResult
        {
            public readonly bool AnyPlaced;
            public readonly bool AnyImportant;

            public PlacedItemsResult(bool anyPlaced, bool anyImportant)
            {
                AnyPlaced = anyPlaced;
                AnyImportant = anyImportant;
            }
        }

        // ── key tables ────────────────────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> ContainerKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "ammo", "metal", "bomb", "expl", "bigexpl", "wbattle", "case", "wbig", "robocell", "instr",
            "instr2", "trash", "fridge", "food", "med", "med2", "table", "filecab", "cup", "bloat", "book",
            "term", "info", "cryo", "chest", "safe", "specweap", "specalc", "speclp",
        };

        private static readonly HashSet<string> UnitKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "scorp", "slime", "pinkslime", "raider", "alicorn1", "alicorn2", "alicorn3", "ranger1",
            "ranger2", "ranger3", "encl2", "encl3", "encl4", "hellhound1", "zombie", "zombie4", "zombie5",
            "zombie6", "zombie7", "zombie8", "zombie9", "bloodwing", "bloodwing2", "bloat0", "bloat1",
            "bloat2", "bloat3", "bloat4", "rat", "molerat", "fish1", "fish2", "ant1", "ant2", "ant3",
            "necros", "ebloat", "turret", "turret1", "robobrain", "protect", "gutsy", "eqd", "sentinel",
            "vortex", "spritebot", "roller",
        };

        /// <summary>29 keys — the 28 <c>else if</c> arms of <c>lootCont</c> with <c>term</c>/<c>info</c>
        /// counted separately (<c>LootGen.as:327-779</c>).</summary>
        public static IReadOnlyCollection<string> KnownContainerKeys => ContainerKeys;

        /// <summary>47 keys — the 39 <c>else if</c> arms of <c>lootDrop</c> with the four aliased arms
        /// expanded (<c>LootGen.as:794-1067</c>).</summary>
        public static IReadOnlyCollection<string> KnownUnitKeys => UnitKeys;

        public static bool IsKnownContainerKey(string key) => key != null && ContainerKeys.Contains(key);
        public static bool IsKnownUnitKey(string key) => key != null && UnitKeys.Contains(key);

        // ── state plumbing ────────────────────────────────────────────────────────────────────────────

        private static RollState Begin(LootContext ctx, float nx, float ny, bool lootBroken, string tableKey)
        {
            if (ctx.Rng == null) throw new InvalidOperationException("LootContext.Rng is required.");
            if (ctx.Pools == null) throw new InvalidOperationException("LootContext.Pools is required.");

            ctx.TableKey = tableKey ?? string.Empty;
            if (ctx.Catalog == null) ctx.UnmatchedKeys.Add("no-catalog:" + ctx.TableKey);

            return new RollState
            {
                Ctx = ctx,
                Rng = ctx.Rng,
                Nx = nx,
                Ny = ny,
                LootBroken = lootBroken,
                IsLoot = 0,
                TableKey = ctx.TableKey,
            };
        }

        // ── LootGen.getRandom (LootGen.as:117-168) ────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>LootGen.getRandom(tip, lvlGate, worthGate)</c> — <b>public in the oracle</b>, with real
        /// external callers (<c>Vendor.as:155</c> picks a weapon by level, <c>:225</c> picks by tip), so
        /// it is public here rather than being a private helper.
        ///
        /// <para>The filter is applied only when <c>tip != "book"</c> <b>and</b> at least one of the three
        /// gates is active (<c>:133</c>). Inside it, each gate <b>passes an entry whose attribute is
        /// absent</b> (<c>:138</c>) — an unstaged item is not "stage 0".</para>
        ///
        /// <para>Two details a naive port gets wrong: the <c>length == 1</c> short-circuit
        /// (<c>:155-158</c>) returns <b>without drawing a random number</b>, and a pool whose weights sum
        /// to zero returns <c>null</c> rather than an item (<c>:159-167</c>) — which is the entire
        /// behaviour of the <c>magic</c> pool.</para>
        /// </summary>
        public static string GetRandom(LootContext ctx, string tip, float levelGate = NoLevelFilter,
                                       int worthGate = NoWorthFilter)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (ctx.Rng == null) throw new InvalidOperationException("LootContext.Rng is required.");
            if (ctx.Pools == null) throw new InvalidOperationException("LootContext.Pools is required.");
            return GetRandomCore(ctx, ctx.Rng, tip, levelGate, worthGate);
        }

        private static string GetRandom(RollState s, string tip, float levelGate = NoLevelFilter,
                                        int worthGate = NoWorthFilter)
            => GetRandomCore(s.Ctx, s.Rng, tip, levelGate, worthGate);

        private static string GetRandomCore(LootContext ctx, IRngService rng, string tip, float levelGate,
                                            int worthGate)
        {
            var pool = ctx.Pools.GetPool(tip);
            if (pool == null) return null;

            int gameStage = ctx.GameStage;

            List<LootPoolEntry> candidates;
            if (tip != LootTips.Book && (levelGate > 0f || worthGate > 0 || gameStage > 0))
            {
                candidates = new List<LootPoolEntry>();
                for (int i = 0; i < pool.Count; i++)
                {
                    var e = pool[i];
                    bool stageOk = gameStage <= 0 || !e.Stage.HasValue || e.Stage.Value <= gameStage;
                    bool levelOk = levelGate == NoLevelFilter || !e.Level.HasValue || e.Level.Value <= levelGate;
                    bool worthOk = worthGate == NoWorthFilter || !e.Worth.HasValue || worthGate == e.Worth.Value;
                    if (stageOk && levelOk && worthOk) candidates.Add(e);
                }
            }
            else
            {
                candidates = pool as List<LootPoolEntry> ?? new List<LootPoolEntry>(pool);
            }

            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0].Id;

            float total = 0f;
            for (int i = 0; i < candidates.Count; i++) total += candidates[i].Chance;

            float roll = rng.NextFloat() * total;
            float running = 0f;
            for (int i = 0; i < candidates.Count; i++)
            {
                running += candidates[i].Chance;
                if (running > roll) return candidates[i].Id;
            }
            return null;
        }

        // ── LootGen.newLoot (LootGen.as:170-297) ──────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>LootGen.newLoot(chance, tip, id, kol, imp, cont)</c>. Returns whether an item was
        /// produced; on success appends one <see cref="LootRoll"/> and increments the call's item count.
        ///
        /// <para><c>param6</c> (the owning <c>Interact</c>) is not modelled: all 267 <c>newLoot</c> call
        /// sites in the two tables use the default, so no roll ever attaches a container to its own
        /// drop.</para>
        /// </summary>
        private static bool NewLoot(RollState s, float chance, string tip, string id = null, int kol = -1,
                                    int imp = 0)
        {
            // :175-178 — the chance gate. Note the RNG is drawn only when chance < 1.
            if (chance < 1f && s.Rng.NextFloat() > chance) return false;

            float hp = s.LootBroken ? 0.4f : 1f;                        // :179-183

            // :184-206 — weapons resolve their id through the pool, with `param3` acting as a WORTH
            // filter (not an id), and each failed attempt halves the condition.
            if (tip == LootTips.Weapon)
            {
                if (ParseInt(id) > 0)
                {
                    id = GetRandom(s, tip, Math.Max(1f, s.Ctx.WeaponLevel + (s.Rng.NextFloat() * 2f - 1f)),
                                   ParseInt(id));
                    if (id == null) hp *= 0.5f;
                }
                if (id == null)
                {
                    id = GetRandom(s, tip, Math.Max(1f, s.Ctx.WeaponLevel + (s.Rng.NextFloat() * 2f - 1f)));
                    if (id == null) hp *= 0.5f;
                }
                if (id == null) id = GetRandom(s, tip);
            }

            // :207-221 — everything else, including a weapon that still has no id.
            if (id == null || id == string.Empty)
            {
                if (tip == LootTips.Expl || tip == LootTips.Uniq)
                    id = GetRandom(s, tip, Math.Max(1f, s.Ctx.WeaponLevel + (s.Rng.NextFloat() * 2f - 1f)));
                else
                    id = GetRandom(s, tip, Math.Max(1f, s.Ctx.LocDifLevel / 2f + (s.Rng.NextFloat() * 2f - 1f)));

                if (id == null) id = GetRandom(s, tip);
            }

            // :222-226 — the oracle traces and gives up.
            if (id == null)
            {
                s.Ctx.UnmatchedKeys.Add(UnresolvedPrefix + tip);
                return false;
            }

            if (kol == -1 && tip == LootTips.Uniq) kol = 1;             // :227-230

            var item = BuildItem(s, tip, id, kol);                      // :231 — new Item(tip, id, kol)

            // :232-241 — the two tip2 keys are rewritten to a real tip after construction.
            if (tip == LootTips.Tip2Eda) item.Tip = LootTips.Food;
            if (tip == LootTips.Tip2Co) item.Tip = LootTips.Scheme;

            item.HpMultiplier = hp;                                     // :242
            item.Important = imp != 0;                                  // :243

            // :245-256 — perk multipliers, then the broken-container penalty. Both truncate, because
            // Item.kol is an int.
            if (item.Id == "money") item.Quantity = (int)(item.Quantity * s.Ctx.CapsMult * s.Ctx.DifCapsMult);
            if (item.Id == "bit") item.Quantity = (int)(item.Quantity * s.Ctx.BitsMult * s.Ctx.DifCapsMult);
            if (s.LootBroken && (item.Id == "money" || item.Id == "bit"))
                item.Quantity = (int)(item.Quantity * 0.5f);

            // :257-260 — a broken container loses half its ammo and explosives outright.
            if (s.LootBroken && (item.Tip == LootTips.Ammo || item.Tip == LootTips.Expl)
                && s.Rng.NextFloat() < 0.5f)
                return false;

            // :261-286 — the per-run limit, skipped only when imp != 0 (never, for a table roll).
            if (imp == 0 && item.Row.HasValue && !string.IsNullOrEmpty(item.Row.Value.LimitKey))
            {
                string limitKey = item.Row.Value.LimitKey;
                int have = s.Ctx.Limits != null ? s.Ctx.Limits.GetLimit(limitKey) : 0;
                float cap = s.Ctx.LootLimit;
                if (item.Row.Value.LimitMultiplier.HasValue) cap *= item.Row.Value.LimitMultiplier.Value;

                if (item.Row.Value.LimitAbsolute.HasValue && have >= item.Row.Value.LimitAbsolute.Value)
                {
                    s.Ctx.UnmatchedKeys.Add("maxlim:" + limitKey);
                    return false;
                }
                if (have >= cap)
                {
                    s.Ctx.UnmatchedKeys.Add("limit:" + limitKey);
                    return false;
                }
                s.Ctx.Limits?.AddLimit(limitKey, 1);
            }

            // :287-294 — the sink. AS3 either stuffs the inventory (test mode) or drops a Loot object.
            s.Ctx.Results.Add(new LootRoll(item.Tip, item.Id, item.Variant, item.Quantity, item.Sost,
                                           item.Important, item.HpMultiplier, s.TableKey));
            s.IsLoot++;
            return true;
        }

        /// <summary>
        /// AS3 <c>int(x)</c>: truncate toward zero, and <b>0 for anything non-numeric</b> — including
        /// <c>null</c>, <c>""</c> and an id like <c>"lsword^1"</c>. The distinction matters because
        /// <c>newLoot</c> treats a weapon's id as a worth filter only when <c>int(id) &gt; 0</c>
        /// (<c>LootGen.as:186</c>).
        /// </summary>
        private static int ParseInt(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double d)
                ? (int)d
                : 0;
        }

        // ── Item construction, reduced to what the roll needs (Item.as:121-312) ───────────────────────

        private struct ResolvedItem
        {
            public string Id;
            public string Tip;
            public int Variant;
            public int Quantity;
            public float Sost;
            public float HpMultiplier;
            public bool Important;
            public LootItemRow? Row;
        }

        private static ResolvedItem BuildItem(RollState s, string tip, string rawId, int kolParam)
        {
            var item = new ResolvedItem { Id = rawId, Tip = tip, Variant = 0, Quantity = kolParam, Sost = 1f };

            // Item.as:132-140 — a "^N" suffix means variant N and is stripped from the id.
            if (!string.IsNullOrEmpty(item.Id) && item.Id.Length >= 2 && item.Id[item.Id.Length - 2] == '^')
            {
                item.Variant = ParseInt(item.Id.Substring(item.Id.Length - 1));
                item.Id = item.Id.Substring(0, item.Id.Length - 2);
            }

            // Item.as:147-150 — uniq is a weapon, and the lookup must use the mapped tip.
            if (item.Tip == LootTips.Uniq) item.Tip = LootTips.Weapon;

            var catalog = s.Ctx.Catalog;
            if (catalog != null)
            {
                if (string.IsNullOrEmpty(item.Tip))
                {
                    // Item.as:143-146 + :314-348 — itemTip(): item, then weapon, then armour.
                    item.Row = catalog.FindByAnyId(item.Id, out string resolvedTip);
                    if (item.Row.HasValue) item.Tip = resolvedTip;
                }
                else
                {
                    item.Row = catalog.Find(item.Tip, item.Id);
                }
            }

            // Item.as:176-194 — weapons and armour are single items, and their condition is rolled from
            // the kol argument the caller passed.
            if (item.Tip == LootTips.Weapon || item.Tip == LootTips.Armor)
            {
                item.Quantity = 1;
                bool amulet = item.Tip == LootTips.Armor && item.Row.HasValue && item.Row.Value.ArmorTip == "3";
                if (amulet)
                    item.Sost = 1f;
                else if (kolParam == 0)
                    item.Sost = 0.05f + s.Rng.NextFloat() * 0.15f;
                else if (kolParam == 1)
                    item.Sost = 0.6f + s.Rng.NextFloat() * 0.25f;
            }

            // Item.as:195-205 — a negative kol means "use the row's @kol, else 1".
            if (item.Quantity < 0 && item.Row.HasValue) item.Quantity = item.Row.Value.Kol ?? 1;

            // Item.as:254-257 — an `item`-tip roll adopts its own row's tip. This is how a `money` roll
            // becomes Item.tip == "money" and picks up the caps multiplier.
            if (item.Tip == LootTips.Item && item.Row.HasValue && !string.IsNullOrEmpty(item.Row.Value.Tip))
                item.Tip = item.Row.Value.Tip;

            return item;
        }

        // ── side effects ──────────────────────────────────────────────────────────────────────────────

        /// <summary>AS3 <c>loc.createUnit(id, nx, ny, true)</c> — no RNG involved.</summary>
        private static void CreateUnit(RollState s, string unitId)
            => s.Ctx.SideEffects?.CreateUnit(unitId, s.Nx, s.Ny);

        /// <summary>
        /// AS3 <c>LootGen.replic(state)</c> (<c>LootGen.as:1071-1077</c>) → <c>isrnd()</c> → the pip-boy
        /// animation. <b>The 50 % gate is part of the oracle</b>, so it is applied here (and it consumes
        /// a random number) rather than being pushed onto the sink.
        /// </summary>
        private static void Replic(RollState s, string state)
        {
            if (s.Rng.NextFloat() < ReplicGate) s.Ctx.SideEffects?.Replic(state);
        }

        /// <summary>AS3's <c>while(_loc9_ &lt;= _loc8_)</c> with <c>_loc9_</c> starting at 0 and
        /// <c>_loc8_</c> an integral floor result: a negative bound means <b>zero</b> iterations.</summary>
        private static int Repeat(int bound) => bound < 0 ? 0 : bound + 1;

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        //  lootCont — container tables (LootGen.as:327-779)
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        // LootGen.as:327-336
        private static void ContAmmo(RollState s)
        {
            NewLoot(s, 0.7f, LootTips.Ammo);
            NewLoot(s, 0.25f, LootTips.Ammo);
            NewLoot(s, 0.15f, LootTips.Ammo);
            if (s.Ctx.Freel) NewLoot(s, 0.7f, LootTips.Ammo);
        }

        // LootGen.as:337-343 — either/or: money, else one ammo.
        private static void ContMetal(RollState s)
        {
            float dif = s.Ctx.EffectiveDif;
            if (!NewLoot(s, 0.5f, LootTips.Item, "money", (int)(s.Rng.NextFloat() * 30f * (dif * 0.15f + 1f) + 5f)))
                NewLoot(s, 1f, LootTips.Ammo);
        }

        // LootGen.as:344-353 — exactly three dynamite.
        private static void ContBomb(RollState s)
        {
            for (int i = 0; i < 3; i++) NewLoot(s, 1f, LootTips.Expl, "dinamit");
        }

        // LootGen.as:354-368
        private static void ContExpl(RollState s)
        {
            int n = (int)Math.Floor(s.Rng.NextFloat() * 4f - 1f);
            for (int i = 0; i <= n; i++) NewLoot(s, 1f, LootTips.Expl);
            NewLoot(s, 0.5f, LootTips.Compe);
            if (s.Ctx.Freel) NewLoot(s, 0.5f, LootTips.Expl);
        }

        // LootGen.as:369-383
        private static void ContBigExpl(RollState s)
        {
            int n = (int)Math.Floor(s.Rng.NextFloat() * 4f + 2f);
            for (int i = 0; i <= n; i++) NewLoot(s, 1f, LootTips.Expl);
            if (s.Ctx.Freel) NewLoot(s, 0.5f, LootTips.Expl);
            NewLoot(s, 0.5f, LootTips.Compe);
        }

        // LootGen.as:384-407
        private static void ContWBattle(RollState s)
        {
            float dif = s.Ctx.EffectiveDif;
            if (!NewLoot(s, 0.04f, LootTips.Uniq))
            {
                if (s.Rng.NextFloat() < Math.Min(dif / 5f, 0.7f)) NewLoot(s, 1f, LootTips.Weapon, "4", 1);
                else NewLoot(s, 1f, LootTips.Weapon, "3", 1);
            }
            NewLoot(s, 0.8f, LootTips.Ammo);
            if (s.Ctx.Freel) NewLoot(s, 0.5f, LootTips.Ammo);
            NewLoot(s, 0.1f, LootTips.Item, "stealth");
            if (s.Ctx.Barahlo) NewLoot(s, 0.1f, LootTips.Compa, "intel_comp");
        }

        // LootGen.as:408-411
        private static void ContCase(RollState s)
        {
            float dif = s.Ctx.EffectiveDif;
            NewLoot(s, 0.9f, LootTips.Item, "money", (int)(s.Rng.NextFloat() * 20f * (dif * 0.11f + 1f) + 5f));
        }

        // LootGen.as:412-435 — note the two `""` ids with a `floor(rnd*4)` kol: the id is re-rolled from
        // the pool, but the kol survives, so a quantity of 0 is reachable. Oracle quirk, reproduced.
        private static void ContWBig(RollState s)
        {
            if (!NewLoot(s, 0.08f, LootTips.Uniq))
            {
                if (s.Rng.NextFloat() < 0.5f) NewLoot(s, 1f, LootTips.Weapon, "5", 1);
                else NewLoot(s, 1f, LootTips.Weapon, "4", 1);
            }
            NewLoot(s, 0.5f, LootTips.Expl, string.Empty, (int)Math.Floor(s.Rng.NextFloat() * 4f));
            NewLoot(s, 0.5f, LootTips.Ammo, string.Empty, (int)Math.Floor(s.Rng.NextFloat() * 4f));
            if (s.Ctx.Freel) NewLoot(s, 0.5f, LootTips.Ammo);
            if (s.Ctx.Barahlo) NewLoot(s, 0.5f, LootTips.Compa, "intel_comp");
        }

        // LootGen.as:436-439
        private static void ContRobocell(RollState s) => NewLoot(s, 1f, LootTips.Compm);

        // LootGen.as:440-460
        private static void ContInstr(RollState s)
        {
            NewLoot(s, 0.1f, LootTips.Item, "pin", (int)Math.Floor(s.Rng.NextFloat() * 5f + 1f));
            if (!NewLoot(s, 0.35f, LootTips.Weapon, "2", 1)) NewLoot(s, 0.5f, LootTips.Item, "rep");
            NewLoot(s, 0.85f, LootTips.Compa);
            NewLoot(s, 0.7f, LootTips.Compw);
            NewLoot(s, 0.1f, LootTips.Compe);
            NewLoot(s, 0.5f, LootTips.Compm);
            NewLoot(s, 0.5f, LootTips.Paint);
            if (s.Ctx.Barahlo)
            {
                NewLoot(s, 0.85f, LootTips.Compa);
                NewLoot(s, 0.7f, LootTips.Compw);
                NewLoot(s, 0.1f, LootTips.Compe);
                NewLoot(s, 0.1f, LootTips.Compe);
                NewLoot(s, 0.5f, LootTips.Compa);
            }
        }

        // LootGen.as:461-479
        private static void ContInstr2(RollState s)
        {
            NewLoot(s, 0.1f, LootTips.Item, "pin", (int)Math.Floor(s.Rng.NextFloat() * 5f + 1f));
            if (!NewLoot(s, 0.35f, LootTips.Weapon, "2", 1)) NewLoot(s, 0.5f, LootTips.Item, "rep");
            NewLoot(s, 0.75f, LootTips.Compa);
            NewLoot(s, 0.4f, LootTips.Compw);
            NewLoot(s, 0.5f, LootTips.Compm);
            NewLoot(s, 0.2f, LootTips.Paint);
            if (s.Ctx.Barahlo)
            {
                NewLoot(s, 0.75f, LootTips.Compa);
                NewLoot(s, 0.4f, LootTips.Compw);
                NewLoot(s, 0.1f, LootTips.Compe);
                NewLoot(s, 0.5f, LootTips.Compa);
            }
        }

        // LootGen.as:480-512
        private static void ContTrash(RollState s)
        {
            float dif = s.Ctx.EffectiveDif;
            if (s.Ctx.Biom == 0) NewLoot(s, 0.25f, LootTips.Food, "radcookie");
            if (s.Rng.NextFloat() < 0.25f)
            {
                if (s.Rng.NextFloat() < 0.6f) CreateUnit(s, "tarakan");
                else CreateUnit(s, "rat");
            }
            else
            {
                int n = (int)Math.Floor(s.Rng.NextFloat() * 2f);
                if (s.Ctx.Barahlo) n += 2;
                for (int i = 0; i <= n; i++) NewLoot(s, 1f, LootTips.Stuff);
                NewLoot(s, 0.4f, LootTips.Item, "money",
                        (int)(s.Rng.NextFloat() * 10f * (dif * 0.1f + 1f) + 5f));
            }
        }

        // LootGen.as:513-544
        private static void ContFridge(RollState s)
        {
            NewLoot(s, 0.5f, LootTips.Food, "sparklecola");
            NewLoot(s, 0.5f, LootTips.Food, "sars");
            NewLoot(s, 0.1f, LootTips.Food, "radcola");
            if (s.Rng.NextFloat() < 0.2f)
            {
                if (s.Rng.NextFloat() < 0.4f) CreateUnit(s, "tarakan");
                else if (s.Rng.NextFloat() < 0.5f) CreateUnit(s, "rat");
                else CreateUnit(s, "bloat");
            }
            else
            {
                int n = (int)Math.Floor(s.Rng.NextFloat() * 2f);
                for (int i = 0; i <= n; i++) NewLoot(s, 1f, LootTips.Food);
                NewLoot(s, 0.3f, LootTips.Compp, "herbs", (int)Math.Floor(s.Rng.NextFloat() * 6f + 1f));
            }
        }

        // LootGen.as:545-569
        private static void ContFood(RollState s)
        {
            if (s.Ctx.Biom == 0) NewLoot(s, 0.25f, LootTips.Food, "radcookie");
            if (s.Rng.NextFloat() < 0.25f)
            {
                if (s.Rng.NextFloat() < 0.6f) CreateUnit(s, "tarakan");
                else CreateUnit(s, "rat");
            }
            else
            {
                NewLoot(s, 0.8f, LootTips.Food);
                NewLoot(s, 0.5f, LootTips.Stuff);
                NewLoot(s, 0.2f, LootTips.Compp, "herbs", (int)Math.Floor(s.Rng.NextFloat() * 6f + 1f));
                NewLoot(s, 0.05f, LootTips.Tip2Co);
            }
        }

        // LootGen.as:570-584
        private static void ContMed(RollState s)
        {
            NewLoot(s, 0.05f, LootTips.Item, "pin", (int)Math.Floor(s.Rng.NextFloat() * 2f + 1f));
            int n = (int)Math.Floor(s.Rng.NextFloat() * 3f - 1f);
            for (int i = 0; i <= n; i++) NewLoot(s, 1f, LootTips.Med);
            NewLoot(s, 0.25f, LootTips.Him);
            NewLoot(s, 0.03f, LootTips.Med, "firstaid");
            NewLoot(s, 0.05f, LootTips.Pot, "potHP");
            NewLoot(s, 0.25f, LootTips.Item, "gel");
        }

        // LootGen.as:585-600
        private static void ContMed2(RollState s)
        {
            NewLoot(s, 0.75f, LootTips.Pot, "potHP");
            int n = (int)Math.Floor(s.Rng.NextFloat() * 3f);
            for (int i = 0; i <= n; i++) NewLoot(s, 1f, LootTips.Med);
            NewLoot(s, 1f, LootTips.Him);
            NewLoot(s, 0.5f, LootTips.Med, "firstaid");
            NewLoot(s, 0.5f, LootTips.Med, "doctor");
            NewLoot(s, 0.5f, LootTips.Med, "surgeon");
            NewLoot(s, 0.8f, LootTips.Item, "gel");
        }

        // LootGen.as:601-617
        private static void ContTable(RollState s)
        {
            float dif = s.Ctx.EffectiveDif;
            NewLoot(s, 0.1f, LootTips.Item, "pin", (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            if (!NewLoot(s, 0.08f, LootTips.Book))
                NewLoot(s, 0.5f, LootTips.Item, "money", (int)(s.Rng.NextFloat() * 50f + 5f + 3f * dif));
            NewLoot(s, 0.1f, LootTips.Food);
            NewLoot(s, 0.13f, LootTips.Weapon, "3");
            NewLoot(s, 0.3f, LootTips.Ammo);
            NewLoot(s, 0.1f, LootTips.Item, "dart");
            NewLoot(s, 0.1f, LootTips.Item, "app");
            NewLoot(s, 0.04f, LootTips.Scheme);
            NewLoot(s, 0.25f, LootTips.Food);
            NewLoot(s, 0.08f, LootTips.Tip2Co);
            NewLoot(s, 0.1f, LootTips.Med, "potm1");
        }

        // LootGen.as:618-625
        private static void ContFilecab(RollState s)
        {
            NewLoot(s, 0.1f, LootTips.Item, "pin", (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            NewLoot(s, 0.5f, LootTips.Item, "money", (int)(s.Rng.NextFloat() * 20f));
            NewLoot(s, 0.1f, LootTips.Item, "app");
            NewLoot(s, 0.02f, LootTips.Scheme);
            NewLoot(s, 0.08f, LootTips.Tip2Co);
        }

        // LootGen.as:626-635
        private static void ContCup(RollState s)
        {
            NewLoot(s, 0.06f, LootTips.Item, "pin", (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            NewLoot(s, 0.3f, LootTips.Item, "money", (int)(s.Rng.NextFloat() * 20f));
            NewLoot(s, 0.25f, LootTips.Compa);
            NewLoot(s, 0.75f, LootTips.Stuff);
            NewLoot(s, 0.2f, LootTips.Compa, "kombu_comp");
            NewLoot(s, 0.2f, LootTips.Compa, "antirad_comp");
            NewLoot(s, 0.2f, LootTips.Compa, "antihim_comp");
        }

        /// <summary>LootGen.as:636-639 — the only container branch that produces <b>no items at all</b>.
        /// It spawns a bloat and nothing else, so <c>lootCont</c> returns <c>false</c> and the HUD says
        /// "itsEmpty" (<c>Interact.as:2020-2023</c>). Reproduced deliberately: this is why
        /// <c>is_loot</c> counts items and not events.</summary>
        private static void ContBloat(RollState s) => CreateUnit(s, "bloat");

        // LootGen.as:640-653
        private static void ContBook(RollState s)
        {
            if (!NewLoot(s, 0.3f, LootTips.Book)) NewLoot(s, 1f, LootTips.Item, "lbook");
            NewLoot(s, 0.1f, LootTips.Item, "gem" + (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            NewLoot(s, 0.25f, LootTips.Scheme);
            NewLoot(s, 0.3f, LootTips.Tip2Co);
            if (s.Ctx.ItemsTip == "bibl") NewLoot(s, 0.5f, LootTips.Item, "book_cm");
        }

        // LootGen.as:654-665 — shared by "term" and "info".
        private static void ContTerm(RollState s)
        {
            if (s.Ctx.ActId == "minst") NewLoot(s, 1f, LootTips.Item, "datast");
            else if (!NewLoot(s, 0.25f, LootTips.Item, "disc")) NewLoot(s, 1f, LootTips.Item, "data");
            NewLoot(s, 0.5f, LootTips.Compm);
        }

        // LootGen.as:666-676
        private static void ContCryo(RollState s)
        {
            int n = (int)Math.Floor(s.Rng.NextFloat() * 3f);
            for (int i = 0; i <= n; i++) NewLoot(s, 1f, LootTips.Item, "pcryo");
            NewLoot(s, 0.5f, LootTips.Item, "gel");
        }

        /// <summary>LootGen.as:677-695 — the M1 table. Eight entries; <c>replic</c> fires on the
        /// item count, which is why the count is read <i>after</i> the rolls.</summary>
        private static void ContChest(RollState s)
        {
            float dif = s.Ctx.EffectiveDif;
            NewLoot(s, 0.1f, LootTips.Item, "pin", (int)Math.Floor(s.Rng.NextFloat() * 5f + 1f));
            NewLoot(s, 0.2f, LootTips.Weapon, "3", 2);
            NewLoot(s, 0.2f, LootTips.Item, "bit", (int)(s.Rng.NextFloat() * 50f + 7f * dif + 2f));
            NewLoot(s, 0.3f, LootTips.Item, "gem" + (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            NewLoot(s, 0.25f, LootTips.Compa);
            NewLoot(s, 0.03f, LootTips.Book);
            NewLoot(s, 0.5f, LootTips.Ammo);
            NewLoot(s, 0.03f, LootTips.Scheme);
            if (s.IsLoot > 5) Replic(s, "full");
            if (s.IsLoot < 2) Replic(s, "empty");
        }

        // LootGen.as:696-740
        private static void ContSafe(RollState s)
        {
            if (s.Ctx.LandRnd && !s.Ctx.HasProb && s.Rng.NextFloat() < 0.05f)
            {
                for (int i = 0; i < 4; i++) CreateUnit(s, "bloat");
                return;
            }

            float dif = s.Ctx.EffectiveDif;
            float d = s.Ctx.AllDif;                     // Interact.as:2013 passes 50 for an ordinary safe
            NewLoot(s, d / 100f, LootTips.Uniq);
            NewLoot(s, 0.1f + d / 100f, LootTips.Item, "sphera");
            NewLoot(s, 0.2f + d / 200f, LootTips.Item, "stealth");
            NewLoot(s, 0.25f + d / 100f, LootTips.Item, "gem" + (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            NewLoot(s, 0.2f, LootTips.Med);
            NewLoot(s, 0.25f, LootTips.Item, "retr");
            NewLoot(s, 0.1f, LootTips.Item, "runa");
            NewLoot(s, 0.1f, LootTips.Item, "reboot");
            NewLoot(s, 0.2f + d / 100f, LootTips.Book);
            NewLoot(s, 0.1f + d / 100f, LootTips.Compp);
            NewLoot(s, 1f, LootTips.Item, "bit",
                    (int)(s.Rng.NextFloat() * (d + 10f) * 8f + 2f + 4f * dif));
            NewLoot(s, 0.1f + d / 300f, LootTips.Scheme);
            NewLoot(s, 0.25f, LootTips.Pot, "potMP");
            NewLoot(s, 0.1f, LootTips.Pot, "potHP");
            if (!NewLoot(s, 0.4f, LootTips.Med, "potm2")) NewLoot(s, 0.3f, LootTips.Med, "potm3");
            if (s.IsLoot == 0) NewLoot(s, 1f, LootTips.Item, "gem" + (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            if (s.IsLoot > 6) Replic(s, "full");
            if (s.IsLoot < 2) Replic(s, "empty");
        }

        // LootGen.as:741-769
        private static void ContSpecWeap(RollState s)
        {
            var candidates = new List<string>();
            if (s.Ctx.LacksWeapon("lsword")) candidates.Add("lsword^1");
            if (s.Ctx.LacksWeapon("antidrak")) candidates.Add("antidrak^1");
            if (s.Ctx.LacksWeapon("quick")) candidates.Add("quick^1");
            if (s.Ctx.LacksWeapon("mlau")) candidates.Add("mlau^1");

            if (candidates.Count > 0)
            {
                int pick = (int)Math.Floor(s.Rng.NextFloat() * candidates.Count);
                NewLoot(s, 1f, LootTips.Weapon, candidates[pick]);
            }
            else
            {
                NewLoot(s, 1f, LootTips.Uniq);
            }
        }

        // LootGen.as:770-774
        private static void ContSpecAlc(RollState s)
        {
            NewLoot(s, 1f, LootTips.Spec, "alc7");
            NewLoot(s, 1f, LootTips.Item, "gem" + (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
        }

        // LootGen.as:775-779
        private static void ContSpecLp(RollState s)
        {
            NewLoot(s, 1f, LootTips.Spec, "lp_item");
            NewLoot(s, 1f, LootTips.Item, "gem" + (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        //  lootDrop — unit tables (LootGen.as:794-1067)
        //  Every `param5 > 0` below is the hero/boss gate (Interact.as:2009 passes Unit.hero).
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        // LootGen.as:794-799
        private static void DropScorp(RollState s)
        {
            NewLoot(s, 1f, LootTips.Compa, "chitin_comp");
            NewLoot(s, 0.25f, LootTips.Compp, "gland");
            NewLoot(s, 0.1f, LootTips.Food, "meat");
        }

        // LootGen.as:800-803
        private static void DropSlime(RollState s) => NewLoot(s, 0.75f, LootTips.Item, "acidslime");

        // LootGen.as:804-807
        private static void DropPinkSlime(RollState s) => NewLoot(s, 0.75f, LootTips.Item, "pinkslime");

        // LootGen.as:808-813 — the table D11's acceptance test keys on: only this table drops `eda`.
        private static void DropRaider(RollState s)
        {
            NewLoot(s, 0.25f, LootTips.Tip2Eda);
            NewLoot(s, 0.25f, LootTips.Ammo);
            NewLoot(s, 0.12f, LootTips.Expl);
        }

        // LootGen.as:814-822 — shared by alicorn1/2/3.
        private static void DropAlicorn(RollState s)
        {
            NewLoot(s, 1f, LootTips.Compp, "mdust");
            NewLoot(s, 0.1f, LootTips.Pot, "potMP");
            if (!NewLoot(s, 0.3f, LootTips.Med, "potm1")) NewLoot(s, 0.2f, LootTips.Med, "potm2");
        }

        // LootGen.as:823-829 — shared by ranger1/2/3.
        private static void DropRanger(RollState s)
        {
            NewLoot(s, 1f, LootTips.Item, "frag", (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            NewLoot(s, 0.5f, LootTips.Item, "scrap", (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            NewLoot(s, 1f, LootTips.Compa, "power_comp");
            NewLoot(s, 0.25f, LootTips.Ammo);
        }

        // LootGen.as:830-838 — shared by encl2/3/4.
        private static void DropEnclave(RollState s)
        {
            NewLoot(s, 0.5f, LootTips.Item, "frag", (int)Math.Floor(s.Rng.NextFloat() * 3f + 1f));
            if (!NewLoot(s, 0.3f, LootTips.Ammo, "batt")) NewLoot(s, 0.5f, LootTips.Ammo, "crystal");
            NewLoot(s, 0.3f, LootTips.Compa, "power_comp");
        }

        // LootGen.as:839-845
        private static void DropHellhound(RollState s)
        {
            if (s.Ctx.Hero > 0) NewLoot(s, 1f, LootTips.Compw, "kogt");
        }

        // LootGen.as:846-851
        private static void DropZombie(RollState s)
        {
            NewLoot(s, 0.35f, LootTips.Compp, "ghoulblood");
            NewLoot(s, 0.15f, LootTips.Compp, "radslime");
            NewLoot(s, 0.5f, LootTips.Compa, "skin_comp");
        }

        // LootGen.as:852-856
        private static void DropZombie4(RollState s)
        {
            NewLoot(s, 0.8f, LootTips.Compp, "ghoulblood");
            NewLoot(s, 1f, LootTips.Compp, "radslime");
        }

        // LootGen.as:857-861
        private static void DropZombie5(RollState s)
        {
            NewLoot(s, 1f, LootTips.Compp, "ghoulblood");
            NewLoot(s, 0.3f, LootTips.Compp, "metal_comp");
        }

        // LootGen.as:862-867
        private static void DropZombie6(RollState s)
        {
            NewLoot(s, 1f, LootTips.Compp, "ghoulblood");
            NewLoot(s, 0.3f, LootTips.Compa, "battle_comp");
            NewLoot(s, 0.8f, LootTips.Compp, "acidslime");
        }

        // LootGen.as:868-876
        private static void DropZombie7(RollState s)
        {
            NewLoot(s, 0.6f, LootTips.Compp, "ghoulblood");
            NewLoot(s, 0.2f, LootTips.Compp, "pinkslime");
            if (s.Ctx.Hero > 0) NewLoot(s, 0.6f, LootTips.Compm, "darkfrag");
        }

        // LootGen.as:877-885
        private static void DropZombie8(RollState s)
        {
            NewLoot(s, 0.6f, LootTips.Compp, "ghoulblood");
            NewLoot(s, 1f, LootTips.Compp, "pinkslime");
            if (s.Ctx.Hero > 0) NewLoot(s, 0.8f, LootTips.Compm, "darkfrag");
        }

        // LootGen.as:886-894
        private static void DropZombie9(RollState s)
        {
            NewLoot(s, 0.6f, LootTips.Compp, "ghoulblood");
            NewLoot(s, 1f, LootTips.Compp, "whorn");
            if (s.Ctx.Hero > 0) NewLoot(s, 1f, LootTips.Compm, "darkfrag");
        }

        // LootGen.as:895-900
        private static void DropBloodwing(RollState s)
        {
            NewLoot(s, 0.2f, LootTips.Compp, "wingmembrane");
            NewLoot(s, 0.16f, LootTips.Compp, "vampfang");
            NewLoot(s, 0.25f, LootTips.Food, "meat");
        }

        // LootGen.as:901-906
        private static void DropBloodwing2(RollState s)
        {
            NewLoot(s, 0.3f, LootTips.Compp, "wingmembrane");
            NewLoot(s, 0.2f, LootTips.Compp, "vampfang");
            NewLoot(s, 0.4f, LootTips.Compp, "pinkslime");
        }

        // LootGen.as:907-911
        private static void DropBloat0(RollState s)
        {
            NewLoot(s, 0.2f, LootTips.Compp, "bloatwing");
            NewLoot(s, 0.1f, LootTips.Compp, "bloateye");
        }

        // LootGen.as:912-917
        private static void DropBloat1(RollState s)
        {
            NewLoot(s, 0.2f, LootTips.Compp, "bloatwing");
            NewLoot(s, 0.1f, LootTips.Compp, "bloateye");
            NewLoot(s, 0.2f, LootTips.Compp, "acidslime");
        }

        // LootGen.as:918-923
        private static void DropBloat2(RollState s)
        {
            NewLoot(s, 0.2f, LootTips.Compp, "bloatwing");
            NewLoot(s, 0.1f, LootTips.Compp, "bloateye");
            NewLoot(s, 0.1f, LootTips.Compp, "gland");
        }

        // LootGen.as:924-929
        private static void DropBloat3(RollState s)
        {
            NewLoot(s, 0.3f, LootTips.Compp, "bloatwing");
            NewLoot(s, 0.2f, LootTips.Compp, "bloateye");
            NewLoot(s, 0.1f, LootTips.Compp, "molefat");
        }

        // LootGen.as:930-934
        private static void DropBloat4(RollState s)
        {
            NewLoot(s, 0.4f, LootTips.Compp, "bloatwing");
            NewLoot(s, 0.3f, LootTips.Compp, "bloateye");
        }

        // LootGen.as:935-940
        private static void DropRat(RollState s)
        {
            NewLoot(s, 0.35f, LootTips.Compp, "ratliver");
            NewLoot(s, 0.25f, LootTips.Compp, "rattail");
            NewLoot(s, 0.1f, LootTips.Food, "meat");
        }

        // LootGen.as:941-946
        private static void DropMoleRat(RollState s)
        {
            NewLoot(s, 0.5f, LootTips.Compp, "ratliver");
            NewLoot(s, 1f, LootTips.Compp, "molefat");
            NewLoot(s, 0.25f, LootTips.Food, "meat");
        }

        // LootGen.as:947-950
        private static void DropFish1(RollState s) => NewLoot(s, 0.5f, LootTips.Compp, "fishfat");

        // LootGen.as:951-954
        private static void DropFish2(RollState s) => NewLoot(s, 1f, LootTips.Compp, "fishfat");

        // LootGen.as:955-959
        private static void DropAnt1(RollState s)
        {
            NewLoot(s, 0.15f, LootTips.Compa, "chitin_comp");
            NewLoot(s, 0.1f, LootTips.Food, "meat");
        }

        // LootGen.as:960-964
        private static void DropAnt2(RollState s)
        {
            NewLoot(s, 0.3f, LootTips.Compa, "chitin_comp");
            NewLoot(s, 0.1f, LootTips.Food, "meat");
        }

        // LootGen.as:965-970
        private static void DropAnt3(RollState s)
        {
            NewLoot(s, 0.2f, LootTips.Compa, "chitin_comp");
            NewLoot(s, 1f, LootTips.Compp, "firegland");
            NewLoot(s, 0.1f, LootTips.Food, "meat");
        }

        // LootGen.as:971-974
        private static void DropNecros(RollState s) => NewLoot(s, 0.5f, LootTips.Item, "dsoul");

        // LootGen.as:975-978
        private static void DropEBloat(RollState s) => NewLoot(s, 1f, LootTips.Compp, "essence");

        // LootGen.as:979-987
        private static void DropTurret(RollState s)
        {
            NewLoot(s, 0.5f, LootTips.Item, "scrap");
            NewLoot(s, 0.35f, LootTips.Compw, "frag");
            if (!NewLoot(s, 0.2f, LootTips.Ammo, "batt")) NewLoot(s, 0.2f, LootTips.Ammo, "energ");
        }

        // LootGen.as:988-998
        private static void DropTurret1(RollState s)
        {
            NewLoot(s, 0.5f, LootTips.Item, "scrap");
            NewLoot(s, 0.5f, LootTips.Item, "scrap");
            NewLoot(s, 0.85f, LootTips.Compw, "frag");
            NewLoot(s, 0.52f, LootTips.Compa, "magus_comp");
            if (!NewLoot(s, 0.4f, LootTips.Ammo, "batt")) NewLoot(s, 0.8f, LootTips.Ammo, "energ");
        }

        // LootGen.as:999-1010
        private static void DropRobobrain(RollState s)
        {
            NewLoot(s, 0.25f, LootTips.Item, "scrap");
            NewLoot(s, 0.15f, LootTips.Compw, "frag");
            NewLoot(s, 0.5f, LootTips.Compm);
            NewLoot(s, 0.5f, LootTips.Ammo, "batt");
            NewLoot(s, 0.4f, LootTips.Compa, "metal_comp");
            if (s.Ctx.Hero > 0) NewLoot(s, 1f, LootTips.Compm, "impgen");
        }

        // LootGen.as:1011-1022
        private static void DropProtect(RollState s)
        {
            NewLoot(s, 0.3f, LootTips.Item, "scrap");
            NewLoot(s, 0.25f, LootTips.Compw, "frag");
            NewLoot(s, 0.6f, LootTips.Compm);
            NewLoot(s, 0.9f, LootTips.Ammo, "batt");
            NewLoot(s, 0.4f, LootTips.Compa, "metal_comp");
            if (s.Ctx.Hero > 0) NewLoot(s, 1f, LootTips.Compm, "uscan");
        }

        // LootGen.as:1023-1037
        private static void DropGutsy(RollState s)
        {
            NewLoot(s, 0.45f, LootTips.Item, "scrap");
            NewLoot(s, 0.5f, LootTips.Compw, "frag");
            NewLoot(s, 0.7f, LootTips.Compm);
            NewLoot(s, 0.85f, LootTips.Compa, "battle_comp");
            if (!NewLoot(s, 0.4f, LootTips.Ammo, "fuel")) NewLoot(s, 0.75f, LootTips.Ammo, "energ");
            if (s.Ctx.Hero > 0) NewLoot(s, 1f, LootTips.Compm, "tlaser");
        }

        // LootGen.as:1038-1050
        private static void DropEqd(RollState s)
        {
            NewLoot(s, 0.45f, LootTips.Item, "scrap");
            NewLoot(s, 0.5f, LootTips.Compw, "frag");
            NewLoot(s, 0.8f, LootTips.Compm);
            NewLoot(s, 0.85f, LootTips.Compa, "magus_comp");
            NewLoot(s, 1f, LootTips.Ammo, "energ");
            NewLoot(s, 0.5f, LootTips.Item, "data");
            if (s.Ctx.Hero > 0) NewLoot(s, 1f, LootTips.Compm, "pcrystal");
        }

        // LootGen.as:1051-1063
        private static void DropSentinel(RollState s)
        {
            NewLoot(s, 0.85f, LootTips.Item, "scrap");
            NewLoot(s, 0.5f, LootTips.Compw, "frag");
            NewLoot(s, 1f, LootTips.Compm);
            if (!NewLoot(s, 0.4f, LootTips.Ammo, "p5")) NewLoot(s, 1f, LootTips.Ammo, "crystal");
            NewLoot(s, 0.85f, LootTips.Ammo, "rocket");
            NewLoot(s, 0.5f, LootTips.Compw);
            NewLoot(s, 1f, LootTips.Compm, "motiv");
        }

        // LootGen.as:1064-1067 — shared by vortex/spritebot/roller.
        private static void DropScrapOnly(RollState s) => NewLoot(s, 0.2f, LootTips.Item, "scrap");
    }
}
