// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEngine;

namespace Gsplat.Editor
{
    [CustomEditor(typeof(GsplatSequencePlayer))]
    public class GsplatSequencePlayerEditor : UnityEditor.Editor
    {
        SerializedProperty m_usePreloadedAssets;
        SerializedProperty m_preloadedFrames;
        SerializedProperty m_plyFolderPath;
        SerializedProperty m_fileNamePattern;
        SerializedProperty m_startFrame;
        SerializedProperty m_endFrame;
        SerializedProperty m_frameRate;
        SerializedProperty m_loop;
        SerializedProperty m_playOnStart;
        SerializedProperty m_reverse;
        SerializedProperty m_bufferSize;
        SerializedProperty m_asyncLoading;
        SerializedProperty m_shDegree;
        SerializedProperty m_gammaToLinear;

        void OnEnable()
        {
            m_usePreloadedAssets = serializedObject.FindProperty("UsePreloadedAssets");
            m_preloadedFrames = serializedObject.FindProperty("PreloadedFrames");
            m_plyFolderPath = serializedObject.FindProperty("PlyFolderPath");
            m_fileNamePattern = serializedObject.FindProperty("FileNamePattern");
            m_startFrame = serializedObject.FindProperty("StartFrame");
            m_endFrame = serializedObject.FindProperty("EndFrame");
            m_frameRate = serializedObject.FindProperty("FrameRate");
            m_loop = serializedObject.FindProperty("Loop");
            m_playOnStart = serializedObject.FindProperty("PlayOnStart");
            m_reverse = serializedObject.FindProperty("Reverse");
            m_bufferSize = serializedObject.FindProperty("BufferSize");
            m_asyncLoading = serializedObject.FindProperty("AsyncLoading");
            m_shDegree = serializedObject.FindProperty("SHDegree");
            m_gammaToLinear = serializedObject.FindProperty("GammaToLinear");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var player = (GsplatSequencePlayer)target;

            // Sequence Source
            EditorGUILayout.LabelField("Sequence Source", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_usePreloadedAssets);

            if (m_usePreloadedAssets.boolValue)
            {
                EditorGUILayout.PropertyField(m_preloadedFrames, true);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(m_plyFolderPath);
                if (GUILayout.Button("Browse", GUILayout.Width(60)))
                {
                    string path = EditorUtility.OpenFolderPanel("Select PLY Sequence Folder", m_plyFolderPath.stringValue, "");
                    if (!string.IsNullOrEmpty(path))
                    {
                        m_plyFolderPath.stringValue = path;
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.PropertyField(m_fileNamePattern);
                EditorGUILayout.HelpBox(
                    "Use {0} for frame number. Examples:\n" +
                    "  frame_{0:D4}.ply -> frame_0000.ply, frame_0001.ply\n" +
                    "  {0}.ply -> 0.ply, 1.ply\n" +
                    "  splat_{0:D3}.ply -> splat_000.ply, splat_001.ply",
                    MessageType.Info);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(m_startFrame);
                EditorGUILayout.PropertyField(m_endFrame);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.PropertyField(m_asyncLoading);
                if (m_asyncLoading.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(m_bufferSize);
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.Space();

            // Playback Settings
            EditorGUILayout.LabelField("Playback Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_frameRate);
            EditorGUILayout.PropertyField(m_loop);
            EditorGUILayout.PropertyField(m_playOnStart);
            EditorGUILayout.PropertyField(m_reverse);

            EditorGUILayout.Space();

            // Rendering Settings
            EditorGUILayout.LabelField("Rendering Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_shDegree);
            EditorGUILayout.PropertyField(m_gammaToLinear);

            EditorGUILayout.Space();

            // Playback Controls (Runtime only)
            EditorGUILayout.LabelField("Playback Controls", EditorStyles.boldLabel);

            if (Application.isPlaying)
            {
                // Status
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Status:", GUILayout.Width(60));
                EditorGUILayout.LabelField(player.IsPlaying ? "Playing" : "Stopped");
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Frame:", GUILayout.Width(60));
                EditorGUILayout.LabelField($"{player.CurrentFrame + 1} / {player.TotalFrames}");
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Time:", GUILayout.Width(60));
                EditorGUILayout.LabelField($"{player.CurrentTime:F2}s / {player.Duration:F2}s");
                EditorGUILayout.EndHorizontal();

                if (!m_usePreloadedAssets.boolValue)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Buffered:", GUILayout.Width(60));
                    EditorGUILayout.LabelField($"{player.LoadedFrameCount} frames");
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.Space();

                // Timeline slider
                EditorGUI.BeginChangeCheck();
                int newFrame = EditorGUILayout.IntSlider("Frame", player.CurrentFrame, 0, player.TotalFrames - 1);
                if (EditorGUI.EndChangeCheck())
                {
                    player.SetFrame(newFrame);
                }

                EditorGUILayout.Space();

                // Control buttons
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(player.IsPlaying ? "Pause" : "Play"))
                {
                    if (player.IsPlaying)
                        player.Pause();
                    else
                        player.Play();
                }
                if (GUILayout.Button("Stop"))
                {
                    player.Stop();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("<<"))
                {
                    player.SetFrame(player.CurrentFrame - 10);
                }
                if (GUILayout.Button("<"))
                {
                    player.SetFrame(player.CurrentFrame - 1);
                }
                if (GUILayout.Button(">"))
                {
                    player.SetFrame(player.CurrentFrame + 1);
                }
                if (GUILayout.Button(">>"))
                {
                    player.SetFrame(player.CurrentFrame + 10);
                }
                EditorGUILayout.EndHorizontal();

                if (!m_usePreloadedAssets.boolValue)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Preload All Frames"))
                    {
                        _ = player.PreloadAllFrames();
                    }
                    if (GUILayout.Button("Clear Buffer"))
                    {
                        player.ClearBuffer();
                    }
                    EditorGUILayout.EndHorizontal();
                }

                // Force repaint during playback
                if (player.IsPlaying)
                {
                    Repaint();
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Enter Play mode to control playback.", MessageType.Info);

                // Show preview info
                int totalFrames = m_usePreloadedAssets.boolValue
                    ? (m_preloadedFrames.arraySize)
                    : (m_endFrame.intValue - m_startFrame.intValue + 1);

                float duration = totalFrames / Mathf.Max(m_frameRate.floatValue, 0.001f);

                EditorGUILayout.LabelField($"Total Frames: {totalFrames}");
                EditorGUILayout.LabelField($"Duration: {duration:F2}s");

                if (!m_usePreloadedAssets.boolValue && !string.IsNullOrEmpty(m_plyFolderPath.stringValue))
                {
                    string firstFrame = System.IO.Path.Combine(m_plyFolderPath.stringValue,
                        string.Format(m_fileNamePattern.stringValue, m_startFrame.intValue));
                    string lastFrame = System.IO.Path.Combine(m_plyFolderPath.stringValue,
                        string.Format(m_fileNamePattern.stringValue, m_endFrame.intValue));

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("File paths preview:", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"First: {firstFrame}", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"Last: {lastFrame}", EditorStyles.miniLabel);

                    if (GUILayout.Button("Validate First Frame"))
                    {
                        if (System.IO.File.Exists(firstFrame))
                        {
                            EditorUtility.DisplayDialog("Validation", $"First frame found:\n{firstFrame}", "OK");
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Validation", $"First frame NOT found:\n{firstFrame}", "OK");
                        }
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
