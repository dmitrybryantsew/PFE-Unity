using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// Base brain for boss encounters.
    /// Manages phase transitions, HP threshold events, arena bounds, and custom encounter state machines.
    /// </summary>
    public abstract class BossBrain : EnemyBrain
    {
        [SerializeField] protected int _phaseIndex = 1;
        [SerializeField] protected Rect _arenaBounds;
        [SerializeField] protected bool _hasArenaBounds;

        protected float _maxHp;
        protected int _quarterHpEventsTriggered;
        protected bool _isSecondLifeActive;

        public int PhaseIndex => _phaseIndex;
        public bool IsSecondLifeActive => _isSecondLifeActive;
        public Rect ArenaBounds => _arenaBounds;
        public bool HasArenaBounds => _hasArenaBounds;

        public event Action<int> OnPhaseChanged;
        public event Action<int> OnQuarterHpThreshold;

        public float CurrentHpFraction
        {
            get
            {
                if (_controller != null && _maxHp > 0f)
                {
                    return Mathf.Clamp01(_controller.CurrentHealth / _maxHp);
                }
                return 1f;
            }
        }

        protected override void Awake()
        {
            base.Awake();
            InitializeBossStats();
        }

        public override void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            base.Initialize(tileQuery, simLoop);
            InitializeBossStats();
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            InitializeBossStats();
        }

        protected virtual void InitializeBossStats()
        {
            if (_controller != null && _controller.Definition != null)
            {
                _maxHp = _controller.Definition.health > 0 ? _controller.Definition.health : 2000f;
            }
        }

        public virtual void SetArenaBounds(Rect bounds)
        {
            _arenaBounds = bounds;
            _hasArenaBounds = true;
        }

        public virtual void SetPhase(int newPhase)
        {
            if (_phaseIndex != newPhase)
            {
                _phaseIndex = newPhase;
                OnPhaseChanged?.Invoke(_phaseIndex);
            }
        }

        public override void SimTick(int tickIndex)
        {
            CheckQuarterHpThresholds();
            base.SimTick(tickIndex);
            ClampToArenaBounds();
        }

        protected virtual void CheckQuarterHpThresholds()
        {
            if (_controller == null || !_controller.IsAlive || _maxHp <= 0f) return;

            float missingFrac = 1f - CurrentHpFraction;
            int quartersPassed = Mathf.FloorToInt(missingFrac * 4f);

            while (_quarterHpEventsTriggered < quartersPassed && _quarterHpEventsTriggered < 4)
            {
                _quarterHpEventsTriggered++;
                OnQuarterHpPassed(_quarterHpEventsTriggered);
                RaiseQuarterHpThreshold(_quarterHpEventsTriggered);
            }
        }

        /// <summary>
        /// Raises <see cref="OnQuarterHpThreshold"/> for a subclass that owns its own latch loop.
        ///
        /// <para><b>Why this exists.</b> A C# event can only be raised from the type that declares it,
        /// so an override of <see cref="CheckQuarterHpThresholds"/> — which
        /// <c>BossRaiderBrain</c> needs, to claim the quarter that a killing blow crosses — could not
        /// fire the event without this. The alternative was a second event declared on the subclass,
        /// which would leave two subscriptions that a caller has to know to attach both of.</para>
        /// </summary>
        protected void RaiseQuarterHpThreshold(int quarter) => OnQuarterHpThreshold?.Invoke(quarter);

        protected virtual void OnQuarterHpPassed(int quarter)
        {
        }

        protected virtual void ClampToArenaBounds()
        {
            if (!_hasArenaBounds || _controller == null) return;

            Vector3 pos = transform.position;
            // Note: Bounds stored in pixel or unit coordinates
            float clampedX = Mathf.Clamp(pos.x, _arenaBounds.xMin, _arenaBounds.xMax);
            float clampedY = Mathf.Clamp(pos.y, _arenaBounds.yMin, _arenaBounds.yMax);

            if (pos.x != clampedX || pos.y != clampedY)
            {
                transform.position = new Vector3(clampedX, clampedY, pos.z);
            }
        }

        /// <summary>
        /// Handles death or revival for multi-stage bosses.
        /// Returns true if the boss revived into a subsequent phase.
        /// </summary>
        public virtual bool TryReviveSecondLife()
        {
            return false;
        }

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;
            if (!_controller.IsAlive) return "die";
            return "stay";
        }
    }
}
