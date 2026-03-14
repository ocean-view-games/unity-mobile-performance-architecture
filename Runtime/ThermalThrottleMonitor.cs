// ---------------------------------------------------------
// ThermalThrottleMonitor.cs
//
// MIT Licence
// Copyright (c) 2024 Ocean View Games Ltd | https://oceanviewgames.co.uk
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicence, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject
// to the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS
// BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN
// ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
// ---------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.Events;

namespace OceanViewGames.Performance
{
    /// <summary>
    /// Severity levels representing the device's thermal state.
    /// </summary>
    public enum ThermalSeverity
    {
        /// <summary>Device temperature is within normal operating range.</summary>
        Nominal = 0,
        /// <summary>Device is slightly warm; consider minimising background work.</summary>
        Fair = 1,
        /// <summary>Device is hot; reduce workload to avoid throttling.</summary>
        Serious = 2,
        /// <summary>Device is critically hot; immediate workload reduction required.</summary>
        Critical = 3
    }

    /// <summary>
    /// Monitors the device thermal state and raises events when transitions occur.
    /// <para>
    /// On <b>iOS</b> this maps directly to <c>NSProcessInfo.thermalState</c> via
    /// <c>Application.thermalState</c> (Unity 2022.1+). On <b>Android</b> the
    /// underlying implementation uses <c>PowerManager.getThermalHeadroom()</c>
    /// where available (API 30+). For older Unity versions or unsupported
    /// platforms the monitor falls back to <see cref="ThermalSeverity.Nominal"/>.
    /// </para>
    /// </summary>
    [AddComponentMenu("Ocean View Games/Performance/Thermal Throttle Monitor")]
    public sealed class ThermalThrottleMonitor : MonoBehaviour
    {
        // ------------------------------------------------------------
        // Serialised fields
        // ------------------------------------------------------------

        [SerializeField, Tooltip("Interval in seconds between thermal state polls. " +
            "Lower values give faster response but cost more CPU time.")]
        private float _pollInterval = 1.0f;

        [SerializeField, Tooltip("Event raised whenever the thermal severity level changes. " +
            "The new severity is passed as the argument.")]
        private UnityEvent<ThermalSeverity> _onThermalStateChanged = new();

        [SerializeField, Tooltip("Event raised when the device enters a throttling state " +
            "(Serious or Critical).")]
        private UnityEvent _onThrottlingBegan = new();

        [SerializeField, Tooltip("Event raised when the device leaves a throttling state " +
            "and returns to Nominal or Fair.")]
        private UnityEvent _onThrottlingEnded = new();

        [SerializeField, Tooltip("Enable debug logging of thermal state transitions to the console.")]
        private bool _enableLogging = true;

        // ------------------------------------------------------------
        // Private state — zero per-frame allocations
        // ------------------------------------------------------------

        private ThermalSeverity _currentLevel = ThermalSeverity.Nominal;
        private float _timeSinceLastPoll;
        private float _lastThrottleEventTime;
        private bool _wasThrottling;

        // Cached to avoid repeated string allocations in logs.
        private static readonly string[] SeverityNames =
        {
            nameof(ThermalSeverity.Nominal),
            nameof(ThermalSeverity.Fair),
            nameof(ThermalSeverity.Serious),
            nameof(ThermalSeverity.Critical)
        };

        // ------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------

        /// <summary>
        /// The most recently observed thermal severity level.
        /// </summary>
        public ThermalSeverity CurrentThermalLevel => _currentLevel;

        /// <summary>
        /// Seconds elapsed since the last thermal state transition, or
        /// <c>-1</c> if no transition has occurred since the monitor was enabled.
        /// </summary>
        public float TimeSinceLastThrottleEvent =>
            _lastThrottleEventTime < 0f ? -1f : Time.time - _lastThrottleEventTime;

        /// <summary>
        /// <c>true</c> when the current thermal level is
        /// <see cref="ThermalSeverity.Serious"/> or higher, indicating the
        /// device is actively throttling performance.
        /// </summary>
        public bool IsThrottling => _currentLevel >= ThermalSeverity.Serious;

        /// <summary>
        /// Normalised thermal level in the range 0 to 1, where 0 is
        /// <see cref="ThermalSeverity.Nominal"/> and 1 is
        /// <see cref="ThermalSeverity.Critical"/>. Useful for threshold
        /// comparisons in systems like <see cref="AdaptiveQualityManager"/>.
        /// </summary>
        public float NormalisedThermalLevel => (float)_currentLevel / 3f;

        /// <summary>
        /// Event raised whenever the thermal severity level changes.
        /// </summary>
        public UnityEvent<ThermalSeverity> OnThermalStateChanged => _onThermalStateChanged;

        /// <summary>
        /// Event raised when the device begins throttling (enters Serious or Critical).
        /// </summary>
        public UnityEvent OnThrottlingBegan => _onThrottlingBegan;

        /// <summary>
        /// Event raised when the device stops throttling (returns to Nominal or Fair).
        /// </summary>
        public UnityEvent OnThrottlingEnded => _onThrottlingEnded;

        // ------------------------------------------------------------
        // MonoBehaviour lifecycle
        // ------------------------------------------------------------

        private void OnEnable()
        {
            _lastThrottleEventTime = -1f;
            _timeSinceLastPoll = _pollInterval; // Force immediate first poll.
            _wasThrottling = false;
            _currentLevel = PollThermalState();
        }

        private void Update()
        {
            _timeSinceLastPoll += Time.deltaTime;

            if (_timeSinceLastPoll < _pollInterval)
                return;

            _timeSinceLastPoll = 0f;

            ThermalSeverity newLevel = PollThermalState();

            if (newLevel == _currentLevel)
                return;

            ThermalSeverity previous = _currentLevel;
            _currentLevel = newLevel;
            _lastThrottleEventTime = Time.time;

            if (_enableLogging)
            {
                // Uses cached severity names — no allocations beyond the
                // initial format string (unavoidable for Debug.Log).
                Debug.Log($"[ThermalThrottleMonitor] Thermal transition: " +
                    $"{SeverityNames[(int)previous]} -> {SeverityNames[(int)newLevel]} " +
                    $"at {Time.time:F2}s");
            }

            _onThermalStateChanged.Invoke(newLevel);

            bool throttlingNow = newLevel >= ThermalSeverity.Serious;

            if (throttlingNow && !_wasThrottling)
                _onThrottlingBegan.Invoke();
            else if (!throttlingNow && _wasThrottling)
                _onThrottlingEnded.Invoke();

            _wasThrottling = throttlingNow;
        }

        // ------------------------------------------------------------
        // Platform thermal state polling
        // ------------------------------------------------------------

        /// <summary>
        /// Reads the current thermal state from the platform.
        /// </summary>
        private static ThermalSeverity PollThermalState()
        {
            // Unity 2022.1+ exposes Application.thermalState which maps to
            // iOS NSProcessInfo.thermalState and Android
            // PowerManager.getThermalHeadroom().
#if UNITY_2022_1_OR_NEWER
            return MapUnityThermalState(Application.thermalState);
#else
            // Prior to Unity 2022.1 there is no built-in thermal API.
            // Platform-specific plugins or native bridges would be needed
            // to retrieve thermal information. Default to Nominal so that
            // gameplay is not unnecessarily degraded.
            return ThermalSeverity.Nominal;
#endif
        }

#if UNITY_2022_1_OR_NEWER
        /// <summary>
        /// Maps Unity's <see cref="ApplicationThermalState"/> to our
        /// <see cref="ThermalSeverity"/> enum.
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        ///   <item><b>iOS</b> — values correspond directly to
        ///   <c>ProcessInfo.ThermalState</c> (.nominal, .fair, .serious, .critical).</item>
        ///   <item><b>Android</b> — Unity internally maps
        ///   <c>PowerManager.THERMAL_STATUS_*</c> constants to these four
        ///   buckets. Note that finer-grained Android statuses (Light,
        ///   Moderate, Severe, Emergency, Shutdown) are collapsed.</item>
        /// </list>
        /// </remarks>
        private static ThermalSeverity MapUnityThermalState(ApplicationThermalState state) =>
            state switch
            {
                ApplicationThermalState.Nominal  => ThermalSeverity.Nominal,
                ApplicationThermalState.Fair     => ThermalSeverity.Fair,
                ApplicationThermalState.Serious  => ThermalSeverity.Serious,
                ApplicationThermalState.Critical => ThermalSeverity.Critical,
                _ => ThermalSeverity.Nominal
            };
#endif
    }
}
