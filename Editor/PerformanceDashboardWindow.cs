// Copyright (c) 2024 Ocean View Games Ltd | https://oceanviewgames.co.uk
// Licensed under the MIT Licence. See LICENCE file in the project root for full licence text.

#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OceanViewGames.Performance.Editor
{
    /// <summary>
    /// Editor window that displays real-time performance data during Play mode,
    /// including thermal state, memory usage, frame timing and quality tier.
    /// </summary>
    public sealed class PerformanceDashboardWindow : EditorWindow
    {
        private ThermalThrottleMonitor thermalMonitor;
        private MemoryBudgetTracker memoryTracker;
        private FrameTimingProfiler frameTimingProfiler;
        private AdaptiveQualityManager qualityManager;

        private Vector2 scrollPosition;
        private double nextRepaintTime;
        private const double RepaintIntervalSeconds = 0.25;

        /// <summary>
        /// Opens the Performance Dashboard window from the menu bar.
        /// </summary>
        [MenuItem("Ocean View Games/Performance Dashboard")]
        public static void ShowWindow()
        {
            var window = GetWindow<PerformanceDashboardWindow>();
            window.titleContent = new GUIContent("Performance Dashboard");
            window.minSize = new Vector2(360f, 300f);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                FindSceneComponents();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                ClearReferences();
            }
        }

        private void Update()
        {
            if (!EditorApplication.isPlaying) return;

            if (EditorApplication.timeSinceStartup >= nextRepaintTime)
            {
                nextRepaintTime = EditorApplication.timeSinceStartup + RepaintIntervalSeconds;
                Repaint();
            }
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            DrawHeader();

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play mode to view live performance data.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            EnsureComponentsCached();

            EditorGUILayout.Space(4f);
            DrawThermalSection();
            EditorGUILayout.Space(4f);
            DrawMemorySection();
            EditorGUILayout.Space(4f);
            DrawFrameTimingSection();
            EditorGUILayout.Space(4f);
            DrawQualityTierSection();
            EditorGUILayout.Space(8f);
            DrawExportButton();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Performance Dashboard", EditorStyles.boldLabel);
            EditorGUILayout.Space(2f);
        }

        private void DrawThermalSection()
        {
            EditorGUILayout.LabelField("Thermal State", EditorStyles.boldLabel);

            if (thermalMonitor == null)
            {
                EditorGUILayout.HelpBox(
                    "ThermalThrottleMonitor not found in the scene.", MessageType.Warning);
                return;
            }

            string state = thermalMonitor.CurrentThermalLevel.ToString();
            Color colour = GetThermalColour(thermalMonitor.CurrentThermalLevel);
            Color previousColour = GUI.contentColor;
            GUI.contentColor = colour;
            EditorGUILayout.LabelField("State:", state, EditorStyles.largeLabel);
            GUI.contentColor = previousColour;
        }

        private void DrawMemorySection()
        {
            EditorGUILayout.LabelField("Memory Budget", EditorStyles.boldLabel);

            if (memoryTracker == null)
            {
                EditorGUILayout.HelpBox(
                    "MemoryBudgetTracker not found in the scene.", MessageType.Warning);
                return;
            }

            float usageMb = memoryTracker.GetCurrentUsageMB();
            float budgetMb = memoryTracker.EffectiveBudgetMB;
            float fraction = budgetMb > 0f ? Mathf.Clamp01(usageMb / budgetMb) : 0f;

            string label = $"{usageMb:F1} MB / {budgetMb:F1} MB ({fraction * 100f:F0}%)";
            Rect rect = GUILayoutUtility.GetRect(18f, 22f, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(rect, fraction, label);
        }

        private void DrawFrameTimingSection()
        {
            EditorGUILayout.LabelField("Frame Timing", EditorStyles.boldLabel);

            if (frameTimingProfiler == null)
            {
                EditorGUILayout.HelpBox(
                    "FrameTimingProfiler not found in the scene.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField("Average Frame Time:",
                $"{frameTimingProfiler.AverageFrameTimeMs:F2} ms");
            EditorGUILayout.LabelField("Worst Frame Time:",
                $"{frameTimingProfiler.WorstFrameTimeMs:F2} ms");
            EditorGUILayout.LabelField("Best Frame Time:",
                $"{frameTimingProfiler.BestFrameTimeMs:F2} ms");
            EditorGUILayout.LabelField("Current FPS:",
                $"{frameTimingProfiler.CurrentFps:F1}");
        }

        private void DrawQualityTierSection()
        {
            EditorGUILayout.LabelField("Adaptive Quality", EditorStyles.boldLabel);

            if (qualityManager == null)
            {
                EditorGUILayout.HelpBox(
                    "AdaptiveQualityManager not found in the scene.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField("Current Tier:",
                qualityManager.CurrentTier.ToString());
        }

        private void DrawExportButton()
        {
            if (GUILayout.Button("Export Performance Snapshot as JSON"))
            {
                ExportSnapshot();
            }
        }

        /// <summary>
        /// Exports a JSON snapshot of the current performance data to a user-selected file.
        /// </summary>
        public void ExportSnapshot()
        {
            string path = EditorUtility.SaveFilePanel(
                "Export Performance Snapshot", "", "performance_snapshot", "json");

            if (string.IsNullOrEmpty(path)) return;

            var snapshot = new PerformanceSnapshot
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                thermalState = thermalMonitor != null
                    ? thermalMonitor.CurrentThermalLevel.ToString() : "N/A",
                memoryUsageMb = memoryTracker != null ? memoryTracker.GetCurrentUsageMB() : 0f,
                memoryBudgetMb = memoryTracker != null ? memoryTracker.EffectiveBudgetMB : 0f,
                averageFrameTimeMs = frameTimingProfiler != null
                    ? frameTimingProfiler.AverageFrameTimeMs : 0f,
                worstFrameTimeMs = frameTimingProfiler != null
                    ? frameTimingProfiler.WorstFrameTimeMs : 0f,
                bestFrameTimeMs = frameTimingProfiler != null
                    ? frameTimingProfiler.BestFrameTimeMs : 0f,
                currentFps = frameTimingProfiler != null
                    ? frameTimingProfiler.CurrentFps : 0f,
                qualityTier = qualityManager != null
                    ? qualityManager.CurrentTier.ToString() : "N/A"
            };

            string json = JsonUtility.ToJson(snapshot, true);
            File.WriteAllText(path, json);
            Debug.Log($"Performance snapshot exported to: {path}");
        }

        private void FindSceneComponents()
        {
            thermalMonitor = FindObjectOfType<ThermalThrottleMonitor>();
            memoryTracker = FindObjectOfType<MemoryBudgetTracker>();
            frameTimingProfiler = FindObjectOfType<FrameTimingProfiler>();
            qualityManager = FindObjectOfType<AdaptiveQualityManager>();
        }

        private void ClearReferences()
        {
            thermalMonitor = null;
            memoryTracker = null;
            frameTimingProfiler = null;
            qualityManager = null;
        }

        private void EnsureComponentsCached()
        {
            if (thermalMonitor == null || memoryTracker == null
                || frameTimingProfiler == null || qualityManager == null)
            {
                FindSceneComponents();
            }
        }

        private static Color GetThermalColour(ThermalSeverity state)
        {
            switch (state)
            {
                case ThermalSeverity.Nominal:  return Color.green;
                case ThermalSeverity.Fair:     return Color.yellow;
                case ThermalSeverity.Serious:  return new Color(1f, 0.5f, 0f);
                case ThermalSeverity.Critical: return Color.red;
                default:                       return Color.white;
            }
        }

        [Serializable]
        private struct PerformanceSnapshot
        {
            public string timestamp;
            public string thermalState;
            public float memoryUsageMb;
            public float memoryBudgetMb;
            public float averageFrameTimeMs;
            public float worstFrameTimeMs;
            public float bestFrameTimeMs;
            public float currentFps;
            public string qualityTier;
        }
    }
}
#endif
