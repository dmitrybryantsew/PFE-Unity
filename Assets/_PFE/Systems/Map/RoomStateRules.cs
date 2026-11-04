using System.Collections.Generic;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The three decisions that make a room survive its own view being destroyed, as pure functions.
    /// </summary>
    /// <remarks>
    /// <para><b>Why these live here rather than inline at their call sites.</b> Every one of them is a
    /// question with a wrong answer that looks right, and the wrong answers are silent: a corpse that is
    /// not pruned respawns, a health value that is restored from the field default heals a 300-HP boss,
    /// and a loot record with a zero count either duplicates or vanishes depending on which way the guard
    /// falls. None of those produce a log line, and none of them can be caught by a compile. Pulled out
    /// as pure functions they are fixture-pinned; left inline they are only ever exercised by a
    /// play-test.</para>
    ///
    /// <para><b>Unity-free on purpose.</b> No <c>UnityEngine</c> types appear in any signature, so the
    /// file can be linked into an offline harness as source (route A) as well as run inside the real test
    /// assembly (route B). That is the same contract <c>LootFallStepRules</c> and
    /// <c>DoorDestructionMath</c> hold to.</para>
    /// </remarks>
    public static class RoomStateRules
    {
        /// <summary>
        /// Remove every corpse from a room's unit list — AS3 <c>Location.resetUnits()</c>
        /// (<c>Location.as:2243</c>: <c>units = units.filter(u =&gt; u.sost &lt; 4)</c>).
        /// </summary>
        /// <remarks>
        /// <b>On re-entry, never at death.</b> A prob room counts its waves off this list during the fight,
        /// so a record that disappears the frame its unit dies completes a wave a beat early. See
        /// <see cref="RoomInstance.PruneDeadUnits"/> for the full reasoning and for who calls this.
        /// </remarks>
        /// <returns>How many records were dropped.</returns>
        public static int PruneDeadUnits(List<UnitInstance> units)
        {
            if (units == null)
            {
                return 0;
            }

            int removed = 0;
            for (int i = units.Count - 1; i >= 0; i--)
            {
                if (units[i] == null || units[i].IsDead)
                {
                    units.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// The health a rebuilt unit should start with, or <c>false</c> to leave whatever the constructor
        /// built.
        /// </summary>
        /// <param name="hasRuntimeHealth">
        /// <see cref="UnitInstance.hasRuntimeHealth"/> — whether the record's health is data or the field
        /// defaults. <b>This is the guard the whole rule exists for.</b> Without it a unit that has never
        /// been hurt arrives with <c>currentHealth == 100</c> and a 300-HP boss is rebuilt at 100.
        /// </param>
        /// <param name="recordedHealth"><see cref="UnitInstance.currentHealth"/>.</param>
        /// <param name="builtMaxHealth">
        /// The ceiling the view actually built — after the definition, the difficulty cut and the elite
        /// multiplier, i.e. after <c>UnitController.ApplyPlacement</c>. Not the record's
        /// <c>maxHealth</c>: that is a note of what the ceiling used to be, and restoring it would let a
        /// save outvote a difficulty change.
        /// </param>
        /// <param name="health">The value to assign, when this returns <c>true</c>.</param>
        /// <returns>
        /// <c>false</c> when the record carries no health, when the recorded value is not positive, or
        /// when the view has no ceiling to clamp against.
        /// </returns>
        /// <remarks>
        /// <para><b>A non-positive recorded value is refused rather than clamped.</b> Zero means "this unit
        /// died", and a dead record must never be spawned: <see cref="PruneDeadUnits"/> drops it first, so
        /// reaching here with a zero means the two halves disagree. Writing <c>1</c> would hide that and
        /// produce a unit that is alive at one hit point with no death event behind it.</para>
        ///
        /// <para><b>Clamped, not trusted.</b> A record can legitimately exceed the rebuilt ceiling: the
        /// player changed the difficulty setting, which scales non-player HP at construction
        /// (<c>Unit.as:1130-1141</c>), or an elite tier was re-rolled. Clamping to the ceiling keeps the
        /// unit alive-and-full rather than at 150 % of a maximum it can never reach again.</para>
        /// </remarks>
        public static bool TryResolveRestoredHealth(bool hasRuntimeHealth, float recordedHealth,
                                                    float builtMaxHealth, out float health)
        {
            health = 0f;

            if (!hasRuntimeHealth || recordedHealth <= 0f || builtMaxHealth <= 0f)
            {
                return false;
            }

            health = recordedHealth > builtMaxHealth ? builtMaxHealth : recordedHealth;
            return true;
        }

        /// <summary>
        /// Whether a floor-loot record is complete enough to be given a view.
        /// </summary>
        /// <remarks>
        /// <para><b>An incomplete record is skipped, not deleted.</b> The caller
        /// (<c>WorldItemPickup.RestoreForRoom</c>) leaves it on the room, so the next build retries it.
        /// That matters because the resolver a record needs is published by
        /// <c>PlayerInventory.Construct</c> and a room can be built before the player exists — deleting
        /// the record in that window would destroy a save's loot because of a load order.</para>
        ///
        /// <para><b>The quantity guard is a real case, not paranoia.</b> AS3's <c>Loot</c> constructor
        /// defaults <c>kol</c> to one, and a stack of zero would be a view that can never be taken; a
        /// save written by an older or buggier path must not turn into permanent invisible clutter on the
        /// floor.</para>
        /// </remarks>
        public static bool IsRestorableLoot(DroppedItemInstance item)
        {
            return item != null
                && !string.IsNullOrEmpty(item.itemId)
                && item.quantity > 0;
        }
    }
}
