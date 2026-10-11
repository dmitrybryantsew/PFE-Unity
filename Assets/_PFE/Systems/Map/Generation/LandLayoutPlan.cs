using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Generation
{
    /// <summary>How a cell's room template is chosen (AS3 <c>Land.buildRandomLand</c> cell-fill branch).</summary>
    public enum CellFill
    {
        /// <summary>No template chosen (should not appear in a finished plan).</summary>
        None = 0,
        /// <summary>AS3 <c>newTipLoc(tip, x, y)</c> — a specific <c>tip</c> is required.</summary>
        Tip = 1,
        /// <summary>AS3 <c>newRandomLoc(stage, x, y)</c> — weighted random fill.</summary>
        Random = 2,
    }

    /// <summary>AS3 <c>Location.createExit(param)</c> — the exit box this cell carries.</summary>
    public enum ExitKind
    {
        None = 0,
        /// <summary><c>createExit()</c> — <c>prob = "exit_&lt;land&gt;"</c>.</summary>
        Shallow = 1,
        /// <summary><c>createExit("1")</c> — <c>prob = "exit_&lt;land&gt;1"</c>, the hand-off room.</summary>
        Deep = 2,
    }

    /// <summary>AS3 <c>Location.createCheck(isBeg)</c>.</summary>
    public enum CheckpointKind
    {
        None = 0,
        /// <summary><c>createCheck(false)</c> — a plain checkpoint.</summary>
        Normal = 1,
        /// <summary><c>createCheck(true)</c> — the entry checkpoint; <c>teleOn = true</c> at <c>landStage == 0</c>.</summary>
        Begin = 2,
    }

    /// <summary>AS3 <c>Land.newRandomProb(loc, maxlevel, imp)</c>.</summary>
    public enum ProbKind
    {
        None = 0,
        /// <summary><c>imp = true</c> — prefers a prob carrying <c>imp='1'</c>.</summary>
        Forced = 1,
        /// <summary>The random roll (<c>Math.random() &lt; 0.3</c> / <c>&lt; 0.25</c>).</summary>
        Chance = 2,
    }

    /// <summary>
    /// One grid cell of a procedural land, as planned by <see cref="LandLayoutPlanner"/>.
    /// Pure data: no Unity objects, so it is assertable offline.
    /// Ports the per-cell part of AS3 <c>Land.buildRandomLand()</c> (<c>Land.as:163-682</c>).
    /// </summary>
    public struct LandCellPlan
    {
        /// <summary>Grid cell, AS3 space (Y grows downward).</summary>
        public Vector2Int Position;

        public CellFill Fill;

        /// <summary>Required room <c>tip</c> when <see cref="Fill"/> is <see cref="CellFill.Tip"/>.</summary>
        public string Tip;

        /// <summary>The <c>param1</c> passed to AS3 <c>newRandomLoc</c> when <see cref="Fill"/> is
        /// <see cref="CellFill.Random"/>. Conf 4/7/10 pass a literal, not <c>landStage</c>.</summary>
        public int SelectionStage;

        /// <summary>AS3 <c>_loc3_.mirror</c> — mirror the door array before matching (<c>Land.as:192</c>).
        /// Always false on a <c>beg*</c> cell.</summary>
        public bool Mirror;

        /// <summary>AS3 <c>_loc3_.ramka</c> (border form); 0 = unset.</summary>
        public int Ramka;

        /// <summary>AS3 <c>_loc3_.backform</c>.</summary>
        public int Backform;

        /// <summary>AS3 <c>Location.bezdna</c> — bottomless (conf 3's pass column).</summary>
        public bool Bezdna;

        /// <summary>AS3 <c>Location.home</c> — suppresses XP scatter via <c>homeStable</c>.</summary>
        public bool Home;

        /// <summary>AS3 <c>Location.atk</c> (conf 11).</summary>
        public bool Atk;

        /// <summary>AS3 <c>_loc3_.transpFon</c> — transparent parallax background for this cell.</summary>
        public bool TranspFon;

        /// <summary>AS3 <c>_loc3_.water</c> — water level; -1 = unset (AS3 <c>null</c>).</summary>
        public int Water;

        /// <summary>AS3 <c>Location.gas</c> (conf 5 sets it on every cell).</summary>
        public bool Gas;

        /// <summary>AS3 <c>Location.visMult</c>; 0 = unset.</summary>
        public int VisMult;

        /// <summary>AS3 <c>Location.petOn</c> — false on conf 2 rows below the top two.</summary>
        public bool PetOn;

        /// <summary>The <c>yOffset</c> handed to <c>setLocDif</c> (<c>Land.as:958-975</c>).</summary>
        public float DifficultyOffset;

        public ExitKind Exit;

        public CheckpointKind Checkpoint;

        public ProbKind Prob;

        /// <summary>AS3 <c>_loc10_</c> — whether <c>createXpBonuses(5)</c> runs for this cell.</summary>
        public bool XpBonuses;

        /// <summary>Conf 3: column <c>x == 2</c> takes no exit / checkpoint / prob (<c>Land.as:580</c>).</summary>
        public bool SuppressPlacement;

        public bool HasExit => Exit != ExitKind.None;
    }

    /// <summary>
    /// A prob that AS3 places <em>before</em> the main placement pass
    /// (<c>Land.as:495-502</c>) rather than from a per-cell rule.
    /// </summary>
    public struct ForcedProbPlan
    {
        public Vector2Int Position;

        /// <summary>Placed only when <c>landStage &gt;= MinStage</c> (conf 3 uses 1; conf 2 uses 0).</summary>
        public int MinStage;
    }

    /// <summary>
    /// The complete, engine-free description of a procedural land's layout.
    /// Consume it to build rooms; assert it to test the layout rules without a scene.
    /// </summary>
    public sealed class LandLayoutPlan
    {
        public string LandId = "";
        public int Conf;
        public int LandStage;

        /// <summary>Grid size in cells (AS3 <c>mx</c>, <c>my</c>) after any conf-specific clamp.</summary>
        public Vector2Int GridSize;

        /// <summary>Inclusive minimum cell (always <c>(0,0)</c> for a procedural land).</summary>
        public Vector2Int BoundsMin;

        /// <summary>Exclusive maximum cell (equals <see cref="GridSize"/>).</summary>
        public Vector2Int BoundsMax;

        /// <summary>Conf 6 sets <c>transpFon</c> on the land itself (<c>Land.as:630</c>).</summary>
        public bool TranspFon;

        /// <summary>Conf 4 zeroes a picked room's <c>kol</c> (<c>Land.as:938-941</c>), so every template
        /// may be used at most once and the grid is a set, not a bag.</summary>
        public bool UniqueRoomPerCell;

        public List<LandCellPlan> Cells = new List<LandCellPlan>();

        public List<ForcedProbPlan> ForcedProbs = new List<ForcedProbPlan>();

        public int CellCount => Cells.Count;

        public bool TryGetCell(int x, int y, out LandCellPlan cell)
        {
            for (int i = 0; i < Cells.Count; i++)
            {
                if (Cells[i].Position.x == x && Cells[i].Position.y == y)
                {
                    cell = Cells[i];
                    return true;
                }
            }

            cell = default;
            return false;
        }

        public LandCellPlan GetCell(int x, int y)
        {
            TryGetCell(x, y, out LandCellPlan cell);
            return cell;
        }

        public bool HasCell(int x, int y) => TryGetCell(x, y, out _);
    }
}
