// ---------------------------------------------------------
// Battery Aware Scheduler
// Copyright (c) 2024 Ocean View Games Ltd | https://oceanviewgames.co.uk
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ---------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace OceanViewGames.Performance
{
    /// <summary>
    /// Schedules heavy work (asset preloading, procedural generation, etc.)
    /// only when the device battery is above a configurable threshold or the
    /// device is currently charging. Queued callbacks are checked periodically
    /// and dispatched when conditions are met.
    /// </summary>
    [AddComponentMenu("Ocean View Games/Performance/Battery Aware Scheduler")]
    public sealed class BatteryAwareScheduler : MonoBehaviour
    {
        // -------------------------------------------------------
        // Serialised Fields
        // -------------------------------------------------------

        [SerializeField]
        [Tooltip("Minimum battery percentage (0-100) required to run heavy work when the device is not charging.")]
        [Range(0f, 100f)]
        private float _minimumBatteryPercent = 25f;

        [SerializeField]
        [Tooltip("How often, in seconds, the scheduler checks whether queued work can be dispatched.")]
        [Min(0.1f)]
        private float _pollIntervalSeconds = 2f;

        [SerializeField]
        [Tooltip("Maximum number of queued callbacks to dispatch per poll tick. Zero means unlimited.")]
        [Min(0)]
        private int _maxDispatchesPerTick;

        [SerializeField]
        [Tooltip("When enabled, heavy work is always permitted while the device is charging regardless of battery level.")]
        private bool _alwaysAllowWhenCharging = true;

        // -------------------------------------------------------
        // Private State
        // -------------------------------------------------------

        private readonly Queue<Action> _pendingCallbacks = new();
        private float _timeSinceLastPoll;

        // Cached values refreshed each poll to avoid per-frame queries.
        private float _cachedBatteryPercent;
        private bool _cachedIsCharging;

        // -------------------------------------------------------
        // Public Properties
        // -------------------------------------------------------

        /// <summary>
        /// Minimum battery percentage (0-100) required to permit heavy work
        /// when the device is not charging.
        /// </summary>
        public float MinimumBatteryPercent
        {
            get => _minimumBatteryPercent;
            set => _minimumBatteryPercent = Mathf.Clamp(value, 0f, 100f);
        }

        /// <summary>
        /// Number of callbacks currently waiting to be dispatched.
        /// </summary>
        public int PendingCount => _pendingCallbacks.Count;

        // -------------------------------------------------------
        // Unity Lifecycle
        // -------------------------------------------------------

        private void Awake()
        {
            RefreshBatteryState();
        }

        private void Update()
        {
            _timeSinceLastPoll += Time.deltaTime;

            if (_timeSinceLastPoll < _pollIntervalSeconds)
                return;

            _timeSinceLastPoll = 0f;

            RefreshBatteryState();
            ProcessQueue();
        }

        // -------------------------------------------------------
        // Public API
        // -------------------------------------------------------

        /// <summary>
        /// Returns <c>true</c> if the current battery state permits heavy
        /// work — either the battery is above the configured threshold or the
        /// device is charging (when <see cref="_alwaysAllowWhenCharging"/> is
        /// enabled).
        /// </summary>
        /// <returns><c>true</c> when heavy work is permitted.</returns>
        public bool CanRunHeavyWork()
        {
            // Use cached values so callers can query cheaply between polls.
            if (_alwaysAllowWhenCharging && _cachedIsCharging)
                return true;

            return _cachedBatteryPercent >= _minimumBatteryPercent;
        }

        /// <summary>
        /// Returns the current battery level as a percentage (0-100).
        /// On platforms that do not report battery information this returns
        /// <c>100</c> as a safe default.
        /// </summary>
        /// <returns>Battery percentage in the range 0-100.</returns>
        public float GetBatteryPercent()
        {
            return _cachedBatteryPercent;
        }

        /// <summary>
        /// Returns <c>true</c> when the device is connected to a power
        /// source and actively charging.
        /// </summary>
        /// <returns><c>true</c> if the device is charging.</returns>
        public bool IsCharging()
        {
            return _cachedIsCharging;
        }

        /// <summary>
        /// Queues a callback to be executed the next time battery conditions
        /// allow heavy work. If conditions are already met the callback is
        /// invoked immediately during the next poll tick.
        /// </summary>
        /// <param name="callback">
        /// The action to invoke when conditions are favourable. Must not be
        /// <c>null</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="callback"/> is <c>null</c>.
        /// </exception>
        public void ScheduleWhenSafe(Action callback)
        {
            if (callback is null)
                throw new ArgumentNullException(nameof(callback));

            _pendingCallbacks.Enqueue(callback);
        }

        /// <summary>
        /// Removes all pending callbacks from the queue without invoking them.
        /// </summary>
        public void ClearPending()
        {
            _pendingCallbacks.Clear();
        }

        /// <summary>
        /// Forces an immediate refresh of the cached battery state. Useful
        /// after the application resumes from the background.
        /// </summary>
        public void ForceRefresh()
        {
            RefreshBatteryState();
        }

        // -------------------------------------------------------
        // Private Helpers
        // -------------------------------------------------------

        private void RefreshBatteryState()
        {
#if UNITY_ANDROID || UNITY_IOS
            float raw = SystemInfo.batteryLevel;

            // SystemInfo.batteryLevel returns -1 when the value is unknown.
            _cachedBatteryPercent = raw >= 0f ? raw * 100f : 100f;

            BatteryStatus status = SystemInfo.batteryStatus;
            _cachedIsCharging = status is BatteryStatus.Charging or BatteryStatus.Full;
#else
            // In the Editor and on platforms without battery info, assume
            // mains power so heavy work is never blocked during development.
            _cachedBatteryPercent = 100f;
            _cachedIsCharging = true;
#endif
        }

        private void ProcessQueue()
        {
            if (_pendingCallbacks.Count == 0)
                return;

            if (!CanRunHeavyWork())
                return;

            int dispatched = 0;
            bool unlimited = _maxDispatchesPerTick <= 0;

            while (_pendingCallbacks.Count > 0 && (unlimited || dispatched < _maxDispatchesPerTick))
            {
                Action callback = _pendingCallbacks.Dequeue();

                try
                {
                    callback.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, this);
                }

                dispatched++;
            }
        }
    }
}
