using System;
using Unity.Mathematics;
using UnityEngine;
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
        [Serializable]
        public class OceanSettings
        {
            // General
            public ReflectionType refType = ReflectionType.ScreenSpaceReflection; // How the reflections are generated
            public PlanarReflections.PlanarReflectionSettings planarSettings;
            public SSRSettings SsrSettings = new SSRSettings();
            public float distanceBlend = 100.0f;
            public int randomSeed = 3234;

            // Cubemap settings
            public Cubemap cubemapRefType;

            // Visual Surface
            public float _waterMaxVisibility = 5.0f;
            public Color _absorptionColor = new Color(0.2f, 0.6f, 0.8f);
            public Color _scatteringColor = new Color(0.0f, 0.085f, 0.1f);

            // Waves
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
            public SSRSteps Steps = SSRSteps.Low; //SSRSteps.Medium;
            [Range(0.01f, 1f)]
            public float StepSize = 0.15f; //0.1f;
            [Range(0.25f, 3f)]
            public float Thickness = 0.5f; //2f;
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
                case ReflectionType.ScreenSpaceReflection:
                    return KeyRefSSR;
                case ReflectionType.PlanarReflection:
                    return KeyRefPlanar;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        public struct WaveOutputData
        {
            public float3 Position;
            public float3 Normal;
        }
    }
}