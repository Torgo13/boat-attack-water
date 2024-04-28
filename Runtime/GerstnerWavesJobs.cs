using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

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
        //public static int _waveCount;
        private static int threadCount;
        public static NativeArray<float3> _waveData; // Wave data from the water system

        //Details for Buoyant Objects

        // worldspace sampling position buffer
        private static NativeArray<float3> _samplePositionsA;
        private static NativeArray<float3> _samplePositionsB;

        // position counter
        public static int _positionCount;

        // Gerstner calculated wave position and normals
        private static NativeArray<Data.WaveOutputData> _gerstnerWavesA, _gerstnerWavesB;

        // Depth data
        private static NativeArray<float> _opacity, /*_waterDepth,*/ _depthProfile;

        // Job handles
        //private static JobHandle _waterDepthHandle;
        //private static JobHandle _opacityHandle;
        private static JobHandle _waterHeightHandle;

        /// <summary>
        /// Dictionary containing the objects GUID and positions to sample for buoyancy
        /// </summary>
        public static readonly Dictionary<int, int2> Registry = new Dictionary<int, int2>();

        //Details for cameras
        //private static NativeArray<float3> _camPositions;
        //private static readonly Dictionary<Camera, float3> CamRegistry = new Dictionary<Camera, float3>();

        public static void Init()
        {
            /*if(Debug.isDebugBuild)
              Debug.Log("Initializing Gerstner Waves Jobs");*/

            //Wave data
            Ocean ocean = Ocean.Instance;
            if (ocean == null)
            {
                return;
            }

            //_waveCount = ocean.waves.Length;
            //_waveData = new NativeArray<float3>(_waveCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            // Max sample count for quality level - TODO need to make sure switching quality levels works
            const int sampleCount = 4096;
            _samplePositionsA = new NativeArray<float3>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _samplePositionsB = new NativeArray<float3>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            _gerstnerWavesA = new NativeArray<Data.WaveOutputData>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _gerstnerWavesB = new NativeArray<Data.WaveOutputData>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            _opacity = new NativeArray<float>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            //_waterDepth = new NativeArray<float>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            const int depthProfileCount = 32;
            _depthProfile = new NativeArray<float>(depthProfileCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < depthProfileCount; i++)
            {
                _depthProfile[i] = ocean.settingsData._waveDepthProfile.Evaluate((float)i / depthProfileCount);
            }

            threadCount = SystemInfo.processorCount / 2;

            Initialized = true;
        }

        public static void Cleanup()
        {
            /*if(Debug.isDebugBuild)
              Debug.Log("Cleaning up Gerstner Wave Jobs");*/
            _waterHeightHandle.Complete();

            DepthGenerator.CleanUp();

            // Cleanup native arrays
            if (_waveData.IsCreated)
            {
                _waveData.Dispose();
            }

            if (_samplePositionsA.IsCreated)
            {
                _samplePositionsA.Dispose();
            }

            if (_samplePositionsB.IsCreated)
            {
                _samplePositionsB.Dispose();
            }

            if (_gerstnerWavesA.IsCreated)
            {
                _gerstnerWavesA.Dispose();
            }

            if (_gerstnerWavesB.IsCreated)
            {
                _gerstnerWavesB.Dispose();
            }

            if (_opacity.IsCreated)
            {
                _opacity.Dispose();
            }
            /*if (_waterDepth.IsCreated)
                _waterDepth.Dispose();*/
            if (_depthProfile.IsCreated)
            {
                _depthProfile.Dispose();
            }

            Initialized = false;
        }

        public static void UpdateSamplePoints(ref NativeArray<float3> samplePoints, int guid)
        {
            if (Registry.TryGetValue(guid, out int2 offsets))
            {
                for (int i = offsets.x; i < offsets.y; i++)
                {
                    _samplePositionsB[i] = samplePoints[i - offsets.x];
                }
            }
            else
            {
                if (_positionCount + samplePoints.Length >= _samplePositionsB.Length)
                {
                    return;
                }

                offsets = new int2(_positionCount, _positionCount + samplePoints.Length);
                Registry.Add(guid, offsets);
                _positionCount += samplePoints.Length;
            }
        }

        public static void RemoveSamplePoints(int guid)
        {
            if (!Registry.TryGetValue(guid, out int2 offsets))
            {
                return;
            }

            int min = offsets.x;
            int size = offsets.y - min;

            Registry.Remove(guid);
            List<int> keys = new List<int>(Registry.Keys);
            int keysCount = keys.Count;
            for (int i = 0; i < keysCount; i++)
            {
                int offsetEntry = keys[i];
                int2 entry = Registry[offsetEntry];
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
            {
                return;
            }

            _gerstnerWavesB.Slice(offsets.x, offsets.y - offsets.x).CopyTo(output);
        }

        // Height jobs for the next frame
        public static void UpdateHeights()
        {
            CompleteJobs();

            if (_processing || Ocean.Instance == null)
            {
                return;
            }

            _processing = true;

#if STATIC_EVERYTHING
            var t = 0.0f;
#else
            float t = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
#endif

            // Test Water Depth
            /*DepthGenerator.WaterDepth waterDepth = new DepthGenerator.WaterDepth
            {
                Position = _samplePositionsA,
                DepthData = DepthGenerator._depthData,
                DepthValues = DepthGenerator._globalDepthValues,
                Depth = _waterDepth,
            };
            _waterDepthHandle = waterDepth.Schedule(_positionCount, threadCount);*/

            // Generate Opacity Values
            /*OpacityJob opacity = new OpacityJob
            {
                DepthProfile = _depthProfile,
                //DepthValues = _waterDepth,
                Opacity = _opacity,
            };

            _opacityHandle = opacity.Schedule(_positionCount, threadCount);*/

            // Gerstner Wave Height
            float offset = Ocean.Instance.transform.position.y;
            HeightJob waterHeight = new HeightJob
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
            {
                return;
            }

            _waterHeightHandle.Complete();

            (_gerstnerWavesA, _gerstnerWavesB) = (_gerstnerWavesB, _gerstnerWavesA);
            _samplePositionsA.CopyFrom(_samplePositionsB);

            _processing = false;
        }

        // Gerstner Height C# Job
        [BurstCompile]
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
                if (i < OffsetLength.x || i >= OffsetLength.y - OffsetLength.x)
                {
                    return;
                }

                int waveDataLength = WaveData.Length;
                float waveCountMulti = 1f / waveDataLength;
                float3 wavePos = new float3(0f, 0f, 0f);
                float3 waveNorm = new float3(0f, 0f, 0f);

                for (int wave = 0; wave < waveDataLength; wave++) // for each wave
                {
                    // Wave data vars
                    float2 pos = Position[i].xz;

                    float amplitude = WaveData[wave].x;
                    float direction = math.radians(WaveData[wave].y); // convert the incoming degrees to radians
                    float wavelength = WaveData[wave].z;
                    ////////////////////////////////wave value calculations//////////////////////////
                    float wSpeed = math.sqrt(9.806f * wavelength); // frequency of the wave based off wavelength
                    float wa = wavelength * amplitude;
                    float qi = 2f / (wa * waveDataLength);

                    math.sincos(direction, out var directionSin, out var directionCos);
                    float2 windDir = new float2(directionCos, directionSin); // calculate wind direction

                    //windDir = math.normalize(windDir);
                    float dir = math.dot(windDir, pos);

                    ////////////////////////////position output calculations/////////////////////////
                    float calc = dir * wavelength + -Time * wSpeed; // the wave calculation
                    math.sincos(calc, out var sinCalc, out var cosCalc); // sin (used for vertical undulation), cosine (used for horizontal undulation)

                    // calculate the offsets for the current point
                    wavePos.xz += qi * amplitude * windDir * cosCalc;
                    wavePos.y += sinCalc * amplitude * waveCountMulti; // the height is divided by the number of waves

                    ////////////////////////////normal output calculations/////////////////////////
                    float3 norm = new float3(-(windDir * wa * cosCalc), 1 - qi * wa * sinCalc); // normal vector
                    waveNorm += norm * waveCountMulti * amplitude;
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

        /*[BurstCompile]
        private struct OpacityJob : IJobParallelFor
        {
            //[ReadOnly] public NativeArray<float> DepthValues;
            [ReadOnly] public NativeArray<float> DepthProfile;

            [WriteOnly] public NativeArray<float> Opacity;

            public void Execute(int index)
            {
                int length = DepthProfile.Length;
                Opacity[index] = math.saturate(math.pow(DepthProfile[(int)math.round(math.clamp((1.0f  - math.saturate(-DepthValues[index] / 20.0f)) * length, 0.0f, length - 1f))], 0.4545f));
            }
        }*/
    }
}