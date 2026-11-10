using UnityEngine;
using R3;
using PFE.Systems.Weapons;
using PFE.Entities.Units;
using PFE.Systems.Effects;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using PFE.UI.Common;

namespace PFE.UI.HUD
{
    /// <summary>
    /// ViewModel for the Heads-Up Display (HUD).
    /// Bridges the gap between game logic (PlayerWeaponLoadout, UnitStats) and UI components.
    /// Exposes reactive properties that UI views can bind to using R3.
    ///
    /// Design Pattern: Model-View-ViewModel (MVVM)
    /// - Model: PlayerWeaponLoadout, UnitStats (game data and logic)
    /// - ViewModel: This class (transforms data for UI consumption)
    /// - View: HealthBarView, AmmoCounterView, etc. (display data)
    ///
    /// Benefits:
    /// - UI doesn't need to know about game logic directly
    /// - Reactive properties update UI automatically when data changes
    /// - Testable (can mock ViewModel without UI)
    /// - Separation of concerns (UI vs game logic)
    /// </summary>
    public class HUDViewModel : MonoBehaviour
    {
        [Header("Game Data Sources")]
        [SerializeField]
        [Tooltip("The player's weapon loadout. Null until the player is found; the bootstrapper " +
                 "re-binds when it appears.")]
        internal PlayerWeaponLoadout playerLoadout;

        [SerializeField]
        [Tooltip("Player stats to display health for")]
        internal UnitStats playerStats;

        /// <summary>
        /// The organ-level stats, needed because AS3's HUD shows <b>two different mana values</b>
        /// at once — see <see cref="ManaPercent"/> and <see cref="ManaOrganPercent"/>. Null is
        /// tolerated: the organ properties then stay at zero and only the budget is available.
        /// </summary>
        [SerializeField]
        [Tooltip("Organ-level stats. Supplies the mana ORGAN (manaHp), which is not on UnitStats.")]
        internal CharacterStats characterStats;

        // Reactive properties for UI binding
        private ReadOnlyReactiveProperty<int> _currentAmmo;
        private ReadOnlyReactiveProperty<int> _maxAmmo;
        private ReadOnlyReactiveProperty<float> _ammoPercent;
        private ReadOnlyReactiveProperty<float> _reloadProgress;
        private ReadOnlyReactiveProperty<bool> _isReloading;
        private ReadOnlyReactiveProperty<float> _healthPercent;
        private ReadOnlyReactiveProperty<float> _currentHealth;
        private ReadOnlyReactiveProperty<float> _maxHealth;
        private ReadOnlyReactiveProperty<string> _healthText;
        private ReadOnlyReactiveProperty<bool> _isAlive;
        private ReadOnlyReactiveProperty<float> _currentMana;
        private ReadOnlyReactiveProperty<float> _maxMana;
        private ReadOnlyReactiveProperty<float> _manaPercent;
        private ReadOnlyReactiveProperty<float> _currentManaOrgan;
        private ReadOnlyReactiveProperty<float> _maxManaOrgan;
        private ReadOnlyReactiveProperty<float> _manaOrganPercent;
        private ReadOnlyReactiveProperty<float> _armourPercent;
        private ReadOnlyReactiveProperty<bool> _hasArmour;
        private ReadOnlyReactiveProperty<string> _manaText;
        private ReadOnlyReactiveProperty<float> _experiencePercent;

        // Composite disposable for cleanup
        private CompositeDisposable _disposables;

        /// <summary>
        /// Current ammo in weapon (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<int> CurrentAmmo => _currentAmmo;

        /// <summary>
        /// Max ammo capacity (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<int> MaxAmmo => _maxAmmo;

        /// <summary>
        /// Ammo percentage 0-1 (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> AmmoPercent => _ammoPercent;

        /// <summary>
        /// Reload progress 0-1 (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> ReloadProgress => _reloadProgress;

        /// <summary>
        /// Whether weapon is currently reloading (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<bool> IsReloading => _isReloading;

        /// <summary>
        /// Health percentage 0-1 (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> HealthPercent => _healthPercent;

        /// <summary>
        /// Current health value (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> CurrentHealth => _currentHealth;

        /// <summary>
        /// Maximum health value (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> MaxHealth => _maxHealth;

        /// <summary>
        /// The "current / max" readout, as a single reactive string — AS3 <c>GUI.setHp</c>'s
        /// <c>hp.text = Math.ceil(hp) + "/" + Math.ceil(maxhp)</c> (<c>GUI.as:944</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>It is one property over BOTH numbers, and that is the fix for a real defect.</b>
        /// The view used to subscribe to <see cref="CurrentHealth"/> alone and call
        /// <see cref="GetHealthText"/> inside the handler, which reads <c>MaxHp</c> at that moment. A
        /// max that changed without a second health write — a level-up at full health, where the pool
        /// goes 100/100 → 115/115 — therefore left the text showing the old maximum until the player
        /// took damage. The bar was right (it is a <c>CombineLatest</c> over both) and the number beside
        /// it was not, which reads as "the max lags behind".</para>
        ///
        /// <para>AS3 cannot have this bug: <c>GUI.setHp</c> is called from the frame loop and reads
        /// both fields live every time.</para>
        /// </remarks>
        public ReadOnlyReactiveProperty<string> HealthText => _healthText;

        /// <summary>
        /// Whether player is alive (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<bool> IsAlive => _isAlive;

        /// <summary>
        /// The <b>magic budget</b> — AS3 <c>Unit.mana</c>, ceiling 1000, regenerates every tick.
        /// This is what AS3's HUD prints as a <i>number</i>: <c>GUI.setMana</c> writes
        /// <c>round(gg.mana / 10) + "%"</c> (<c>GUI.as:993</c>).
        ///
        /// <para><b>Do not draw the mana BAR from this.</b> AS3 draws the bar from the organ — see
        /// <see cref="ManaOrganPercent"/>. The two halves of that HUD disagree on purpose, and a
        /// port that uses one pool for both looks correct until the budget regenerates while the
        /// bar stays pinned.</para>
        /// </summary>
        public ReadOnlyReactiveProperty<float> CurrentMana => _currentMana;

        /// <summary>
        /// The budget's ceiling — AS3 <c>Unit.maxmana</c>, 1000. See <see cref="CurrentMana"/>.
        /// </summary>
        public ReadOnlyReactiveProperty<float> MaxMana => _maxMana;

        /// <summary>
        /// The budget as a 0..1 fraction — the value behind AS3's <c>NN%</c> mana readout
        /// (<c>GUI.as:993</c>), where the percentage is <c>round(mana / 10)</c> only because the
        /// ceiling is 1000.
        /// </summary>
        public ReadOnlyReactiveProperty<float> ManaPercent => _manaPercent;

        /// <summary>
        /// The <b>mana organ</b> — AS3 <c>Pers.manaHP</c>, ceiling 400, a wound track that does
        /// <i>not</i> regenerate. Read from <see cref="characterStats"/>.
        /// </summary>
        public ReadOnlyReactiveProperty<float> CurrentManaOrgan => _currentManaOrgan;

        /// <summary>The organ's ceiling — AS3 <c>Pers.inMaxMana</c>, 400.</summary>
        public ReadOnlyReactiveProperty<float> MaxManaOrgan => _maxManaOrgan;

        /// <summary>
        /// The organ as a 0..1 fraction. <b>This is the mana bar's value.</b> AS3:
        /// <c>vis.manaBar.mana.scaleX = gg.pers.manaHP / gg.pers.inMaxMana</c>
        /// (<c>GUI.as:999</c>), while the adjacent text shows the budget. Both are in
        /// <c>setMana</c>, six lines apart.
        /// </summary>
        public ReadOnlyReactiveProperty<float> ManaOrganPercent => _manaOrganPercent;

        /// <summary>
        /// Equipped armour integrity 0-1 (for binding). 0 when nothing is equipped — check
        /// <see cref="HasArmour"/> before drawing it.
        /// </summary>
        public ReadOnlyReactiveProperty<float> ArmourPercent => _armourPercent;

        /// <summary>
        /// Whether any armour is equipped (for binding). The armour bar's visibility gate.
        /// </summary>
        public ReadOnlyReactiveProperty<bool> HasArmour => _hasArmour;

        /// <summary>
        /// The magic-budget readout as one reactive string — AS3 <c>GUI.setMana</c>'s three branches,
        /// over <c>gg.mana</c> (<c>GUI.as:987-998</c>). The rule is
        /// <see cref="ManaReadout.Format"/>; this is only its lifetime.
        /// </summary>
        /// <remarks>
        /// <para><b>Reactive, not a per-frame call, because the mana text is the one readout whose
        /// <i>silence</i> is meaningful.</b> A full pool prints nothing, so a view that polled
        /// <see cref="GetManaText"/> would have to decide every frame whether an empty string means
        /// "full" or "no source". Binding to the stream makes the empty state an ordinary value.</para>
        /// </remarks>
        public ReadOnlyReactiveProperty<string> ManaText => _manaText;

        /// <summary>
        /// Progress through the current level, 0..1 — the value behind AS3
        /// <c>vis.xpBar.xp.scaleX</c> (<c>GUI.as:1057-1061</c>). Read from
        /// <see cref="characterStats"/>, so it stays at 0 for a HUD with no player block.
        /// </summary>
        /// <remarks>
        /// Polled with <c>EveryUpdate</c> rather than published, for the same reason as the mana organ:
        /// <c>CharacterStats.xp</c>/<c>level</c> are plain serialized fields written by
        /// <c>GainXp</c>/<c>UpLevel</c> with no change stream, and a level-up changes <i>two</i> of them
        /// at once, so a subscription to either alone would show one frame of the wrong fraction.
        /// </remarks>
        public ReadOnlyReactiveProperty<float> ExperiencePercent => _experiencePercent;

        /// <summary>
        /// This player's live status effects — AS3 <c>Unit.effects</c>, for the effect strip. Null
        /// before the player exists; the strip tolerates that and draws nothing.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately not a reactive property.</b> The set is a plain list with no change
        /// notification (see <see cref="ActiveEffectSet"/>), and every consumer of it — the strip, a
        /// debug readback — wants the whole list rather than a diff. A view polls it and writes only
        /// what changed.
        /// </remarks>
        public ActiveEffectSet Effects => playerStats != null ? playerStats.Effects : null;

        /// <summary>
        /// AS3 <c>gg.shithp</c> — the magic-shield pool. One of the effect strip's three hard-coded
        /// priority slots (<c>GUI.as:1191-1197</c>); 0 when there is no player.
        /// </summary>
        public float ShieldHp => playerStats != null ? playerStats.ShitHp : 0f;

        /// <summary>
        /// Initialize the ViewModel with its game data sources.
        /// Call this when setting up the HUD.
        /// </summary>
        /// <param name="loadout">Player's weapon loadout</param>
        /// <param name="stats">Player's stats</param>
        /// <param name="organs">
        /// Organ-level stats, for the mana organ. Optional: pass null and the three
        /// <c>...ManaOrgan</c> properties stay at zero while the budget properties still work.
        /// </param>
        public void Initialize(PlayerWeaponLoadout loadout, UnitStats stats, CharacterStats organs = null)
        {
            playerLoadout = loadout;
            playerStats = stats;
            characterStats = organs;

            // Rebind, not SetupBindings: rebinding replaces the ReadOnlyReactiveProperty instances,
            // and the previous set must be disposed or every re-initialise leaks a live subscription
            // on UnitStats.CurrentHp.
            Rebind();
        }

        // ── Source selection ──────────────────────────────────────────────────
        // The loadout exposes the reactive shape the bindings below need (CurrentAmmo / IsReloading /
        // ReloadProgress as ReactiveProperty<T>). It is the only weapon source since Phase 2.2 retired
        // the legacy WeaponLogic path; a null loadout means "no weapon yet", not "try the other path".

        private ReactiveProperty<int> AmmoSource => playerLoadout?.CurrentAmmo;

        private ReactiveProperty<bool> ReloadingSource => playerLoadout?.IsReloading;

        private ReactiveProperty<float> ReloadProgressSource => playerLoadout?.ReloadProgress;

        /// <summary>
        /// Magazine capacity for the equipped weapon. Read through the live controller rather than
        /// cached: the loadout can swap weapons, and a stale capacity makes the ammo bar lie.
        /// </summary>
        private int MagazineSize => playerLoadout?.Current?.State?.Def?.magazineSize ?? 0;

        /// <summary>True when a weapon source exists and is currently carrying a live weapon.</summary>
        public bool HasWeapon => AmmoSource != null;

        /// <summary>
        /// The stats this ViewModel is currently bound to, or null. Exposed so a bootstrapper can
        /// tell "already bound to this player" from "the player was replaced" without guessing.
        /// </summary>
        public UnitStats StatsSource => playerStats;

        /// <summary>
        /// The organ stats this ViewModel is bound to, or null. Exposed for the same reason as
        /// <see cref="StatsSource"/>: a bootstrapper needs to tell "already bound" from "the player
        /// was replaced" without guessing. Null is legitimate — the budget still binds.
        /// </summary>
        public CharacterStats CharacterStatsSource => characterStats;

        private void Awake()
        {
            _disposables = new CompositeDisposable();
        }

        private void Start()
        {
            // If sources are assigned via Inspector, setup bindings on Start.
            //
            // Rebind, not SetupBindings: the bootstrapper adds this component and then drives
            // Initialize() from its own Update, and Unity does not order Start against that. If
            // Start lands after an Initialize, a bare SetupBindings() would append a second set of
            // subscriptions to _disposables instead of replacing the first — the same
            // double-subscription trap the three HUD views had. Rebind() disposes first, so either
            // order converges on exactly one set. The guard stays so the normal path (sources
            // assigned later, at runtime) does not log "stats is null" as an error.
            if (playerStats != null && HasWeapon)
            {
                Rebind();
            }
        }

        /// <summary>
        /// Re-establish bindings against the current sources.
        ///
        /// <para>Needed because a <see cref="PlayerWeaponLoadout"/> creates its controller in its own
        /// <c>Start()</c>, and Unity does not order <c>Start</c> between components — so a HUD that
        /// bound in <c>Start</c> may have run before the weapon existed. The bootstrapper polls
        /// <c>Current</c> and calls this when the equipped controller changes (including weapon
        /// swaps, where the ammo <c>ReactiveProperty</c> instance itself is replaced).</para>
        /// </summary>
        public void Rebind()
        {
            // Drop the previous subscriptions, then take a fresh set. Without the dispose the old
            // ReactivePropertys would keep firing into a view that no longer reads them.
            _disposables?.Dispose();
            _disposables = new CompositeDisposable();

            _currentAmmo = null;
            _maxAmmo = null;
            _ammoPercent = null;
            _reloadProgress = null;
            _isReloading = null;
            _healthPercent = null;
            _currentHealth = null;
            _maxHealth = null;
            _healthText = null;
            _isAlive = null;
            _currentMana = null;
            _maxMana = null;
            _manaPercent = null;
            _currentManaOrgan = null;
            _maxManaOrgan = null;
            _manaOrganPercent = null;
            _armourPercent = null;
            _hasArmour = null;
            _manaText = null;
            _experiencePercent = null;

            SetupBindings();
        }

        /// <summary>
        /// Wrap <paramref name="source"/> in a <see cref="ReadOnlyReactiveProperty{T}"/> this ViewModel
        /// genuinely <b>owns</b>, so <see cref="Rebind"/> can dispose it without touching the game's data.
        ///
        /// <para><b>The identity <c>Select</c> is load-bearing — do not "simplify" it away.</b> R3's
        /// <c>ToReadOnlyReactiveProperty()</c> short-circuits when the source is already a
        /// <c>ReadOnlyReactiveProperty&lt;T&gt;</c>: it returns <i>the source object itself</i>.
        /// <c>ReactiveProperty&lt;T&gt;</c> derives from that type, so <b>every</b> property this class
        /// binds — <c>UnitStats.CurrentHp</c>/<c>MaxHp</c>/<c>Mana</c>/<c>ArmourIntegrity</c>/<c>HasArmour</c>,
        /// <c>WeaponRuntimeState.CurrentAmmoRP</c>/<c>IsReloadingRP</c>/<c>ReloadProgressRP</c> — takes
        /// that path. Adding the result to <see cref="_disposables"/> therefore hands the <i>game's own</i>
        /// live property to the ViewModel, and <see cref="Rebind"/> (which runs on every weapon swap)
        /// disposes it. The player's vitals and the weapon's state then throw
        /// <c>ObjectDisposedException</c> on their next write, and the HUD wedges permanently because the
        /// rebind that threw is never retried. Measured on 2026-10-03 against R3 1.3.0:
        /// <c>ReferenceEquals(rp, rp.ToReadOnlyReactiveProperty())</c> is <b>true</b>, while
        /// <c>rp.Select(x =&gt; x).ToReadOnlyReactiveProperty()</c> leaves <c>rp</c> writable after the
        /// wrapper is disposed and still tracks it.</para>
        ///
        /// <para>Only the pass-through bindings need this. A binding that goes through an operator first
        /// (<c>Select</c>, <c>CombineLatest</c>, <c>DistinctUntilChanged</c>) already builds a fresh
        /// <c>ConnectedReactiveProperty</c>, so it is safe as written.</para>
        /// </summary>
        private static ReadOnlyReactiveProperty<T> Owned<T>(Observable<T> source)
            => source.Select(x => x).ToReadOnlyReactiveProperty();

        /// <summary>
        /// Set up reactive property bindings from game data sources.
        /// This is where we transform raw game data into UI-friendly reactive properties.
        /// </summary>
        private void SetupBindings()
        {
            if (playerStats == null)
            {
                Debug.LogError("[HUDViewModel] Cannot setup bindings: stats is null!");
                return;
            }

            var ammo = AmmoSource;
            if (ammo == null)
            {
                // Not an error: the loadout equips its starting weapon in Start(), and a HUD built
                // before that legitimately has nothing to show yet. Rebind() will be called when it
                // arrives. Health below still binds, so the player is not left without a HUD.
                Debug.Log("[HUDViewModel] No weapon equipped yet — ammo bindings deferred until Rebind().");
            }
            else
            {
                // Weapon bindings. The pass-throughs go through Owned() so Rebind() cannot dispose the
                // weapon's own state — see Owned.
                _currentAmmo = Owned(ammo);
                _maxAmmo = ammo.Select(_ => MagazineSize).ToReadOnlyReactiveProperty();
                _ammoPercent = ammo.Select(a =>
                {
                    int size = MagazineSize;
                    return size > 0 ? (float)a / size : 0f;
                }).ToReadOnlyReactiveProperty();
                _reloadProgress = Owned(ReloadProgressSource);
                _isReloading = Owned(ReloadingSource);

                _disposables.Add(_currentAmmo);
                _disposables.Add(_maxAmmo);
                _disposables.Add(_ammoPercent);
                _disposables.Add(_reloadProgress);
                _disposables.Add(_isReloading);
            }

            // Stats bindings.
            //
            // Built here rather than using UnitStats.HpPercent: that property allocates a fresh
            // CombineLatest subscription on every read and never disposes it, so reading it once per
            // bind leaks a live subscription each time. Owning it here means Rebind() cleans it up.
            _healthPercent = playerStats.CurrentHp
                .CombineLatest(playerStats.MaxHp, (current, max) => max > 0 ? current / max : 0f)
                .ToReadOnlyReactiveProperty();
            _currentHealth = Owned(playerStats.CurrentHp);
            _maxHealth = Owned(playerStats.MaxHp);
            _isAlive = playerStats.CurrentHp.Select(hp => hp > 0).ToReadOnlyReactiveProperty();

            // The numeric readout fires on EITHER number, not on the health alone — see HealthText.
            // The rule itself lives in HealthReadout so it has an offline fixture; this is only the
            // lifetime, which needs a MonoBehaviour to own it.
            _healthText = HealthReadout.Bind(playerStats.CurrentHp, playerStats.MaxHp);

            _disposables.Add(_healthPercent);
            _disposables.Add(_currentHealth);
            _disposables.Add(_maxHealth);
            _disposables.Add(_isAlive);
            _disposables.Add(_healthText);

            // Mana bindings
            _currentMana = Owned(playerStats.Mana);
            _maxMana = Owned(playerStats.MaxMana);
            _manaPercent = playerStats.Mana.CombineLatest(playerStats.MaxMana, (current, max) =>
                max > 0 ? current / max : 0f
            ).ToReadOnlyReactiveProperty();

            _disposables.Add(_currentMana);
            _disposables.Add(_maxMana);
            _disposables.Add(_manaPercent);

            // The budget readout. Bound to the budget alone — the percentage is a function of that one
            // number, because the ceiling is a constant 1000 (ManaReadout.MaxBudget) rather than a stat.
            _manaText = playerStats.Mana
                .Select(mana => ManaReadout.Format(mana, MagiaLabel, MagiaOverLabel))
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty();

            _disposables.Add(_manaText);

            // Mana ORGAN bindings — the other half of AS3's split readout.
            //
            // Polled with EveryUpdate rather than read off a ReactiveProperty because
            // CharacterStats.manaHp is a plain field: it is a wound track, written by
            // ApplyManaDamage / HealOrgan rather than published as a reactive stream. This is the
            // same shape CharacterStatsViewModel already uses for it, and DistinctUntilChanged keeps
            // the poll from pushing identical frames into the view.
            if (characterStats != null)
            {
                _currentManaOrgan = Observable.EveryUpdate()
                    .Select(_ => characterStats.manaHp)
                    .DistinctUntilChanged()
                    .ToReadOnlyReactiveProperty(characterStats.manaHp);

                _maxManaOrgan = Observable.EveryUpdate()
                    .Select(_ => characterStats.MaxMana)
                    .DistinctUntilChanged()
                    .ToReadOnlyReactiveProperty(characterStats.MaxMana);

                _manaOrganPercent = Observable.EveryUpdate()
                    .Select(_ => characterStats.MaxMana > 0f
                        ? Mathf.Clamp01(characterStats.manaHp / characterStats.MaxMana)
                        : 0f)
                    .DistinctUntilChanged()
                    .ToReadOnlyReactiveProperty();

                _disposables.Add(_currentManaOrgan);
                _disposables.Add(_maxManaOrgan);
                _disposables.Add(_manaOrganPercent);
            }

            // Armour bindings.
            //
            // Taken straight off UnitStats' reactive mirrors rather than computed here, because unlike
            // health there is no second source to combine: ArmourIntegrity is already 0..1 and already
            // published on every equip / wear / repair. Reading `playerStats.armour.IntegrityPercent`
            // instead would be a snapshot with no change signal, so the bar would never move.
            _armourPercent = Owned(playerStats.ArmourIntegrity);
            _hasArmour = Owned(playerStats.HasArmour);

            _disposables.Add(_armourPercent);
            _disposables.Add(_hasArmour);

            // Experience. Read off the character block, which is where the level and the lifetime XP
            // total live (AS3 Pers.xpCur/xpPrev/xpNext). Optional source: without it the bar simply
            // stays empty rather than showing a fake 0 %.
            if (characterStats != null)
            {
                _experiencePercent = Observable.EveryUpdate()
                    .Select(_ => ExperienceFraction())
                    .DistinctUntilChanged()
                    .ToReadOnlyReactiveProperty();

                _disposables.Add(_experiencePercent);
            }

            Debug.Log($"[HUDViewModel] Reactive bindings established (weapon={(ammo != null ? "yes" : "none")}).");
        }

        /// <summary>
        /// AS3 <c>(xpCur - xpPrev) / (xpNext - xpPrev)</c>, with both ends derived from the curve the
        /// way <c>Pers.as:579-580</c> derives them: <c>xpPrev = xpProgress(level - 1)</c>,
        /// <c>xpNext = xpProgress(level)</c>.
        ///
        /// <para><b>The thresholds come from the curve, not from a stored field.</b> The oracle caches
        /// them on the Pers block, but they are a pure function of the level and the curve's
        /// <c>xpDelta</c> — and the port's curve is a ScriptableObject that a difficulty change can
        /// replace. Reading them live means a curve swap moves the bar on the same frame instead of
        /// leaving it pinned to the old progression until the next level-up.</para>
        /// </summary>
        private float ExperienceFraction()
        {
            if (characterStats == null)
            {
                return 0f;
            }

            LevelCurve curve = characterStats.Curve;
            if (curve == null)
            {
                return 0f;
            }

            int level = characterStats.Level;
            int previous = curve.GetXpForLevel(level - 1);
            int next = curve.GetXpForLevel(level);

            return ExperienceReadout.Fraction(characterStats.Xp, previous, next);
        }

        /// <summary>
        /// Update the weapon source (e.g., when switching weapons).
        /// Re-establishes bindings for the new weapon.
        /// </summary>
        /// <param name="newLoadout">New loadout to display</param>
        public void SetLoadout(PlayerWeaponLoadout newLoadout)
        {
            playerLoadout = newLoadout;

            Rebind();
        }

        /// <summary>
        /// Update the stats source (e.g., when switching characters).
        /// Re-establishes bindings for the new stats.
        /// </summary>
        /// <param name="newStats">New stats to display</param>
        public void SetStats(UnitStats newStats)
        {
            playerStats = newStats;

            Rebind();
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
        }

        /// <summary>
        /// Get ammo text formatted for display (e.g., "12 / 12").
        /// </summary>
        /// <returns>Formatted ammo string</returns>
        public string GetAmmoText()
        {
            var ammo = AmmoSource;
            if (ammo == null) return "-- / --";

            return $"{ammo.Value} / {MagazineSize}";
        }

        /// <summary>
        /// Get health text formatted for display (e.g., "85 / 100").
        /// </summary>
        /// <remarks>
        /// A live snapshot, for a caller that has no binding (a test, a tooltip). <b>A view must not
        /// call this from a change handler</b> — that is the pattern that made the maximum lag; bind
        /// <see cref="HealthText"/> instead.
        /// </remarks>
        /// <returns>Formatted health string</returns>
        public string GetHealthText()
        {
            if (playerStats == null) return "-- / --";

            return FormatHealth(playerStats.CurrentHp.Value, playerStats.MaxHp.Value);
        }

        /// <summary>
        /// AS3 <c>GUI.setHp</c>: <c>Math.ceil(hp) + "/" + Math.ceil(maxhp)</c> (<c>GUI.as:944</c>).
        /// </summary>
        /// <remarks>
        /// Ceiling, not rounding: the oracle rounds up so a player on 0.4 hp reads as "1", not as "0" —
        /// a bar at zero with the player still alive is the one reading that looks like a bug.
        /// <para>The rule is <see cref="HealthReadout.Format"/> — moved out of the view model so a shell
        /// fixture can execute it (<c>HealthReadoutFormatTests</c>). This forwarder exists so the
        /// live-snapshot accessor above keeps one definition.</para>
        /// </remarks>
        private static string FormatHealth(float current, float max)
            => HealthReadout.Format(current, max);

        /// <summary>
        /// The HUD's mana readout — AS3 <c>GUI.setMana</c> (<c>GUI.as:985-997</c>).
        ///
        /// <para><b>This is the BUDGET, and it is a percentage — not a "current / max" pair.</b>
        /// AS3 writes <c>txtMagia + " " + round(mana / 10) + "%"</c>; dividing by 10 only yields a
        /// percentage because the ceiling is 1000. Three branches, in order:</para>
        /// <list type="number">
        ///   <item><description><c>mana &lt; 10</c> → the "out of magic" label.</description></item>
        ///   <item><description><c>mana &lt; 995</c> (or a spell is cooling down) → the percentage.</description></item>
        ///   <item><description>otherwise → empty. A full pool shows nothing at all.</description></item>
        /// </list>
        ///
        /// <para><b>The labels now come from the real table.</b> AS3 reads <c>txtMagia</c> /
        /// <c>txtMagiaOver</c> through <c>Res.guiText</c>, and the port <i>does</i> ship that table
        /// (<c>Resources/Localisation/text_en.xml</c>, resolved by <see cref="PfeTextTableProvider"/> —
        /// it has carried <c>magia</c> = "Magic" all along). An earlier revision of this method returned
        /// the raw keys on the belief that no GUI table existed, which put the literal string
        /// <c>magia 34%</c> on the HUD.</para>
        ///
        /// <para><b>Not ported, and deliberately not invented:</b> the spell-cooldown term in branch 2,
        /// and the two <c>appendText</c> suffixes (the held prop's kg, and "heavy" when
        /// <c>dmana &lt; -5</c>). None of those affects which pool is read, which is the point of this
        /// method.</para>
        /// </summary>
        /// <returns>Formatted mana string</returns>
        public string GetManaText()
        {
            if (playerStats == null) return string.Empty;

            return ManaReadout.Format(playerStats.Mana.Value, MagiaLabel, MagiaOverLabel);
        }

        // AS3 reads these through Res.guiText("magia") / Res.guiText("magiaover"); both rows exist in
        // the shipped document, so they are resolved rather than echoed. The fallbacks are the keys
        // themselves — a missing table then reads as "magia 34%", which is obviously a lookup miss
        // rather than a plausible-looking English word.
        private const string MagiaKey = "magia";
        private const string MagiaOverKey = "magiaover";

        private static string MagiaLabel => ResolveGuiText(MagiaKey);
        private static string MagiaOverLabel => ResolveGuiText(MagiaOverKey);

        private static string ResolveGuiText(string key)
        {
            string text = PfeTextTableProvider.Shared.Gui(key);
            return string.IsNullOrEmpty(text) || text.StartsWith("*", System.StringComparison.Ordinal)
                ? key
                : text;
        }
    }
}
