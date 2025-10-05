using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;
using Unity.Collections;

namespace WaterSystem
{
    /// <summary>
    /// C# Jobs system version of the Gerstner waves implementation
    /// </summary>
    public static class GerstnerWavesJobs
    {
        //General variables
        public static bool Initialized;
        private static bool _firstFrame = true;
        private static bool _processing;
#if ZERO
        private static int _waveCount;
#endif // ZERO

        const int sampleCount = 4096;
        const int depthProfileCount = 32;

        public static NativeArray<float> _data;

        public static NativeArray<float3> _waveData; // Wave data from the water system

        //Details for Buoyant Objects

        // world space sampling position buffer
        private static NativeArray<float3> _samplePositionsA;
        private static NativeArray<float3> _samplePositionsB;

        // position counter
        public static int _positionCount;

        // Gerstner calculated wave position and normals
        private static NativeArray<Data.WaveOutputData> _gerstnerWavesA, _gerstnerWavesB;

        // Depth data
        private static NativeArray<float> _opacity;
#if ZERO
        private static NativeArray<float> _waterDepth;
#endif // ZERO
        private static NativeArray<float> _depthProfile;

        // Job handles
#if ZERO
        private static JobHandle _waterDepthHandle;
        private static JobHandle _opacityHandle;
#endif // ZERO
        private static JobHandle _waterHeightHandle;

        /// <summary>
        /// Dictionary containing the objects GUID and positions to sample for buoyancy
        /// </summary>
        static readonly Dictionary<int, int2> _registry = new Dictionary<int, int2>();
        public static Dictionary<int, int2> Registry => _registry;

        public static void Init()
        {
            //Wave data
            Ocean ocean = Ocean.Instance;
            if (ocean == null)
                return;

            _data = new NativeArray<float>(
                  3 * sampleCount   // _samplePositionsA
                + 3 * sampleCount   // _samplePositionsB
                + 6 * sampleCount   // _gerstnerWavesA
                + 6 * sampleCount   // _gerstnerWavesB
                + sampleCount       // _opacity
                + depthProfileCount // _depthProfile
                , Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            int start = 0;
            int length = 3 * sampleCount;

            // Max sample count for quality level
            _samplePositionsA = _data.GetSubArray(start, length).Reinterpret<float3>(sizeof(float));
            start += length;
            _samplePositionsB = _data.GetSubArray(start, length).Reinterpret<float3>(sizeof(float));
            start += length;

            length = 6 * sampleCount;
            _gerstnerWavesA = _data.GetSubArray(start, length).Reinterpret<Data.WaveOutputData>(sizeof(float));
            start += length;
            _gerstnerWavesB = _data.GetSubArray(start, length).Reinterpret<Data.WaveOutputData>(sizeof(float));
            start += length;

            length = sampleCount;
            _opacity = _data.GetSubArray(start, length);
            start += length;

            length = depthProfileCount;
            _depthProfile = _data.GetSubArray(start, length);
            var waveDepthProfile = ocean.settingsData._waveDepthProfile;

            for (int i = 0; i < depthProfileCount; i++)
            {
                _depthProfile[i] = waveDepthProfile.Evaluate(i / (float)depthProfileCount);
            }

            Initialized = true;
        }

        public static void Cleanup()
        {
            _waterHeightHandle.Complete();

            DepthGenerator.CleanUp();

            // Cleanup native arrays
            if (_data.IsCreated)
                _data.Dispose();

            Initialized = false;
        }

        public static void UpdateSamplePoints(ref NativeArray<float3> samplePoints, int guid)
        {
            if (Registry.TryGetValue(guid, out var offsets))
            {
                for (var i = offsets.x; i < offsets.y; i++)
                    _samplePositionsB[i] = samplePoints[i - offsets.x];
            }
            else
            {
                if (_positionCount + samplePoints.Length >= _samplePositionsB.Length)
                    return;

                offsets = new int2(_positionCount, _positionCount + samplePoints.Length);
                Registry.Add(guid, offsets);
                _positionCount += samplePoints.Length;
            }
        }

        public static void RemoveSamplePoints(int guid)
        {
            if (!Registry.TryGetValue(guid, out int2 offsets))
                return;

            var min = offsets.x;
            var size = offsets.y - min;

            Registry.Remove(guid);
            using var _0 = UnityEngine.Pool.ListPool<int>.Get(out var keys);
            keys.AddRange(Registry.Keys);
            for (int i = 0, keysCount = keys.Count; i < keysCount; i++)
            {
                int offsetEntry = keys[i];
                var entry = Registry[offsetEntry];
                // if values after removal, offset
                if (entry.x > min)
                {
                    entry -= size;
                }

                Registry[offsetEntry] = entry;
            }

            _positionCount -= size;
        }

        public static void GetData(int guid, ref Data.WaveOutputData[] output)
        {
            if (!Registry.TryGetValue(guid, out int2 offsets))
                return;

            _gerstnerWavesB.Slice(offsets.x, offsets.y - offsets.x).CopyTo(output);
        }

        // Height jobs for the next frame
        public static void UpdateHeights()
        {
            CompleteJobs();

            if (_processing)
                return;

            if (Ocean.Instance == null)
                return;

            _processing = true;

#if STATIC_EVERYTHING
            var t = 0.0f;
#else
            var t = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
#endif

#if ZER0
            // Test Water Depth
            var waterDepth = new DepthGenerator.WaterDepth()
            {
                Position = _positions,
                DepthData = DepthGenerator._depthData,
                DepthValues = DepthGenerator._globalDepthValues,
                Depth = _waterDepth,
            };
            
            _waterDepthHandle = waterDepth.Schedule(_positionCount, 64);
            
            // Generate Opacity Values
            var opacity = new OpacityJob()
            {
                DepthProfile = _depthProfile,
                DepthValues = _waterDepth,
                Opacity = _opacity,
            };

            _opacityHandle = opacity.Schedule(_positionCount, _waterDepthHandle);
#endif // ZER0

            // Gerstner Wave Height
            var offset = Ocean.Instance.transform.position.y;
            var waterHeight = new HeightJob
            {
                WaveData = _waveData,
                Position = _samplePositionsA,
                OffsetLength = new int2(0, _positionCount),
                Time = t,
                Output = _gerstnerWavesA,
                WaveLevelOffset = offset,
                Opacity = _depthProfile,
            };

            _waterHeightHandle = waterHeight.Schedule(_positionCount, 32, default);

            JobHandle.ScheduleBatchedJobs();

            _firstFrame = false;
        }

        private static void CompleteJobs()
        {
            if (_firstFrame || !_processing)
                return;

            _waterHeightHandle.Complete();

            (_gerstnerWavesA, _gerstnerWavesB) = (_gerstnerWavesB, _gerstnerWavesA);
            _samplePositionsA.CopyFrom(_samplePositionsB);

            _processing = false;
        }

        // Gerstner Height C# Job
        [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
        internal struct HeightJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction]
            [ReadOnly] public NativeArray<float3> WaveData; // wave data stored in vec4's like the shader version but packed into one
            [ReadOnly] public NativeArray<float3> Position;

            [WriteOnly] public NativeArray<Data.WaveOutputData> Output;

            [ReadOnly] public float Time;
            [ReadOnly] public int2 OffsetLength;

            [ReadOnly] public float WaveLevelOffset;
            [ReadOnly] public NativeArray<float> Opacity;

            // The code actually running on the job
            public void Execute(int i)
            {
#if ZERO
                if (i < OffsetLength.x || i >= OffsetLength.y - OffsetLength.x)
                    return;
#endif // ZERO

                var waveCountMulti = 1f / WaveData.Length;
                var wavePos = new float3(0f, 0f, 0f);
                var waveNorm = new float3(0f, 0f, 0f);

                for (var wave = 0; wave < WaveData.Length; wave++) // for each wave
                {
                    // Wave data vars
                    var pos = Position[i].xz;

                    var amplitude = WaveData[wave].x;
                    var direction = math.radians(WaveData[wave].y); // convert the incoming degrees to radians
                    var wavelength = WaveData[wave].z;
                    ////////////////////////////////wave value calculations//////////////////////////
                    var wSpeed = math.sqrt(9.806f * wavelength); // frequency of the wave based off wavelength
                    float wa = wavelength * amplitude;
                    var qi = 2f / (wa * WaveData.Length);

                    math.sincos(direction, out var directionSin, out var directionCos);
                    var windDir = new float2(directionCos, directionSin); // calculate wind direction
                    var dir = math.dot(windDir, pos);

                    ////////////////////////////position output calculations/////////////////////////
                    var calc = dir * wavelength + -Time * wSpeed; // the wave calculation
                    math.sincos(calc, out var sinCalc, out var cosCalc); // sin (used for vertical undulation), cosine (used for horizontal undulation)

                    // calculate the offsets for the current point
                    wavePos.xz += amplitude * cosCalc * qi * windDir;
                    wavePos.y += sinCalc * amplitude * waveCountMulti; // the height is divided by the number of waves

                    ////////////////////////////normal output calculations/////////////////////////
                    // normal vector
                    var norm = new float3(-(cosCalc * wa * windDir),
                        1 - qi * wa * sinCalc);
                    waveNorm += amplitude * waveCountMulti * norm;
                }

                Data.WaveOutputData output = new Data.WaveOutputData();

                wavePos *= math.saturate(Opacity[i]);
                wavePos.xz += Position[i].xz;
                wavePos.y += WaveLevelOffset;
                output.Position = wavePos;

                waveNorm.xy *= Opacity[i];
                output.Normal = math.normalize(waveNorm.xzy);

                Output[i] = output;
            }
        }

#if ZERO
        [BurstCompile]
        private struct OpacityJob : IJobFor
        {
            [ReadOnly] public NativeArray<float> DepthValues;
            [ReadOnly] public NativeArray<float> DepthProfile;

            [WriteOnly] public NativeArray<float> Opacity;
            
            public void Execute(int index)
            {
                var profileIndex = 1.0f - math.saturate(-DepthValues[index] / 20.0f);
                
                profileIndex *= DepthProfile.Length;
                
                profileIndex = math.clamp(profileIndex, 0.0f, DepthProfile.Length - 1f);
                
                Opacity[index] = math.saturate(DepthProfile[(int)math.round(profileIndex)]);
            }
        }
#endif // ZERO
    }
}
