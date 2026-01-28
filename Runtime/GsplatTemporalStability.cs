// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// Temporal stability filter to reduce flickering in Gaussian Splatting sequences.
    /// Attach this component to your camera.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [ExecuteAlways]
    public class GsplatTemporalStability : MonoBehaviour
    {
        [Header("Stability Settings")]
        [Tooltip("Enable temporal stability filtering")]
        public bool Enabled = true;

        [Tooltip("Blend factor with history frame (0 = no blend, 1 = full history)")]
        [Range(0f, 0.9f)]
        public float BlendFactor = 0.3f;

        [Tooltip("Color difference threshold for stability detection")]
        [Range(0.01f, 0.5f)]
        public float ColorThreshold = 0.1f;

        [Header("Debug")]
        [Tooltip("Show the history buffer")]
        public bool ShowHistory;

        Material m_material;
        RenderTexture m_historyBuffer;
        RenderTexture m_tempBuffer;

        static readonly int k_historyTex = Shader.PropertyToID("_HistoryTex");
        static readonly int k_blendFactor = Shader.PropertyToID("_BlendFactor");
        static readonly int k_colorThreshold = Shader.PropertyToID("_ColorThreshold");

        void OnEnable()
        {
            CreateMaterial();
        }

        void OnDisable()
        {
            ReleaseResources();
        }

        void OnDestroy()
        {
            ReleaseResources();
        }

        void CreateMaterial()
        {
            if (m_material != null)
                return;

            var shader = Shader.Find("Gsplat/TemporalStability");
            if (shader == null)
            {
                Debug.LogError("Could not find Gsplat/TemporalStability shader");
                return;
            }

            m_material = new Material(shader);
        }

        void ReleaseResources()
        {
            if (m_historyBuffer != null)
            {
                m_historyBuffer.Release();
                DestroyImmediate(m_historyBuffer);
                m_historyBuffer = null;
            }

            if (m_tempBuffer != null)
            {
                m_tempBuffer.Release();
                DestroyImmediate(m_tempBuffer);
                m_tempBuffer = null;
            }

            if (m_material != null)
            {
                DestroyImmediate(m_material);
                m_material = null;
            }
        }

        void EnsureBuffers(int width, int height)
        {
            if (m_historyBuffer == null || m_historyBuffer.width != width || m_historyBuffer.height != height)
            {
                if (m_historyBuffer != null)
                {
                    m_historyBuffer.Release();
                    DestroyImmediate(m_historyBuffer);
                }

                m_historyBuffer = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf);
                m_historyBuffer.name = "Gsplat History Buffer";
                m_historyBuffer.Create();
            }

            if (m_tempBuffer == null || m_tempBuffer.width != width || m_tempBuffer.height != height)
            {
                if (m_tempBuffer != null)
                {
                    m_tempBuffer.Release();
                    DestroyImmediate(m_tempBuffer);
                }

                m_tempBuffer = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf);
                m_tempBuffer.name = "Gsplat Temp Buffer";
                m_tempBuffer.Create();
            }
        }

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (!Enabled || m_material == null || !Application.isPlaying)
            {
                Graphics.Blit(source, destination);
                return;
            }

            EnsureBuffers(source.width, source.height);

            if (ShowHistory && m_historyBuffer != null)
            {
                Graphics.Blit(m_historyBuffer, destination);
                return;
            }

            // Set shader parameters
            m_material.SetTexture(k_historyTex, m_historyBuffer);
            m_material.SetFloat(k_blendFactor, BlendFactor);
            m_material.SetFloat(k_colorThreshold, ColorThreshold);

            // Apply temporal stability filter
            Graphics.Blit(source, m_tempBuffer, m_material);

            // Copy result to destination
            Graphics.Blit(m_tempBuffer, destination);

            // Update history buffer
            Graphics.Blit(m_tempBuffer, m_historyBuffer);
        }

        /// <summary>
        /// Clear the history buffer (call this when jumping to a different frame).
        /// </summary>
        public void ClearHistory()
        {
            if (m_historyBuffer != null)
            {
                RenderTexture.active = m_historyBuffer;
                GL.Clear(true, true, Color.clear);
                RenderTexture.active = null;
            }
        }
    }
}
