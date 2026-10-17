using System;
using PFE.Core;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// The runtime difficulty SETTING — AS3 <c>Game.globalDif</c> (<c>Game.as:41</c>) and
    /// <c>Game.changeDif</c> (<c>Game.as:311-329</c>).
    ///
    /// <para><b>This is AXIS A of the difficulty model, and it is a different number from the land/run
    /// difficulty (Axis B).</b> <c>globalDif</c> is chosen in the main menu, lives for the whole run, and
    /// adjusts the player's own parameters, enemy HP, hearing and checkpoints. It does <b>not</b> scale
    /// loot quality — that is <c>locDifLevel</c>/<c>weaponLevel</c>, derived from the land and the
    /// player's level. See <c>docs/DifficultyModel/01_ORACLE_DIFFICULTY_MODEL.md</c> §0.</para>
    ///
    /// <para><b>Why a static rather than a DI registration.</b> The readers are spread across the room
    /// streaming path (<c>LocationDifficultyPlanner</c>, built by <c>RoomGenerator</c>, which
    /// <c>MapBridge</c> constructs outside the container), the enemy spawn path and the loot sinks — none
    /// of which can resolve a container-scoped service. The port's established answer for "a value built
    /// once at boot that gameplay reads" is a static with a settable value; this mirrors
    /// <c>LootRuntime.Rng</c>, <c>RoomPopulator.GetSpawnRng</c> and <c>CharacterStats.CombatRng</c>.</para>
    ///
    /// <para><b>Two behaviours are the oracle's own, and are reproduced deliberately:</b></para>
    /// <list type="number">
    /// <item><see cref="Change"/> compares the <b>raw</b> argument against the current value
    /// (<c>Game.as:313-316</c>) <i>before</i> clamping. So <c>Change(9)</c> on a run already at 4 returns
    /// <c>true</c> every time and re-runs the parameter table, even though the stored value never moves.
    /// That is observable through the returned bool, which the caller uses to decide whether to redraw,
    /// so it is reproduced rather than "fixed".</item>
    /// <item>The clamp is <c>0..4</c> (<c>Game.as:318-325</c>). Levels 5+ do not exist and the port's
    /// <c>NewGameDifficulty</c> enum has exactly five members — do not invent a sixth.</item>
    /// </list>
    ///
    /// <para><b>The value's source at boot.</b> The oracle writes <c>globalDif</c> from the new-game
    /// settings/save and defaults to 2 (<c>Game.as:41,99,183</c>). The port's
    /// <c>NewGameSettings.difficulty</c> is the same 0..4 range with <c>Normal = 2</c>, so there is
    /// <b>no translation</b>. <see cref="Value"/> falls back to that setting until something calls
    /// <see cref="Seed"/> or <see cref="Change"/> — which makes the read correct regardless of whether
    /// the Pip-Buck or the world build runs first.</para>
    /// </summary>
    public static class GlobalDifficulty
    {
        /// <summary>AS3 <c>Game.globalDif = 2</c> (<c>Game.as:41</c>) — Normal.</summary>
        public const int Default = 2;

        /// <summary>The clamp floor (<c>Game.as:318-321</c>).</summary>
        public const int MinIndex = 0;

        /// <summary>The clamp ceiling (<c>Game.as:322-325</c>).</summary>
        public const int MaxIndex = 4;

        /// <summary>
        /// Raised after a change was applied, with the new (clamped) value. The reader is the player's
        /// <c>CharacterStats</c>, which re-runs the <c>Pers.setGlobalDif</c> parameter table — the
        /// oracle's <c>changeDif</c> tail (<c>Game.as:326-327</c>).
        ///
        /// <para>It fires even when the stored value did not move, because that is what the oracle does:
        /// the raw comparison lets <c>changeDif(9)</c> through at level 4 and it re-applies the table
        /// every call. See the class remarks.</para>
        /// </summary>
        public static event Action<int> Changed;

        private static int _value = Default;
        private static bool _resolved;

        /// <summary>
        /// The current setting, 0..4. Before anything seeds it, this resolves once from the pending
        /// new-game settings (<see cref="GameBootData.PendingDifficultyIndex"/>) and remembers the
        /// answer, so a reader that runs before the world build still sees the chosen difficulty.
        /// </summary>
        public static int Value
        {
            get
            {
                if (!_resolved)
                {
                    _value = Clamp(GameBootData.PendingDifficultyIndex(Default));
                    _resolved = true;
                }

                return _value;
            }
        }

        /// <summary>AS3's two clamp blocks (<c>Game.as:318-325</c>), as a value.</summary>
        public static int Clamp(int n) => n < MinIndex ? MinIndex : (n > MaxIndex ? MaxIndex : n);

        /// <summary>
        /// AS3 <c>Game.changeDif(n)</c> (<c>Game.as:311-329</c>). Returns <c>false</c> when the raw
        /// argument equals the current value — the early return the caller's redraw decision rides on
        /// (<c>Game.as:313-316</c>).
        ///
        /// <para><b>The comparison is against the raw argument, not the clamp result</b> — see the class
        /// remarks. <c>Change(-1)</c> at 0 returns <c>true</c> and clamps to 0; <c>Change(9)</c> at 4
        /// returns <c>true</c> and clamps to 4.</para>
        /// </summary>
        /// <returns><c>true</c> when a change was applied (i.e. the caller should react).</returns>
        public static bool Change(int n)
        {
            // Read through Value, not the backing field: an unresolved static still holds the declared
            // 2, while the run's real value is the new-game choice. Comparing against the declared
            // default would report a spurious change for a game started at a non-Normal difficulty.
            if (n == Value)
            {
                // Game.as:313-316 — `if(param1 == this.globalDif) return false;`
                return false;
            }

            _value = Clamp(n);
            _resolved = true;
            Changed?.Invoke(_value);
            return true;
        }

        /// <summary>
        /// Assign the boot value unconditionally. Unlike <see cref="Change"/> this always applies, even
        /// when it equals the current value — the oracle's <c>Pers</c> constructor applies the parameter
        /// table once at construction regardless (<c>Pers.as:517-528</c>), and a new game at Normal must
        /// still get Normal's table rather than whatever the field initialisers left.
        /// </summary>
        public static void Seed(int n)
        {
            _value = Clamp(n);
            _resolved = true;
            Changed?.Invoke(_value);
        }

        /// <summary>
        /// AS3's console verb <c>dif X</c> (<c>Consol.as:237-247</c>) — write <c>Game.globalDif</c> and
        /// re-run the parameter table.
        ///
        /// <para><b>Why this is not <see cref="Change"/>.</b> The oracle's console path does not call
        /// <c>changeDif</c>. It assigns the field directly, clamps with the same two blocks, and then
        /// calls <c>pers.setGlobalDif</c> + <c>pers.setParameters</c> — so it has <b>no equality
        /// early-return</b>: <c>dif 2</c> on a run already at 2 still re-applies Normal's table. That is
        /// identical to <see cref="Seed"/> in behaviour and differs only in intent, which is why the two
        /// are separate names rather than one method called from both places: a future reader must not
        /// have to work out whether the boot path and the debug verb are allowed to share a tail.</para>
        ///
        /// <para><b>One deliberate divergence.</b> The oracle reads the argument as a raw token
        /// (<c>_loc2_[1]</c>) and compares it numerically, so a non-numeric token coerces to <c>NaN</c>,
        /// both comparisons are false, and the field is left holding a <i>string</i> — after which
        /// <c>setGlobalDif</c> matches no arm and silently applies nothing. The port parses to
        /// <c>int</c> and the console reports a usage error instead; a silent no-op is a worse debug
        /// tool than a message.</para>
        /// </summary>
        public static void SetFromConsole(int n)
        {
            _value = Clamp(n);
            _resolved = true;
            Changed?.Invoke(_value);
        }

        /// <summary>
        /// Drop the resolved value and every subscriber. Test seam — nothing in shipping code calls it.
        /// </summary>
        public static void ResetForTests()
        {
            _value = Default;
            _resolved = false;
            Changed = null;
        }
    }
}
