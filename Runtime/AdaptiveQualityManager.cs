// Copyright (c) 2024 Ocean View Games Ltd | https://oceanviewgames.co.uk
// Licensed under the MIT Licence. See LICENCE file in the project root for full licence text.

using System;
using UnityEngine;
using UnityEngine.Events;

namespace OceanViewGames.Performance
{
    /// <summary>
    /// Quality tier representing a discrete level of graphical fidelity.
    /// </summary>
    public enum QualityTier
    {
        Ultra   = 0,
        High    = 1,
        Medium  = 2,
        Low     = 3,
        Minimal = 4
    }

    /// <summary>
    /// Serialisable per-tier quality configuration.
    /// </summary>
    [Serializable]
    public struct QualityTierSettings
    {
        /// <summary>Texture resolution level (0 = full, 1 = half, 2 = quarter, 3 = eighth).</summary>
        [Tooltip("Texture resolution level (0 = full, 1 = half, 2 = quarter, 3 = eighth).")]
        public int textureResolutionLevel;

        /// <summary>Maximum shadow draw distance in metres.</summary>
        [Tooltip("Maximum shadow draw distance in metres.")]
        public float shadowDistance;

        /// <summary>LOD bias multiplier. Higher values favour higher-detail models.</summary>
        [Tooltip("LOD bias multiplier. Higher values favour higher-detail models.")]
        public float lodBias;

        /// <summary>Global particle count multiplier (0-1).</summary>
        [Tooltip("Global particle count multiplier (0-1).")]
        [Range(0f, 1f)]
        public float particleCountMultiplier;

        /// <summary>Target frame rate (-1 for unlimited).</summary>
        [Tooltip("Target frame rate (-1 for unlimited).")]
        public int targetFrameRate;
    }

    /// <summary>
    /// Event payload raised when the active quality tier changes.
    /// </summary>
    [Serializable]
    public class QualityTierChangedEvent : UnityEvent<QualityTier, QualityTier> { }

    /// <summary>
    /// Monitors thermal, memory and frame-timing data and automatically adjusts
    /// quality settings to maintain smooth performance on mobile devices.
    /// Implements hysteresis to prevent rapid tier oscillation.
    /// </summary>
    [AddComponentMenu("Ocean View Games/Performance/Adaptive Quality Manager")]
    public sealed class AdaptiveQualityManager : MonoBehaviour
    {
        #region Serialised Fields

        [Header("Data Sources")]

        /// <summary>Reference to the thermal throttle monitor.</summary>
        [SerializeField]
        [Tooltip("Reference to the ThermalThrottleMonitor that supplies device thermal state.")]
        private ThermalThrottleMonitor thermalMonitor;

        /// <summary>Reference to the memory budget tracker.</summary>
        [SerializeField]
        [Tooltip("Reference to the MemoryBudgetTracker that supplies memory pressure data.")]
        private MemoryBudgetTracker memoryTracker;

        /// <summary>Reference to the frame timing profiler.</summary>
        [SerializeField]
        [Tooltip("Reference to the FrameTimingProfiler that supplies frame-time statistics.")]
        private FrameTimingProfiler frameProfiler;

        [Header("Tier Configuration")]

        /// <summary>Settings applied at each quality tier.</summary>
        [SerializeField]
        [Tooltip("Quality settings for each tier, indexed by QualityTier enum value.")]
        private QualityTierSettings[] tierSettings = new QualityTierSettings[5]
        {
            new() { textureResolutionLevel = 0, shadowDistance = 150f, lodBias = 2.0f, particleCountMultiplier = 1.0f, targetFrameRate = 60 },
            new() { textureResolutionLevel = 0, shadowDistance = 100f, lodBias = 1.5f, particleCountMultiplier = 0.8f, targetFrameRate = 60 },
            new() { textureResolutionLevel = 1, shadowDistance = 60f,  lodBias = 1.0f, particleCountMultiplier = 0.5f, targetFrameRate = 30 },
            new() { textureResolutionLevel = 2, shadowDistance = 30f,  lodBias = 0.7f, particleCountMultiplier = 0.3f, targetFrameRate = 30 },
            new() { textureResolutionLevel = 3, shadowDistance = 10f,  lodBias = 0.4f, particleCountMultiplier = 0.1f, targetFrameRate = 30 },
        };

        [Header("Hysteresis")]

        /// <summary>Seconds the system must remain stable before stepping up a tier.</summary>
        [SerializeField]
        [Tooltip("Seconds the system must remain stable at the current tier before stepping up.")]
        [Range(1f, 30f)]
        private float hysteresisUpSeconds = 10f;

        [Header("Thresholds")]

        /// <summary>Frame time in milliseconds above which quality is reduced.</summary>
        [SerializeField]
        [Tooltip("Frame time in milliseconds above which quality is reduced.")]
        private float frameTimeDegradeThresholdMs = 18f;

        /// <summary>Frame time in milliseconds below which quality may be increased.</summary>
        [SerializeField]
        [Tooltip("Frame time in milliseconds below which quality may be increased.")]
        private float frameTimeUpgradeThresholdMs = 12f;

        /// <summary>Thermal level (0-1) above which quality is reduced.</summary>
        [SerializeField]
        [Tooltip("Normalised thermal level (0-1) above which quality is reduced.")]
        [Range(0f, 1f)]
        private float thermalDegradeThreshold = 0.7f;

        /// <summary>Memory usage ratio (0-1) above which quality is reduced.</summary>
        [SerializeField]
        [Tooltip("Normalised memory usage ratio (0-1) above which quality is reduced.")]
        [Range(0f, 1f)]
        private float memoryDegradeThreshold = 0.85f;

        [Header("Events")]

        /// <summary>Raised when the quality tier changes. Supplies (previous, new) tiers.</summary>
        [SerializeField]
        [Tooltip("Raised when the quality tier changes. Arguments: previous tier, new tier.")]
        private QualityTierChangedEvent onQualityTierChanged = new();

        #endregion

        #region Public API

        /// <summary>The currently active quality tier.</summary>
        public QualityTier CurrentTier => currentTier;

        /// <summary>Seconds elapsed since the last tier change.</summary>
        public float TimeSinceLastChange => timeSinceLastChange;

        /// <summary>Event raised when the quality tier changes.</summary>
        public QualityTierChangedEvent OnQualityTierChanged => onQualityTierChanged;

        /// <summary>
        /// Forces an immediate transition to the specified quality tier,
        /// resetting the hysteresis timer.
        /// </summary>
        /// <param name="tier">The tier to transition to.</param>
        public void ForceSetTier(QualityTier tier)
        {
            QualityTier previous = currentTier;
            currentTier = tier;
            timeSinceLastChange = 0f;
            ApplyTierSettings(currentTier);

            if (previous != currentTier)
            {
                onQualityTierChanged.Invoke(previous, currentTier);
            }
        }

        #endregion

        #region Private State

        private QualityTier currentTier = QualityTier.High;
        private float timeSinceLastChange;
        private const int TierCount = 5;

        #endregion

        #region MonoBehaviour

        private void Start()
        {
            ApplyTierSettings(currentTier);
        }

        private void Update()
        {
            timeSinceLastChange += Time.unscaledDeltaTime;
            EvaluateAndAdapt();
        }

        #endregion

        #region Core Logic

        private void EvaluateAndAdapt()
        {
            bool shouldDegrade = ShouldDegrade();
            bool shouldUpgrade = ShouldUpgrade();

            if (shouldDegrade)
            {
                StepDown();
                return;
            }

            if (shouldUpgrade && timeSinceLastChange >= hysteresisUpSeconds)
            {
                StepUp();
            }
        }

        private bool ShouldDegrade()
        {
            if (frameProfiler != null && frameProfiler.AverageFrameTimeMs > frameTimeDegradeThresholdMs)
                return true;

            if (thermalMonitor != null && thermalMonitor.NormalisedThermalLevel > thermalDegradeThreshold)
                return true;

            if (memoryTracker != null && memoryTracker.NormalisedMemoryUsage > memoryDegradeThreshold)
                return true;

            return false;
        }

        private bool ShouldUpgrade()
        {
            if (frameProfiler != null && frameProfiler.AverageFrameTimeMs > frameTimeUpgradeThresholdMs)
                return false;

            if (thermalMonitor != null && thermalMonitor.NormalisedThermalLevel > thermalDegradeThreshold * 0.6f)
                return false;

            if (memoryTracker != null && memoryTracker.NormalisedMemoryUsage > memoryDegradeThreshold * 0.8f)
                return false;

            return true;
        }

        private void StepDown()
        {
            int next = (int)currentTier + 1;
            if (next >= TierCount) return;

            QualityTier previous = currentTier;
            currentTier = (QualityTier)next;
            timeSinceLastChange = 0f;
            ApplyTierSettings(currentTier);
            onQualityTierChanged.Invoke(previous, currentTier);
        }

        private void StepUp()
        {
            int next = (int)currentTier - 1;
            if (next < 0) return;

            QualityTier previous = currentTier;
            currentTier = (QualityTier)next;
            timeSinceLastChange = 0f;
            ApplyTierSettings(currentTier);
            onQualityTierChanged.Invoke(previous, currentTier);
        }

        private void ApplyTierSettings(QualityTier tier)
        {
            int index = (int)tier;
            if (index < 0 || index >= tierSettings.Length) return;

            ref readonly QualityTierSettings settings = ref tierSettings[index];

            QualitySettings.globalTextureMipmapLimit = settings.textureResolutionLevel;
            QualitySettings.shadowDistance = settings.shadowDistance;
            QualitySettings.lodBias = settings.lodBias;
            Application.targetFrameRate = settings.targetFrameRate;

            // Particle count multiplier is exposed for external systems to query
            // via CurrentTier and the tier settings array; no global Unity API exists.
        }

        #endregion
    }
}
