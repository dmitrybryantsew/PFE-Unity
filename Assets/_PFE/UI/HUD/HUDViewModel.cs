using UnityEngine;
using R3;
using PFE.Systems.Weapons;
using PFE.Entities.Units;
using PFE.Systems.RPG;

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
        private ReadOnlyReactiveProperty<bool> _isAlive;
        private ReadOnlyReactiveProperty<float> _currentMana;
        private ReadOnlyReactiveProperty<float> _maxMana;
        private ReadOnlyReactiveProperty<float> _manaPercent;
        private ReadOnlyReactiveProperty<float> _currentManaOrgan;
        private ReadOnlyReactiveProperty<float> _maxManaOrgan;
        private ReadOnlyReactiveProperty<float> _manaOrganPercent;
        private ReadOnlyReactiveProperty<float> _armourPercent;
        private ReadOnlyReactiveProperty<bool> _hasArmour;

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
        /// (<c>GUI.as:1000</c>), while the adjacent text shows the budget. Both are in
        /// <c>setMana</c>, four lines apart.
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
            _isAlive = null;
            _currentMana = null;
            _maxMana = null;
            _manaPercent = null;
            _currentManaOrgan = null;
            _maxManaOrgan = null;
            _manaOrganPercent = null;
            _armourPercent = null;
            _hasArmour = null;

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

            _disposables.Add(_healthPercent);
            _disposables.Add(_currentHealth);
            _disposables.Add(_maxHealth);
            _disposables.Add(_isAlive);

            // Mana bindings
            _currentMana = Owned(playerStats.Mana);
            _maxMana = Owned(playerStats.MaxMana);
            _manaPercent = playerStats.Mana.CombineLatest(playerStats.MaxMana, (current, max) =>
                max > 0 ? current / max : 0f
            ).ToReadOnlyReactiveProperty();

            _disposables.Add(_currentMana);
            _disposables.Add(_maxMana);
            _disposables.Add(_manaPercent);

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

            Debug.Log($"[HUDViewModel] Reactive bindings established (weapon={(ammo != null ? "yes" : "none")}).");
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
        /// <returns>Formatted health string</returns>
        public string GetHealthText()
        {
            if (playerStats == null) return "-- / --";

            float current = playerStats.CurrentHp.Value;
            float max = playerStats.MaxHp.Value;

            return $"{Mathf.Round(current)} / {Mathf.Round(max)}";
        }

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
        /// <para><b>Not ported, and deliberately not invented:</b> AS3's <c>txtMagia</c> /
        /// <c>txtMagiaOver</c> come from <c>Res.guiText</c>, and this port has no GUI-string table,
        /// so the keys are returned literally (see the two constants below) rather than translated.
        /// Also absent: the spell-cooldown term in branch 2, and the two <c>appendText</c> suffixes
        /// (the held prop's kg, and "heavy" when <c>dmana &lt; -5</c>). None of those affects which
        /// pool is read, which is the point of this method.</para>
        /// </summary>
        /// <returns>Formatted mana string</returns>
        public string GetManaText()
        {
            if (playerStats == null) return string.Empty;

            float mana = playerStats.Mana.Value;

            if (mana < 10f)
            {
                return MagiaOverLabel;
            }

            if (mana < 995f)
            {
                return $"{MagiaLabel} {Mathf.Round(mana / 10f):0}%";
            }

            return string.Empty;
        }

        // AS3 reads these through Res.guiText("magia") / Res.guiText("magiaover"). The port has no
        // GUI-string table, so the keys stand in for the text. They are constants rather than inline
        // literals so that a later localisation pass has exactly one place to change -- and so a
        // reviewer sees a placeholder rather than a suspiciously English-looking label.
        private const string MagiaLabel = "magia";
        private const string MagiaOverLabel = "magiaover";
    }
}
