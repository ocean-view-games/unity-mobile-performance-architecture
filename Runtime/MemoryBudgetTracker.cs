// Copyright (c) 2024 Ocean View Games Ltd | https://oceanviewgames.co.uk
// Licensed under the MIT Licence. See LICENCE file in the project root for full licence text.

using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Profiling;

namespace OceanViewGames.Performance
{
    /// <summary>
    /// Tracks Unity managed and native memory allocation against configurable budgets.
    /// Fires warnings at configurable thresholds and can force cleanup when over budget.
    /// </summary>
    [AddComponentMenu("Ocean View Games/Performance/Memory Budget Tracker")]
    public sealed class MemoryBudgetTracker : MonoBehaviour
    {
        /// <summary>
        /// Preset memory budget tiers for different platform capabilities.
        /// </summary>
        public enum BudgetPreset
        {
            /// <summary>Low-end Android devices — 512 MB budget.</summary>
            LowEndAndroid = 512,
            /// <summary>Mid-range Android devices — 1024 MB budget.</summary>
            MidRangeAndroid = 1024,
            /// <summary>Standard iOS devices — 2048 MB budget.</summary>
            StandardIOS = 2048,
            /// <summary>Custom budget defined via <see cref="customBudgetMB"/>.</summary>
            Custom = 0
        }

        /// <summary>
        /// Breakdown of memory allocation across subsystems.
        /// </summary>
        public readonly struct AllocationBreakdown
        {
            /// <summary>Managed heap allocation in megabytes.</summary>
            public float ManagedHeapMB { get; init; }

            /// <summary>Native engine allocation in megabytes.</summary>
            public float NativeMemoryMB { get; init; }

            /// <summary>Graphics and GPU driver allocation in megabytes.</summary>
            public float GraphicsMemoryMB { get; init; }

            /// <summary>Total allocation across all subsystems in megabytes.</summary>
            public float TotalMB { get; init; }
        }

        [Header("Budget Configuration")]

        [SerializeField, Tooltip("Select a platform preset or choose Custom to set a manual budget.")]
        private BudgetPreset budgetPreset = BudgetPreset.MidRangeAndroid;

        [SerializeField, Tooltip("Custom memory budget in megabytes. Only used when Budget Preset is set to Custom.")]
        private float customBudgetMB = 1024f;

        [Header("Warning Thresholds")]

        [SerializeField, Tooltip("First warning threshold as a fraction of the total budget (0–1)."), Range(0f, 1f)]
        private float warningThreshold = 0.75f;

        [SerializeField, Tooltip("Critical warning threshold as a fraction of the total budget (0–1)."), Range(0f, 1f)]
        private float criticalThreshold = 0.90f;

        [Header("Polling")]

        [SerializeField, Tooltip("How often (in seconds) to poll memory usage. Lower values increase precision but cost more CPU.")]
        private float pollingInterval = 1f;

        [SerializeField, Tooltip("Automatically unload unused assets and collect garbage when over budget.")]
        private bool autoCleanupOnOverBudget;

        [Header("Events")]

        [SerializeField, Tooltip("Raised when memory usage exceeds the warning threshold.")]
        private UnityEvent onWarningThresholdReached = new();

        [SerializeField, Tooltip("Raised when memory usage exceeds the critical threshold.")]
        private UnityEvent onCriticalThresholdReached = new();

        [SerializeField, Tooltip("Raised when memory usage exceeds the total budget.")]
        private UnityEvent onOverBudget = new();

        // Cached state to avoid per-frame allocations.
        private float _nextPollTime;
        private bool _warningFired;
        private bool _criticalFired;
        private bool _overBudgetFired;
        private float _cachedUsageMB;
        private AllocationBreakdown _cachedBreakdown;

        private const float BytesToMB = 1f / (1024f * 1024f);

        /// <summary>
        /// The effective memory budget in megabytes, resolved from the active preset or custom value.
        /// </summary>
        public float EffectiveBudgetMB => budgetPreset == BudgetPreset.Custom
            ? customBudgetMB
            : (float)budgetPreset;

        /// <summary>
        /// Normalised memory usage as a fraction of the budget (0 to 1+).
        /// Values above 1 indicate the budget has been exceeded.
        /// </summary>
        public float NormalisedMemoryUsage => EffectiveBudgetMB > 0f
            ? _cachedUsageMB / EffectiveBudgetMB
            : 0f;

        private void OnEnable()
        {
            _nextPollTime = 0f;
            ResetThresholdFlags();
        }

        private void Update()
        {
            // Zero-allocation hot path: simple float comparison each frame.
            if (Time.unscaledTime < _nextPollTime)
                return;

            _nextPollTime = Time.unscaledTime + pollingInterval;
            PollMemory();
        }

        private void PollMemory()
        {
            _cachedBreakdown = SampleBreakdown();
            _cachedUsageMB = _cachedBreakdown.TotalMB;

            float budget = EffectiveBudgetMB;
            if (budget <= 0f)
                return;

            float ratio = _cachedUsageMB / budget;

            if (ratio >= 1f && !_overBudgetFired)
            {
                _overBudgetFired = true;
                onOverBudget?.Invoke();

                if (autoCleanupOnOverBudget)
                    ForceCleanup();
            }

            if (ratio >= criticalThreshold && !_criticalFired)
            {
                _criticalFired = true;
                onCriticalThresholdReached?.Invoke();
            }

            if (ratio >= warningThreshold && !_warningFired)
            {
                _warningFired = true;
                onWarningThresholdReached?.Invoke();
            }

            // Reset flags when usage drops back below thresholds so they can fire again.
            if (ratio < warningThreshold)
                ResetThresholdFlags();
        }

        /// <summary>
        /// Returns the current total memory usage in megabytes.
        /// </summary>
        /// <returns>Total allocated memory in megabytes.</returns>
        public float GetCurrentUsageMB() => _cachedUsageMB;

        /// <summary>
        /// Returns how many megabytes remain before the budget is exhausted.
        /// A negative value indicates the budget has been exceeded.
        /// </summary>
        /// <returns>Remaining budget in megabytes.</returns>
        public float GetBudgetRemainingMB() => EffectiveBudgetMB - _cachedUsageMB;

        /// <summary>
        /// Checks whether the current memory usage exceeds the configured budget.
        /// </summary>
        /// <returns><c>true</c> if usage is at or above the budget; otherwise <c>false</c>.</returns>
        public bool IsOverBudget() => _cachedUsageMB >= EffectiveBudgetMB;

        /// <summary>
        /// Returns a detailed breakdown of memory allocation across subsystems.
        /// Values are taken from the most recent polling sample.
        /// </summary>
        /// <returns>An <see cref="AllocationBreakdown"/> with current figures.</returns>
        public AllocationBreakdown GetAllocationBreakdown() => _cachedBreakdown;

        /// <summary>
        /// Forces an immediate unload of unused assets followed by a full garbage collection.
        /// Use sparingly as this can cause hitches.
        /// </summary>
        public void ForceCleanup()
        {
            Resources.UnloadUnusedAssets();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>
        /// Applies a <see cref="BudgetPreset"/> at runtime, updating the effective budget.
        /// </summary>
        /// <param name="preset">The preset to apply.</param>
        public void SetBudgetPreset(BudgetPreset preset)
        {
            budgetPreset = preset;
            ResetThresholdFlags();
        }

        /// <summary>
        /// Sets a custom memory budget in megabytes and switches the preset to
        /// <see cref="BudgetPreset.Custom"/>.
        /// </summary>
        /// <param name="megabytes">The desired budget in megabytes.</param>
        public void SetCustomBudget(float megabytes)
        {
            budgetPreset = BudgetPreset.Custom;
            customBudgetMB = Mathf.Max(0f, megabytes);
            ResetThresholdFlags();
        }

        private static AllocationBreakdown SampleBreakdown()
        {
            float managed = Profiler.GetMonoUsedSizeLong() * BytesToMB;
            float native = Profiler.GetTotalAllocatedMemoryLong() * BytesToMB;
            float graphics = Profiler.GetAllocatedMemoryForGraphicsDriver() * BytesToMB;

            return new AllocationBreakdown
            {
                ManagedHeapMB = managed,
                NativeMemoryMB = native,
                GraphicsMemoryMB = graphics,
                TotalMB = managed + native + graphics
            };
        }

        private void ResetThresholdFlags()
        {
            _warningFired = false;
            _criticalFired = false;
            _overBudgetFired = false;
        }
    }
}
