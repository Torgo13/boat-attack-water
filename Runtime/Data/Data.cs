using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Rendering;
using WaterSystem.Rendering;

namespace WaterSystem
{
    public class Data
    {
        // Shader Keywords
        private const string KeyRefCubemap = "_REFLECTION_CUBEMAP";
        private const string KeyRefProbe = "_REFLECTION_PROBES";
        private const string KeyRefPlanar = "_REFLECTION_PLANARREFLECTION";
        private const string KeyRefSSR = "_REFLECTION_SSR";

        /// <summary>
        /// This class stores the settings for a water system
        /// </summary>
        [System.Serializable]
        public class OceanSettings
        {
            // General
#if ZERO
            public GeometryType waterGeomType; // The type of geometry, either vertex offset or tessellation
#endif // ZERO
            public ReflectionType refType = ReflectionType.ScreenSpaceReflection; // How the reflections are generated
            public PlanarReflections.PlanarReflectionSettings planarSettings = new PlanarReflections.PlanarReflectionSettings();
            public SSRSettings SsrSettings = new SSRSettings();
#if ZERO
            public bool isInfinite; // Is the water infinite
#endif // ZERO
            public float distanceBlend = 100.0f;
            public int randomSeed = 3234;

            // Cubemap settings
            public Cubemap cubemapRefType;

            // Visual Surface
            public float _waterMaxVisibility = 5.0f;
            public Color _absorptionColor = new Color(0.2f, 0.6f, 0.8f);
            public Color _scatteringColor = new Color(0.0f, 0.085f, 0.1f);

            // Waves
#if ZERO
            public List<Wave> _waves = new List<Wave>();
#endif // ZERO
            public bool _customWaves;
            public BasicWaves _basicWaveSettings = new BasicWaves(0.5f, 45.0f, 5.0f);
            public AnimationCurve _waveFoamProfile = AnimationCurve.Linear(0.02f, 0f, 0.98f, 1f);
            public AnimationCurve _waveDepthProfile = AnimationCurve.Linear(0.0f, 1f, 0.98f, 0f);


            // Micro(surface) Waves
            public float _microWaveIntensity = 0.25f;

            // Shore
            public float _foamIntensity = 1.0f;
            public AnimationCurve _shoreFoamProfile = AnimationCurve.Linear(0.02f, 1f, 0.98f, 0f);
        }

        [Serializable]
        public class SSRSettings
        {
            public SSRSteps Steps = SSRSteps.Low;
            [Range(0.01f, 1f)]
            public float StepSize = 0.15f;
            [Range(0.25f, 3f)]
            public float Thickness = 0.5f;
        }

        [Serializable]
        public enum SSRSteps
        {
            Low = 8,
            Medium = 16,
            High = 32,
        }

        /// <summary>
        /// Basic wave type, this is for the base Gerstner wave values
        /// it will drive automatic generation of n amount of waves
        /// </summary>
        [Serializable]
        public struct BasicWaves
        {
            [Range(3, 10)]
            public int waveCount;
            public float amplitude;
            public float direction;
            public float wavelength;

            public BasicWaves(float amp, float dir, float len)
            {
                waveCount = 6;
                amplitude = amp;
                direction = dir;
                wavelength = len;
            }
        }
        
#if ZERO
        /// <summary>
        /// Class to describe a single Gerstner Wave
        /// </summary>
        [Serializable]
        public struct Wave
        {
            public float amplitude; // height of the wave in units(m)
            public float direction; // direction the wave travels in degrees from Z+
            public float wavelength; // distance between crest>crest
            public float2 origin; // Omi directional point of origin
            public float onmiDir; // Is omni?

            public Wave(float amp, float dir, float length, float2 org, bool omni)
            {
                amplitude = amp;
                direction = dir;
                wavelength = length;
                origin = org;
                onmiDir = omni ? 1 : 0;
            }
        }
#endif // ZERO

        /// <summary>
        /// The type of reflection source, custom cubemap, closest reflection probe, planar reflection
        /// </summary>
        [Serializable]
        public enum ReflectionType
        {
            Cubemap,
            ReflectionProbe,
            ScreenSpaceReflection,
            PlanarReflection,
        }

        public static string GetReflectionKeyword(ReflectionType type)
        {
            switch (type)
            {
                case ReflectionType.Cubemap:
                    return KeyRefCubemap;
                case ReflectionType.ReflectionProbe:
                    return KeyRefProbe;
                case ReflectionType.PlanarReflection:
                    return KeyRefPlanar;
                case ReflectionType.ScreenSpaceReflection:
                    return KeyRefSSR;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

#if ZER0
        /// <summary>
        /// The type of geometry, either vertex offset or tessellation
        /// </summary>
        [Serializable]
        public enum GeometryType
        {
            VertexOffset,
            Tesselation
        }

        [Serializable]
        public enum DebugShading
        {
            none,
            normalWS,
            Reflection,
            Refraction,
            Specular,
            SSS,
            Shadow,
            Foam,
            FoamMask,
            WaterBufferA,
            WaterBufferB,
            Depth,
            WaterDepth,
            Fresnel,
            Mesh,
        }
#endif // ZERO

        public struct WaveOutputData
        {
            public float3 Position;
            public float3 Normal;
        }
    }
}
