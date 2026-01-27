// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// Component for playing sequences of PLY files as Gaussian Splatting animations.
    /// Supports both preloaded GsplatAsset sequences and runtime-loaded PLY file sequences.
    /// </summary>
    [ExecuteAlways]
    public class GsplatSequencePlayer : MonoBehaviour, IGsplat
    {
        [Header("Sequence Source")]
        [Tooltip("Use preloaded GsplatAsset array instead of runtime PLY loading")]
        public bool UsePreloadedAssets;

        [Tooltip("Preloaded GsplatAsset frames (used when UsePreloadedAssets is true)")]
        public GsplatAsset[] PreloadedFrames;

        [Tooltip("Folder containing PLY sequence files (used when UsePreloadedAssets is false)")]
        public string PlyFolderPath;

        [Tooltip("File name pattern, use {0} for frame number (e.g., 'frame_{0:D4}.ply')")]
        public string FileNamePattern = "frame_{0:D4}.ply";

        [Tooltip("Starting frame number")]
        public int StartFrame;

        [Tooltip("Ending frame number (inclusive)")]
        public int EndFrame = 899;

        [Header("Playback Settings")]
        [Tooltip("Frames per second")]
        public float FrameRate = 30f;

        [Tooltip("Loop playback")]
        public bool Loop = true;

        [Tooltip("Play automatically on start")]
        public bool PlayOnStart = true;

        [Tooltip("Reverse playback direction")]
        public bool Reverse;

        [Header("Buffer Settings")]
        [Tooltip("Number of frames to buffer ahead")]
        public int BufferSize = 10;

        [Tooltip("Load frames asynchronously in background")]
        public bool AsyncLoading = true;

        [Header("Interpolation Settings")]
        [Tooltip("Enable frame interpolation for smoother playback")]
        public bool EnableInterpolation = true;

        [Header("Rendering Settings")]
        [Range(0, 3)]
        public int SHDegree = 3;

        public bool GammaToLinear;

        [Header("Debug Display")]
        [Tooltip("Show frame info on screen")]
        public bool ShowFrameInfo;

        [Tooltip("Position of the frame info display")]
        public TextAnchor DisplayPosition = TextAnchor.UpperLeft;

        // Playback state
        bool m_isPlaying;
        float m_currentTime;
        int m_currentFrameIndex;
        int m_displayedFrameIndex = -1;
        int m_displayedNextFrameIndex = -1;
        float m_interpolationFactor;

        // Frame buffer
        Dictionary<int, GsplatPlyLoader.FrameData> m_frameBuffer = new Dictionary<int, GsplatPlyLoader.FrameData>();
        HashSet<int> m_loadingFrames = new HashSet<int>();

        // Renderer (standard mode)
        GsplatRendererImpl m_renderer;

        // Renderer (interpolation mode)
        GsplatRendererImplInterp m_rendererInterp;

        uint m_currentSplatCount;
        byte m_currentSHBands;
        Bounds m_currentBounds;

        // Properties
        public bool IsPlaying => m_isPlaying;
        public int CurrentFrame => m_currentFrameIndex;
        public int TotalFrames => UsePreloadedAssets ? PreloadedFrames?.Length ?? 0 : EndFrame - StartFrame + 1;
        public float Duration => TotalFrames / Mathf.Max(FrameRate, 0.001f);
        public float CurrentTime => m_currentTime;
        public float InterpolationFactor => m_interpolationFactor;

        // IGsplat implementation
        public bool Valid => m_currentSplatCount > 0;
        public uint SplatCount => m_currentSplatCount;
        public ISorterResource SorterResource => EnableInterpolation ? m_rendererInterp?.SorterResource : m_renderer?.SorterResource;

        void OnEnable()
        {
            GsplatSorter.Instance.RegisterGsplat(this);

            if (PlayOnStart && Application.isPlaying)
                Play();
        }

        void OnDisable()
        {
            GsplatSorter.Instance.UnregisterGsplat(this);
            m_renderer?.Dispose();
            m_renderer = null;
            m_rendererInterp?.Dispose();
            m_rendererInterp = null;
            m_frameBuffer.Clear();
            m_loadingFrames.Clear();
        }

        void Update()
        {
            if (m_isPlaying && Application.isPlaying)
            {
                UpdatePlayback();
            }

            if (EnableInterpolation)
            {
                UpdateFrameInterpolated();
                RenderCurrentFrameInterpolated();
            }
            else
            {
                UpdateFrame();
                RenderCurrentFrame();
            }
        }

        void UpdatePlayback()
        {
            float deltaTime = Time.deltaTime;
            if (Reverse) deltaTime = -deltaTime;

            m_currentTime += deltaTime;

            int totalFrames = TotalFrames;
            float frameDuration = 1f / Mathf.Max(FrameRate, 0.001f);

            // Calculate frame index and interpolation factor
            float exactFrame = m_currentTime / frameDuration;
            m_currentFrameIndex = Mathf.FloorToInt(exactFrame);
            m_interpolationFactor = exactFrame - m_currentFrameIndex;

            if (Loop)
            {
                if (m_currentFrameIndex >= totalFrames)
                {
                    m_currentFrameIndex = 0;
                    m_currentTime = m_currentTime % (totalFrames * frameDuration);
                    exactFrame = m_currentTime / frameDuration;
                    m_interpolationFactor = exactFrame - m_currentFrameIndex;
                }
                else if (m_currentFrameIndex < 0)
                {
                    m_currentFrameIndex = totalFrames - 1;
                    m_currentTime = (totalFrames - 1) * frameDuration;
                    m_interpolationFactor = 0;
                }
            }
            else
            {
                if (m_currentFrameIndex >= totalFrames)
                {
                    m_currentFrameIndex = totalFrames - 1;
                    m_interpolationFactor = 0;
                    m_isPlaying = false;
                }
                else if (m_currentFrameIndex < 0)
                {
                    m_currentFrameIndex = 0;
                    m_interpolationFactor = 0;
                    m_isPlaying = false;
                }
            }

            // Buffer ahead frames
            if (AsyncLoading && !UsePreloadedAssets)
            {
                BufferFrames();
            }
        }

        void BufferFrames()
        {
            int totalFrames = TotalFrames;
            int direction = Reverse ? -1 : 1;

            // When interpolation is enabled, always ensure next frame is also loaded
            int extraFrames = EnableInterpolation ? 1 : 0;

            for (int i = 0; i < BufferSize + extraFrames; i++)
            {
                int frameToLoad = m_currentFrameIndex + i * direction;
                if (Loop)
                {
                    frameToLoad = ((frameToLoad % totalFrames) + totalFrames) % totalFrames;
                }
                else if (frameToLoad < 0 || frameToLoad >= totalFrames)
                {
                    continue;
                }

                if (!m_frameBuffer.ContainsKey(frameToLoad) && !m_loadingFrames.Contains(frameToLoad))
                {
                    LoadFrameAsync(frameToLoad);
                }
            }

            // Clean up old frames
            var framesToRemove = m_frameBuffer.Keys
                .Where(f => Mathf.Abs(f - m_currentFrameIndex) > BufferSize * 2)
                .ToList();
            foreach (var f in framesToRemove)
            {
                m_frameBuffer.Remove(f);
            }
        }

        async void LoadFrameAsync(int frameIndex)
        {
            m_loadingFrames.Add(frameIndex);

            try
            {
                string path = GetFramePath(frameIndex);
                var frameData = await GsplatPlyLoader.LoadAsync(path);
                if (frameData != null && this != null)
                {
                    m_frameBuffer[frameIndex] = frameData;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading frame {frameIndex}: {e.Message}");
            }
            finally
            {
                m_loadingFrames.Remove(frameIndex);
            }
        }

        string GetFramePath(int frameIndex)
        {
            int actualFrameNumber = StartFrame + frameIndex;
            string fileName = string.Format(FileNamePattern, actualFrameNumber);
            return Path.Combine(PlyFolderPath, fileName);
        }

        GsplatPlyLoader.FrameData GetFrameData(int frameIndex)
        {
            if (UsePreloadedAssets)
            {
                if (PreloadedFrames != null && frameIndex >= 0 && frameIndex < PreloadedFrames.Length)
                {
                    var asset = PreloadedFrames[frameIndex];
                    if (asset != null)
                    {
                        return new GsplatPlyLoader.FrameData
                        {
                            SplatCount = asset.SplatCount,
                            SHBands = asset.SHBands,
                            Bounds = asset.Bounds,
                            Positions = asset.Positions,
                            Colors = asset.Colors,
                            SHs = asset.SHs,
                            Scales = asset.Scales,
                            Rotations = asset.Rotations
                        };
                    }
                }
            }
            else
            {
                if (m_frameBuffer.TryGetValue(frameIndex, out var frameData))
                {
                    return frameData;
                }
                else if (!AsyncLoading)
                {
                    string path = GetFramePath(frameIndex);
                    frameData = GsplatPlyLoader.Load(path);
                    if (frameData != null)
                    {
                        m_frameBuffer[frameIndex] = frameData;
                        return frameData;
                    }
                }
            }
            return null;
        }

        void UpdateFrame()
        {
            if (m_currentFrameIndex == m_displayedFrameIndex)
                return;

            var frameData = GetFrameData(m_currentFrameIndex);
            if (frameData == null)
                return;

            // Dispose interpolation renderer if switching modes
            if (m_rendererInterp != null)
            {
                m_rendererInterp.Dispose();
                m_rendererInterp = null;
            }

            // Update renderer with new frame data
            bool needsRecreate = m_renderer == null ||
                                 m_currentSplatCount != frameData.SplatCount ||
                                 m_currentSHBands != frameData.SHBands;

            if (needsRecreate)
            {
                m_renderer?.Dispose();
                m_renderer = new GsplatRendererImpl(frameData.SplatCount, frameData.SHBands);
            }

            // Upload frame data to GPU
            m_renderer.PositionBuffer.SetData(frameData.Positions);
            m_renderer.ScaleBuffer.SetData(frameData.Scales);
            m_renderer.RotationBuffer.SetData(frameData.Rotations);
            m_renderer.ColorBuffer.SetData(frameData.Colors);
            if (frameData.SHBands > 0 && frameData.SHs != null)
                m_renderer.SHBuffer.SetData(frameData.SHs);

            m_currentSplatCount = frameData.SplatCount;
            m_currentSHBands = frameData.SHBands;
            m_currentBounds = frameData.Bounds;
            m_displayedFrameIndex = m_currentFrameIndex;
        }

        void UpdateFrameInterpolated()
        {
            int totalFrames = TotalFrames;
            int nextFrameIndex = m_currentFrameIndex + 1;
            if (Loop)
            {
                nextFrameIndex = nextFrameIndex % totalFrames;
            }
            else
            {
                nextFrameIndex = Mathf.Min(nextFrameIndex, totalFrames - 1);
            }

            // Check if we need to update
            if (m_currentFrameIndex == m_displayedFrameIndex && nextFrameIndex == m_displayedNextFrameIndex)
                return;

            var frameDataA = GetFrameData(m_currentFrameIndex);
            var frameDataB = GetFrameData(nextFrameIndex);

            // If we don't have both frames, fall back to showing current frame only
            if (frameDataA == null)
                return;

            if (frameDataB == null)
            {
                frameDataB = frameDataA; // Use same frame if next isn't available
            }

            // Check compatibility
            if (frameDataA.SplatCount != frameDataB.SplatCount || frameDataA.SHBands != frameDataB.SHBands)
            {
                Debug.LogWarning($"Frame {m_currentFrameIndex} and {nextFrameIndex} have different splat counts or SH bands. Interpolation disabled for this transition.");
                frameDataB = frameDataA;
            }

            // Dispose standard renderer if switching modes
            if (m_renderer != null)
            {
                m_renderer.Dispose();
                m_renderer = null;
            }

            // Update renderer with new frame data
            bool needsRecreate = m_rendererInterp == null ||
                                 m_currentSplatCount != frameDataA.SplatCount ||
                                 m_currentSHBands != frameDataA.SHBands;

            if (needsRecreate)
            {
                m_rendererInterp?.Dispose();
                m_rendererInterp = new GsplatRendererImplInterp(frameDataA.SplatCount, frameDataA.SHBands);
            }

            // Upload frame A data to GPU
            m_rendererInterp.PositionBuffer.SetData(frameDataA.Positions);
            m_rendererInterp.ScaleBuffer.SetData(frameDataA.Scales);
            m_rendererInterp.RotationBuffer.SetData(frameDataA.Rotations);
            m_rendererInterp.ColorBuffer.SetData(frameDataA.Colors);
            if (frameDataA.SHBands > 0 && frameDataA.SHs != null)
                m_rendererInterp.SHBuffer.SetData(frameDataA.SHs);

            // Upload frame B data to GPU
            m_rendererInterp.PositionBufferB.SetData(frameDataB.Positions);
            m_rendererInterp.ScaleBufferB.SetData(frameDataB.Scales);
            m_rendererInterp.RotationBufferB.SetData(frameDataB.Rotations);
            m_rendererInterp.ColorBufferB.SetData(frameDataB.Colors);
            if (frameDataB.SHBands > 0 && frameDataB.SHs != null)
                m_rendererInterp.SHBufferB.SetData(frameDataB.SHs);

            m_currentSplatCount = frameDataA.SplatCount;
            m_currentSHBands = frameDataA.SHBands;
            m_currentBounds = frameDataA.Bounds;
            m_currentBounds.Encapsulate(frameDataB.Bounds);
            m_displayedFrameIndex = m_currentFrameIndex;
            m_displayedNextFrameIndex = nextFrameIndex;
        }

        void RenderCurrentFrame()
        {
            if (Valid && m_renderer != null)
            {
                m_renderer.Render(m_currentSplatCount, transform, m_currentBounds,
                    gameObject.layer, GammaToLinear, SHDegree);
            }
        }

        void RenderCurrentFrameInterpolated()
        {
            if (Valid && m_rendererInterp != null)
            {
                m_rendererInterp.Render(m_currentSplatCount, transform, m_currentBounds,
                    gameObject.layer, m_interpolationFactor, GammaToLinear, SHDegree);
            }
        }

        void OnGUI()
        {
            if (!ShowFrameInfo || !Application.isPlaying)
                return;

            // Create style
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            // Build info text
            string info = $"Frame: {m_currentFrameIndex + 1} / {TotalFrames}\n" +
                          $"Time: {m_currentTime:F2}s / {Duration:F2}s";

            if (EnableInterpolation)
            {
                info += $"\nInterp: {m_interpolationFactor:P0}";
            }

            if (!UsePreloadedAssets)
            {
                info += $"\nBuffered: {m_frameBuffer.Count}";
            }

            // Calculate position
            Vector2 size = style.CalcSize(new GUIContent(info));
            float padding = 10f;
            Rect rect;

            switch (DisplayPosition)
            {
                case TextAnchor.UpperLeft:
                    rect = new Rect(padding, padding, size.x, size.y);
                    break;
                case TextAnchor.UpperCenter:
                    rect = new Rect((Screen.width - size.x) / 2, padding, size.x, size.y);
                    break;
                case TextAnchor.UpperRight:
                    rect = new Rect(Screen.width - size.x - padding, padding, size.x, size.y);
                    break;
                case TextAnchor.MiddleLeft:
                    rect = new Rect(padding, (Screen.height - size.y) / 2, size.x, size.y);
                    break;
                case TextAnchor.MiddleCenter:
                    rect = new Rect((Screen.width - size.x) / 2, (Screen.height - size.y) / 2, size.x, size.y);
                    break;
                case TextAnchor.MiddleRight:
                    rect = new Rect(Screen.width - size.x - padding, (Screen.height - size.y) / 2, size.x, size.y);
                    break;
                case TextAnchor.LowerLeft:
                    rect = new Rect(padding, Screen.height - size.y - padding, size.x, size.y);
                    break;
                case TextAnchor.LowerCenter:
                    rect = new Rect((Screen.width - size.x) / 2, Screen.height - size.y - padding, size.x, size.y);
                    break;
                case TextAnchor.LowerRight:
                    rect = new Rect(Screen.width - size.x - padding, Screen.height - size.y - padding, size.x, size.y);
                    break;
                default:
                    rect = new Rect(padding, padding, size.x, size.y);
                    break;
            }

            // Draw shadow
            var shadowStyle = new GUIStyle(style) { normal = { textColor = Color.black } };
            GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), info, shadowStyle);

            // Draw text
            GUI.Label(rect, info, style);
        }

        // Public API

        /// <summary>
        /// Start or resume playback.
        /// </summary>
        public void Play()
        {
            m_isPlaying = true;

            // Preload first frames if using async loading
            if (AsyncLoading && !UsePreloadedAssets && Application.isPlaying)
            {
                BufferFrames();
            }
        }

        /// <summary>
        /// Pause playback.
        /// </summary>
        public void Pause()
        {
            m_isPlaying = false;
        }

        /// <summary>
        /// Stop playback and reset to first frame.
        /// </summary>
        public void Stop()
        {
            m_isPlaying = false;
            m_currentTime = 0;
            m_currentFrameIndex = 0;
            m_interpolationFactor = 0;
        }

        /// <summary>
        /// Jump to a specific frame.
        /// </summary>
        /// <param name="frameIndex">Frame index (0-based).</param>
        public void SetFrame(int frameIndex)
        {
            int totalFrames = TotalFrames;
            m_currentFrameIndex = Mathf.Clamp(frameIndex, 0, totalFrames - 1);
            m_currentTime = m_currentFrameIndex / Mathf.Max(FrameRate, 0.001f);
            m_interpolationFactor = 0;
            m_displayedFrameIndex = -1; // Force update

            if (AsyncLoading && !UsePreloadedAssets && Application.isPlaying)
            {
                BufferFrames();
            }
        }

        /// <summary>
        /// Jump to a specific time.
        /// </summary>
        /// <param name="time">Time in seconds.</param>
        public void SetTime(float time)
        {
            m_currentTime = Mathf.Clamp(time, 0, Duration);
            float frameDuration = 1f / Mathf.Max(FrameRate, 0.001f);
            float exactFrame = m_currentTime / frameDuration;
            m_currentFrameIndex = Mathf.FloorToInt(exactFrame);
            m_interpolationFactor = exactFrame - m_currentFrameIndex;
            m_displayedFrameIndex = -1; // Force update

            if (AsyncLoading && !UsePreloadedAssets && Application.isPlaying)
            {
                BufferFrames();
            }
        }

        /// <summary>
        /// Preload all frames into memory (use with caution for large sequences).
        /// </summary>
        public async Task PreloadAllFrames()
        {
            if (UsePreloadedAssets)
            {
                Debug.LogWarning("PreloadAllFrames is not needed when using preloaded assets.");
                return;
            }

            int totalFrames = TotalFrames;
            var tasks = new List<Task>();

            for (int i = 0; i < totalFrames; i++)
            {
                if (!m_frameBuffer.ContainsKey(i))
                {
                    int frameIndex = i;
                    tasks.Add(Task.Run(async () =>
                    {
                        string path = GetFramePath(frameIndex);
                        var frameData = await GsplatPlyLoader.LoadAsync(path);
                        if (frameData != null)
                        {
                            lock (m_frameBuffer)
                            {
                                m_frameBuffer[frameIndex] = frameData;
                            }
                        }
                    }));
                }
            }

            await Task.WhenAll(tasks);
            Debug.Log($"Preloaded {m_frameBuffer.Count} frames.");
        }

        /// <summary>
        /// Clear the frame buffer to free memory.
        /// </summary>
        public void ClearBuffer()
        {
            m_frameBuffer.Clear();
            m_displayedFrameIndex = -1;
            m_displayedNextFrameIndex = -1;
        }

        /// <summary>
        /// Get the number of frames currently loaded in the buffer.
        /// </summary>
        public int LoadedFrameCount => m_frameBuffer.Count;
    }
}
