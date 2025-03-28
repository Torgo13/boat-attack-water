using System.Collections.Generic;
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
        private static int threadCount;
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
        private static NativeArray<float> _opacity, /*_waterDepth,*/ _depthProfile;

        // Job handles
        private static JobHandle _waterHeightHandle;

        /// <summary>
        /// Dictionary containing the objects GUID and positions to sample for buoyancy
        /// </summary>
        public static readonly Dictionary<int, int2> Registry = new Dictionary<int, int2>();

        public static void Init()
        {
            //Wave data
            Ocean ocean = Ocean.Instance;
            if (ocean == null)
                return;

            // Max sample count for quality level
            const int sampleCount = 4096;
            _samplePositionsA = new NativeArray<float3>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _samplePositionsB = new NativeArray<float3>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            _gerstnerWavesA = new NativeArray<Data.WaveOutputData>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _gerstnerWavesB = new NativeArray<Data.WaveOutputData>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            _opacity = new NativeArray<float>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            const int depthProfileCount = 32;
            _depthProfile = new NativeArray<float>(depthProfileCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < depthProfileCount; i++)
            {
                _depthProfile[i] = ocean.settingsData._waveDepthProfile.Evaluate(i / (float)depthProfileCount);
            }

            threadCount = SystemInfo.processorCount / 2;

            Initialized = true;
        }

        public static void Cleanup()
        {
            _waterHeightHandle.Complete();

            DepthGenerator.CleanUp();

            // Cleanup native arrays
            if (_waveData.IsCreated)
                _waveData.Dispose();

            if (_samplePositionsA.IsCreated)
                _samplePositionsA.Dispose();

            if (_samplePositionsB.IsCreated)
                _samplePositionsB.Dispose();

            if (_gerstnerWavesA.IsCreated)
                _gerstnerWavesA.Dispose();

            if (_gerstnerWavesB.IsCreated)
                _gerstnerWavesB.Dispose();

            if (_opacity.IsCreated)
                _opacity.Dispose();

            if (_depthProfile.IsCreated)
                _depthProfile.Dispose();

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

            _waterHeightHandle = waterHeight.Schedule(_positionCount, threadCount);

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
        [BurstCompile(FloatPrecision.Low, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
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
            public void Execute(int index)
            {
                if (index < OffsetLength.x || index >= OffsetLength.y - OffsetLength.x)
                    return;

                int waveDataLength = WaveData.Length;
                var waveCountMulti = 1f / waveDataLength;
                var wavePos = new float3(0f, 0f, 0f);
                var waveNorm = new float3(0f, 0f, 0f);

                for (var wave = 0; wave < waveDataLength; wave++) // for each wave
                {
                    // Wave data vars
                    var pos = Position[index].xz;

                    var amplitude = WaveData[wave].x;
                    var direction = math.radians(WaveData[wave].y); // convert the incoming degrees to radians
                    var wavelength = WaveData[wave].z;
                    ////////////////////////////////wave value calculations//////////////////////////
                    var wSpeed = math.sqrt(9.806f * wavelength); // frequency of the wave based off wavelength
                    float wa = wavelength * amplitude;
                    var qi = 2f / (wa * waveDataLength);

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
                    var norm = new float3(-(cosCalc * wa * windDir), 1 - qi * wa * sinCalc); // normal vector
                    waveNorm += amplitude * waveCountMulti * norm;
                }

                Data.WaveOutputData output = new Data.WaveOutputData();

                wavePos *= math.saturate(Opacity[index]);
                wavePos.xz += Position[index].xz;
                wavePos.y += WaveLevelOffset;
                output.Position = wavePos;

                waveNorm.xy *= Opacity[index];
                output.Normal = math.normalize(waveNorm.xzy);

                Output[index] = output;
            }
        }
    }
}
