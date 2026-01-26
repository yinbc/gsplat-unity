// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// Renderer implementation with interpolation support for smooth frame transitions.
    /// Maintains two sets of buffers for frame A and frame B.
    /// </summary>
    public class GsplatRendererImplInterp
    {
        public uint SplatCount { get; private set; }
        public byte SHBands { get; private set; }

        MaterialPropertyBlock m_propertyBlock;

        // Frame A buffers
        public GraphicsBuffer PositionBuffer { get; private set; }
        public GraphicsBuffer ScaleBuffer { get; private set; }
        public GraphicsBuffer RotationBuffer { get; private set; }
        public GraphicsBuffer ColorBuffer { get; private set; }
        public GraphicsBuffer SHBuffer { get; private set; }

        // Frame B buffers
        public GraphicsBuffer PositionBufferB { get; private set; }
        public GraphicsBuffer ScaleBufferB { get; private set; }
        public GraphicsBuffer RotationBufferB { get; private set; }
        public GraphicsBuffer ColorBufferB { get; private set; }
        public GraphicsBuffer SHBufferB { get; private set; }

        // Shared buffers
        public GraphicsBuffer OrderBuffer { get; private set; }
        public ISorterResource SorterResource { get; private set; }

        // Materials for interpolation
        Material[] m_interpMaterials;

        public bool Valid =>
            PositionBuffer != null &&
            ScaleBuffer != null &&
            RotationBuffer != null &&
            ColorBuffer != null &&
            PositionBufferB != null &&
            ScaleBufferB != null &&
            RotationBufferB != null &&
            ColorBufferB != null &&
            (SHBands == 0 || (SHBuffer != null && SHBufferB != null));

        static readonly int k_orderBuffer = Shader.PropertyToID("_OrderBuffer");
        static readonly int k_positionBuffer = Shader.PropertyToID("_PositionBuffer");
        static readonly int k_scaleBuffer = Shader.PropertyToID("_ScaleBuffer");
        static readonly int k_rotationBuffer = Shader.PropertyToID("_RotationBuffer");
        static readonly int k_colorBuffer = Shader.PropertyToID("_ColorBuffer");
        static readonly int k_shBuffer = Shader.PropertyToID("_SHBuffer");
        static readonly int k_positionBufferB = Shader.PropertyToID("_PositionBufferB");
        static readonly int k_scaleBufferB = Shader.PropertyToID("_ScaleBufferB");
        static readonly int k_rotationBufferB = Shader.PropertyToID("_RotationBufferB");
        static readonly int k_colorBufferB = Shader.PropertyToID("_ColorBufferB");
        static readonly int k_shBufferB = Shader.PropertyToID("_SHBufferB");
        static readonly int k_matrixM = Shader.PropertyToID("_MATRIX_M");
        static readonly int k_splatInstanceSize = Shader.PropertyToID("_SplatInstanceSize");
        static readonly int k_splatCount = Shader.PropertyToID("_SplatCount");
        static readonly int k_gammaToLinear = Shader.PropertyToID("_GammaToLinear");
        static readonly int k_shDegree = Shader.PropertyToID("_SHDegree");
        static readonly int k_interpolationFactor = Shader.PropertyToID("_InterpolationFactor");

        static readonly string[] k_shKeywords = { "SH_BANDS_0", "SH_BANDS_1", "SH_BANDS_2", "SH_BANDS_3" };

        public GsplatRendererImplInterp(uint splatCount, byte shBands)
        {
            SplatCount = splatCount;
            SHBands = shBands;
            CreateResources(splatCount);
            CreateMaterials();
            CreatePropertyBlock();
        }

        public void RecreateResources(uint splatCount, byte shBands)
        {
            if (SplatCount == splatCount && SHBands == shBands)
                return;
            Dispose();
            SplatCount = splatCount;
            SHBands = shBands;
            CreateResources(splatCount);
            CreateMaterials();
            CreatePropertyBlock();
        }

        void CreateResources(uint splatCount)
        {
            int count = (int)splatCount;
            int vec3Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Vector3));
            int vec4Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Vector4));

            // Frame A buffers
            PositionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, vec3Size);
            ScaleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, vec3Size);
            RotationBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, vec4Size);
            ColorBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, vec4Size);

            // Frame B buffers
            PositionBufferB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, vec3Size);
            ScaleBufferB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, vec3Size);
            RotationBufferB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, vec4Size);
            ColorBufferB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, vec4Size);

            if (SHBands > 0)
            {
                int shCount = GsplatUtils.SHBandsToCoefficientCount(SHBands) * count;
                SHBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, shCount, vec3Size);
                SHBufferB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, shCount, vec3Size);
            }

            OrderBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
            SorterResource = GsplatSorter.Instance.CreateSorterResource(splatCount, PositionBuffer, OrderBuffer);
        }

        void CreateMaterials()
        {
            var shader = Shader.Find("Gsplat/Interpolated");
            if (shader == null)
            {
                Debug.LogError("Could not find Gsplat/Interpolated shader");
                return;
            }

            m_interpMaterials = new Material[4];
            for (int i = 0; i < 4; i++)
            {
                m_interpMaterials[i] = new Material(shader);
                m_interpMaterials[i].EnableKeyword(k_shKeywords[i]);
            }
        }

        void CreatePropertyBlock()
        {
            m_propertyBlock ??= new MaterialPropertyBlock();

            m_propertyBlock.SetBuffer(k_orderBuffer, OrderBuffer);

            // Frame A
            m_propertyBlock.SetBuffer(k_positionBuffer, PositionBuffer);
            m_propertyBlock.SetBuffer(k_scaleBuffer, ScaleBuffer);
            m_propertyBlock.SetBuffer(k_rotationBuffer, RotationBuffer);
            m_propertyBlock.SetBuffer(k_colorBuffer, ColorBuffer);

            // Frame B
            m_propertyBlock.SetBuffer(k_positionBufferB, PositionBufferB);
            m_propertyBlock.SetBuffer(k_scaleBufferB, ScaleBufferB);
            m_propertyBlock.SetBuffer(k_rotationBufferB, RotationBufferB);
            m_propertyBlock.SetBuffer(k_colorBufferB, ColorBufferB);

            if (SHBands > 0)
            {
                m_propertyBlock.SetBuffer(k_shBuffer, SHBuffer);
                m_propertyBlock.SetBuffer(k_shBufferB, SHBufferB);
            }
        }

        public void Dispose()
        {
            PositionBuffer?.Dispose();
            ScaleBuffer?.Dispose();
            RotationBuffer?.Dispose();
            ColorBuffer?.Dispose();
            SHBuffer?.Dispose();

            PositionBufferB?.Dispose();
            ScaleBufferB?.Dispose();
            RotationBufferB?.Dispose();
            ColorBufferB?.Dispose();
            SHBufferB?.Dispose();

            OrderBuffer?.Dispose();
            SorterResource?.Dispose();

            if (m_interpMaterials != null)
            {
                foreach (var mat in m_interpMaterials)
                {
                    if (mat != null)
                        Object.DestroyImmediate(mat);
                }
            }

            PositionBuffer = null;
            ScaleBuffer = null;
            RotationBuffer = null;
            ColorBuffer = null;
            SHBuffer = null;
            PositionBufferB = null;
            ScaleBufferB = null;
            RotationBufferB = null;
            ColorBufferB = null;
            SHBufferB = null;
            OrderBuffer = null;
        }

        /// <summary>
        /// Render with interpolation between frame A and frame B.
        /// </summary>
        /// <param name="splatCount">Number of splats to render.</param>
        /// <param name="transform">Object transform.</param>
        /// <param name="localBounds">Bounding box in object space.</param>
        /// <param name="layer">Layer used for rendering.</param>
        /// <param name="interpolationFactor">Interpolation factor (0.0 = frame A, 1.0 = frame B).</param>
        /// <param name="gammaToLinear">Convert color space from Gamma to Linear.</param>
        /// <param name="shDegree">Order of SH coefficients used for rendering.</param>
        public void Render(uint splatCount, Transform transform, Bounds localBounds, int layer,
            float interpolationFactor, bool gammaToLinear = false, int shDegree = 3)
        {
            if (!Valid || !GsplatSettings.Instance.Valid || !GsplatSorter.Instance.Valid)
                return;

            if (m_interpMaterials == null || m_interpMaterials[SHBands] == null)
                return;

            m_propertyBlock.SetInteger(k_splatCount, (int)splatCount);
            m_propertyBlock.SetInteger(k_gammaToLinear, gammaToLinear ? 1 : 0);
            m_propertyBlock.SetInteger(k_splatInstanceSize, (int)GsplatSettings.Instance.SplatInstanceSize);
            m_propertyBlock.SetInteger(k_shDegree, shDegree);
            m_propertyBlock.SetFloat(k_interpolationFactor, interpolationFactor);
            m_propertyBlock.SetMatrix(k_matrixM, transform.localToWorldMatrix);

            var rp = new RenderParams(m_interpMaterials[SHBands])
            {
                worldBounds = GsplatUtils.CalcWorldBounds(localBounds, transform),
                matProps = m_propertyBlock,
                layer = layer
            };

            Graphics.RenderMeshPrimitives(rp, GsplatSettings.Instance.Mesh, 0,
                Mathf.CeilToInt(splatCount / (float)GsplatSettings.Instance.SplatInstanceSize));
        }
    }
}
