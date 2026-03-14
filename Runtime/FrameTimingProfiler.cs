// Copyright (c) 2024 Ocean View Games Ltd | https://oceanviewgames.co.uk
// Licensed under the MIT Licence. See LICENCE file in the project root for full licence text.

using System;
using UnityEngine;

namespace OceanViewGames.Performance
{
    /// <summary>
    /// Defines the target frame rate used for budget calculations.
    /// </summary>
    public enum TargetFrameRate
    {
        /// <summary>60 FPS — 16.67 ms budget per frame.</summary>
        Sixty = 60,

        /// <summary>30 FPS — 33.33 ms budget per frame.</summary>
        Thirty = 30
    }

    /// <summary>
    /// Immutable summary of frame timing statistics over the most recent rolling window.
    /// </summary>
    public readonly struct FrameTimingSummary
    {
        /// <summary>Average frame time in milliseconds.</summary>
        public float AverageFrameTimeMs { get; }

        /// <summary>95th percentile frame time in milliseconds.</summary>
        public float Percentile95Ms { get; }

        /// <summary>99th percentile frame time in milliseconds.</summary>
        public float Percentile99Ms { get; }

        /// <summary>Percentage of frames that completed within the target budget (0–100).</summary>
        public float PercentageOnTarget { get; }

        /// <summary>Number of spike frames (exceeding 2× the target budget) in the window.</summary>
        public int SpikeCount { get; }

        /// <summary>Total number of samples currently in the rolling window.</summary>
        public int SampleCount { get; }

        /// <summary>Target frame time budget in milliseconds.</summary>
        public float TargetBudgetMs { get; }

        /// <summary>
        /// Creates a new <see cref="FrameTimingSummary"/>.
        /// </summary>
        public FrameTimingSummary(
            float averageFrameTimeMs,
            float percentile95Ms,
            float percentile99Ms,
            float percentageOnTarget,
            int spikeCount,
            int sampleCount,
            float targetBudgetMs)
        {
            AverageFrameTimeMs = averageFrameTimeMs;
            Percentile95Ms = percentile95Ms;
            Percentile99Ms = percentile99Ms;
            PercentageOnTarget = percentageOnTarget;
            SpikeCount = spikeCount;
            SampleCount = sampleCount;
            TargetBudgetMs = targetBudgetMs;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"Avg {AverageFrameTimeMs:F2} ms | P95 {Percentile95Ms:F2} ms | P99 {Percentile99Ms:F2} ms | " +
            $"On-target {PercentageOnTarget:F1}% | Spikes {SpikeCount} | Samples {SampleCount}";
    }

    /// <summary>
    /// Lightweight frame timing profiler that tracks per-frame durations over a
    /// configurable rolling window with zero per-frame heap allocations.
    /// Attach to any <see cref="GameObject"/> to begin profiling automatically.
    /// </summary>
    [AddComponentMenu("Ocean View Games/Performance/Frame Timing Profiler")]
    public sealed class FrameTimingProfiler : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Number of frames held in the rolling window. Larger values give smoother averages but use more memory.")]
        [Range(30, 1000)]
        private int _windowSize = 120;

        [SerializeField]
        [Tooltip("Target frame rate used to calculate the per-frame budget.")]
        private TargetFrameRate _targetFrameRate = TargetFrameRate.Sixty;

        [SerializeField]
        [Tooltip("Log a warning to the console whenever a spike frame is detected.")]
        private bool _logSpikes = true;

        // Pre-allocated circular buffer — no per-frame allocations.
        private float[] _frameTimes;

        // Scratch array used for percentile sorting so we never allocate during queries.
        private float[] _sortBuffer;

        private int _writeIndex;
        private int _sampleCount;
        private float _targetBudgetMs;

        /// <summary>
        /// The current rolling-window size.
        /// </summary>
        public int WindowSize => _windowSize;

        /// <summary>
        /// The per-frame budget in milliseconds derived from <see cref="TargetFrameRate"/>.
        /// </summary>
        public float TargetBudgetMs => _targetBudgetMs;

        /// <summary>
        /// Number of samples currently stored in the rolling window.
        /// </summary>
        public int SampleCount => _sampleCount;

        /// <summary>
        /// Average frame time in milliseconds over the current rolling window.
        /// Returns 0 if no samples have been recorded.
        /// </summary>
        public float AverageFrameTimeMs
        {
            get
            {
                if (_sampleCount == 0) return 0f;
                float sum = 0f;
                for (int i = 0; i < _sampleCount; i++)
                    sum += _frameTimes[i];
                return sum / _sampleCount;
            }
        }

        /// <summary>
        /// Worst (highest) frame time in milliseconds in the current rolling window.
        /// </summary>
        public float WorstFrameTimeMs
        {
            get
            {
                if (_sampleCount == 0) return 0f;
                float max = 0f;
                for (int i = 0; i < _sampleCount; i++)
                    if (_frameTimes[i] > max) max = _frameTimes[i];
                return max;
            }
        }

        /// <summary>
        /// Best (lowest) frame time in milliseconds in the current rolling window.
        /// </summary>
        public float BestFrameTimeMs
        {
            get
            {
                if (_sampleCount == 0) return 0f;
                float min = float.MaxValue;
                for (int i = 0; i < _sampleCount; i++)
                    if (_frameTimes[i] < min) min = _frameTimes[i];
                return min;
            }
        }

        /// <summary>
        /// Current frames per second based on the most recent frame time.
        /// </summary>
        public float CurrentFps => _sampleCount > 0 && _frameTimes[(_writeIndex - 1 + _windowSize) % _windowSize] > 0f
            ? 1000f / _frameTimes[(_writeIndex - 1 + _windowSize) % _windowSize]
            : 0f;

        /// <summary>
        /// Total number of spike frames detected since the last <see cref="Clear"/> or <see cref="Initialise"/>.
        /// </summary>
        public int SpikeCount
        {
            get
            {
                int count = 0;
                float spikeBudget = _targetBudgetMs * 2f;
                for (int i = 0; i < _sampleCount; i++)
                    if (_frameTimes[i] > spikeBudget) count++;
                return count;
            }
        }

        private void Awake()
        {
            Initialise();
        }

        private void Update()
        {
            float frameTimeMs = Time.unscaledDeltaTime * 1000f;
            RecordSample(frameTimeMs);

            if (_logSpikes && frameTimeMs > _targetBudgetMs * 2f)
            {
                Debug.LogWarning(
                    $"[FrameTimingProfiler] Spike detected: {frameTimeMs:F2} ms " +
                    $"(budget {_targetBudgetMs:F2} ms, frame {Time.frameCount})",
                    this);
            }
        }

        /// <summary>
        /// Re-initialises the profiler, clearing all recorded samples and
        /// re-allocating buffers if the window size has changed.
        /// </summary>
        public void Initialise()
        {
            _targetBudgetMs = 1000f / (int)_targetFrameRate;

            if (_frameTimes is null || _frameTimes.Length != _windowSize)
            {
                _frameTimes = new float[_windowSize];
                _sortBuffer = new float[_windowSize];
            }
            else
            {
                Array.Clear(_frameTimes, 0, _frameTimes.Length);
            }

            _writeIndex = 0;
            _sampleCount = 0;
        }

        /// <summary>
        /// Generates a <see cref="FrameTimingSummary"/> from the current rolling window.
        /// This performs an in-place copy and sort on a pre-allocated scratch buffer
        /// so it does not allocate on the managed heap.
        /// </summary>
        /// <returns>A snapshot of the current frame timing statistics.</returns>
        public FrameTimingSummary GetSummary()
        {
            int count = _sampleCount;
            if (count == 0)
            {
                return new FrameTimingSummary(0f, 0f, 0f, 100f, 0, 0, _targetBudgetMs);
            }

            // Copy active samples into the scratch buffer for sorting.
            float sum = 0f;
            int onTarget = 0;
            int spikes = 0;
            float spikeBudget = _targetBudgetMs * 2f;

            for (int i = 0; i < count; i++)
            {
                float t = _frameTimes[i];
                _sortBuffer[i] = t;
                sum += t;

                if (t <= _targetBudgetMs)
                    onTarget++;
                if (t > spikeBudget)
                    spikes++;
            }

            Array.Sort(_sortBuffer, 0, count);

            float avg = sum / count;
            float p95 = GetPercentile(count, 0.95f);
            float p99 = GetPercentile(count, 0.99f);
            float pct = onTarget / (float)count * 100f;

            return new FrameTimingSummary(avg, p95, p99, pct, spikes, count, _targetBudgetMs);
        }

        /// <summary>
        /// Clears all recorded samples without reallocating buffers.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_frameTimes, 0, _frameTimes.Length);
            _writeIndex = 0;
            _sampleCount = 0;
        }

        /// <summary>
        /// Changes the target frame rate at runtime and recalculates the budget.
        /// Existing samples are preserved.
        /// </summary>
        /// <param name="target">The new target frame rate.</param>
        public void SetTargetFrameRate(TargetFrameRate target)
        {
            _targetFrameRate = target;
            _targetBudgetMs = 1000f / (int)_targetFrameRate;
        }

        // Records a single frame time into the circular buffer.
        private void RecordSample(float frameTimeMs)
        {
            _frameTimes[_writeIndex] = frameTimeMs;
            _writeIndex = (_writeIndex + 1) % _windowSize;

            if (_sampleCount < _windowSize)
                _sampleCount++;
        }

        // Reads a percentile value from the pre-sorted scratch buffer.
        private float GetPercentile(int count, float percentile)
        {
            if (count <= 1)
                return _sortBuffer[0];

            float rank = percentile * (count - 1);
            int lower = (int)rank;
            int upper = lower + 1;
            float fraction = rank - lower;

            if (upper >= count)
                return _sortBuffer[count - 1];

            return _sortBuffer[lower] + ((_sortBuffer[upper] - _sortBuffer[lower]) * fraction);
        }
    }
}
