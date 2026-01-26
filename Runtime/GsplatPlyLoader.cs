// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// Runtime PLY file loader for Gaussian Splatting data.
    /// This allows loading PLY files at runtime without going through the asset import pipeline.
    /// </summary>
    public static class GsplatPlyLoader
    {
        public class PlyHeaderInfo
        {
            public uint VertexCount;
            public int PropertyCount;
            public int SHPropertyCount;
            public int PositionOffset = -1;
            public int ColorOffset = -1;
            public int SHOffset = -1;
            public int OpacityOffset = -1;
            public int ScaleOffset = -1;
            public int RotationOffset = -1;
        }

        /// <summary>
        /// Frame data loaded from a PLY file.
        /// </summary>
        public class FrameData
        {
            public uint SplatCount;
            public byte SHBands;
            public Bounds Bounds;
            public Vector3[] Positions;
            public Vector4[] Colors;
            public Vector3[] SHs;
            public Vector3[] Scales;
            public Vector4[] Rotations;
        }

        static string ReadLine(Stream stream)
        {
            var byteBuffer = new List<byte>();
            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1 || b == '\n') break;
                byteBuffer.Add((byte)b);
            }

            if (byteBuffer.Count > 0 && byteBuffer.Last() == '\r')
            {
                byteBuffer.RemoveAt(byteBuffer.Count - 1);
            }

            return Encoding.UTF8.GetString(byteBuffer.ToArray());
        }

        static PlyHeaderInfo ReadPlyHeader(Stream stream)
        {
            var info = new PlyHeaderInfo();

            while (ReadLine(stream) is { } line && line != "end_header")
            {
                var tokens = line.Split(' ');
                if (tokens.Length == 3 && tokens[0] == "element" && tokens[1] == "vertex")
                    info.VertexCount = uint.Parse(tokens[2]);
                if (tokens.Length != 3 || tokens[0] != "property") continue;
                switch (tokens[2])
                {
                    case "x":
                        info.PositionOffset = info.PropertyCount;
                        break;
                    case "f_dc_0":
                        info.ColorOffset = info.PropertyCount;
                        break;
                    case "f_rest_0":
                        info.SHOffset = info.PropertyCount;
                        break;
                    case "opacity":
                        info.OpacityOffset = info.PropertyCount;
                        break;
                    case "scale_0":
                        info.ScaleOffset = info.PropertyCount;
                        break;
                    case "rot_0":
                        info.RotationOffset = info.PropertyCount;
                        break;
                }

                if (tokens[2].StartsWith("f_rest_"))
                    info.SHPropertyCount++;
                info.PropertyCount++;
            }

            return info;
        }

        /// <summary>
        /// Load a PLY file synchronously.
        /// </summary>
        /// <param name="path">Path to the PLY file.</param>
        /// <returns>Loaded frame data, or null if loading failed.</returns>
        public static FrameData Load(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"PLY file not found: {path}");
                return null;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            return LoadFromStream(fs, path);
        }

        /// <summary>
        /// Load a PLY file from a byte array.
        /// </summary>
        /// <param name="data">PLY file data as bytes.</param>
        /// <param name="name">Name for error reporting.</param>
        /// <returns>Loaded frame data, or null if loading failed.</returns>
        public static FrameData LoadFromBytes(byte[] data, string name = "memory")
        {
            using var ms = new MemoryStream(data);
            return LoadFromStream(ms, name);
        }

        /// <summary>
        /// Load a PLY file asynchronously.
        /// </summary>
        /// <param name="path">Path to the PLY file.</param>
        /// <returns>Task that resolves to loaded frame data, or null if loading failed.</returns>
        public static Task<FrameData> LoadAsync(string path)
        {
            return Task.Run(() => Load(path));
        }

        static FrameData LoadFromStream(Stream stream, string sourceName)
        {
            var plyInfo = ReadPlyHeader(stream);
            var shCoeffs = plyInfo.SHPropertyCount / 3;
            var shBands = GsplatUtils.CalcSHBandsFromSHPropertyCount(plyInfo.SHPropertyCount);

            if (shBands > 3 || GsplatUtils.SHBandsToCoefficientCount(shBands) * 3 != plyInfo.SHPropertyCount)
            {
                Debug.LogError($"{sourceName} load error: unexpected SH property count {plyInfo.SHPropertyCount}");
                return null;
            }

            if (plyInfo.PositionOffset == -1 || plyInfo.ColorOffset == -1 || plyInfo.OpacityOffset == -1 ||
                plyInfo.ScaleOffset == -1 || plyInfo.RotationOffset == -1)
            {
                Debug.LogError($"{sourceName} load error: missing required properties in PLY header");
                return null;
            }

            var frameData = new FrameData
            {
                SplatCount = plyInfo.VertexCount,
                SHBands = shBands,
                Positions = new Vector3[plyInfo.VertexCount],
                Colors = new Vector4[plyInfo.VertexCount],
                SHs = shCoeffs > 0 ? new Vector3[plyInfo.VertexCount * shCoeffs] : null,
                Scales = new Vector3[plyInfo.VertexCount],
                Rotations = new Vector4[plyInfo.VertexCount]
            };

            var buffer = new byte[plyInfo.PropertyCount * sizeof(float)];
            var bounds = new Bounds();

            for (uint i = 0; i < plyInfo.VertexCount; i++)
            {
                var readBytes = stream.Read(buffer, 0, buffer.Length);
                if (readBytes != buffer.Length)
                {
                    Debug.LogError($"{sourceName} load error: unexpected end of file at vertex {i}");
                    return null;
                }

                var properties = MemoryMarshal.Cast<byte, float>(buffer);
                frameData.Positions[i] = new Vector3(
                    properties[plyInfo.PositionOffset],
                    properties[plyInfo.PositionOffset + 1],
                    properties[plyInfo.PositionOffset + 2]);
                frameData.Colors[i] = new Vector4(
                    properties[plyInfo.ColorOffset],
                    properties[plyInfo.ColorOffset + 1],
                    properties[plyInfo.ColorOffset + 2],
                    GsplatUtils.Sigmoid(properties[plyInfo.OpacityOffset]));

                if (shCoeffs > 0 && plyInfo.SHOffset >= 0)
                {
                    for (int j = 0; j < shCoeffs; j++)
                        frameData.SHs[i * shCoeffs + j] = new Vector3(
                            properties[j + plyInfo.SHOffset],
                            properties[j + plyInfo.SHOffset + shCoeffs],
                            properties[j + plyInfo.SHOffset + shCoeffs * 2]);
                }

                frameData.Scales[i] = new Vector3(
                    Mathf.Exp(properties[plyInfo.ScaleOffset]),
                    Mathf.Exp(properties[plyInfo.ScaleOffset + 1]),
                    Mathf.Exp(properties[plyInfo.ScaleOffset + 2]));
                frameData.Rotations[i] = new Vector4(
                    properties[plyInfo.RotationOffset],
                    properties[plyInfo.RotationOffset + 1],
                    properties[plyInfo.RotationOffset + 2],
                    properties[plyInfo.RotationOffset + 3]).normalized;

                if (i == 0) bounds = new Bounds(frameData.Positions[i], Vector3.zero);
                else bounds.Encapsulate(frameData.Positions[i]);
            }

            frameData.Bounds = bounds;
            return frameData;
        }

        /// <summary>
        /// Create a GsplatAsset from frame data.
        /// </summary>
        /// <param name="frameData">Frame data to convert.</param>
        /// <returns>New GsplatAsset instance.</returns>
        public static GsplatAsset CreateAsset(FrameData frameData)
        {
            var asset = ScriptableObject.CreateInstance<GsplatAsset>();
            asset.SplatCount = frameData.SplatCount;
            asset.SHBands = frameData.SHBands;
            asset.Bounds = frameData.Bounds;
            asset.Positions = frameData.Positions;
            asset.Colors = frameData.Colors;
            asset.SHs = frameData.SHs;
            asset.Scales = frameData.Scales;
            asset.Rotations = frameData.Rotations;
            return asset;
        }
    }
}
