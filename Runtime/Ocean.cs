using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;
using WaterSystem.Rendering;

namespace WaterSystem
{
#if ZERO
    [ExecuteAlways, DisallowMultipleComponent]
#endif // ZERO
    [AddComponentMenu("URP Water System/Ocean")]
    public class Ocean : MonoBehaviour
    {
        // Singleton
        private static Ocean _instance;
        public static Ocean Instance
        {
            get
            {
                if (_instance == null)
                {
#if UNITY_2022_3_OR_NEWER
                    _instance = FindFirstObjectByType<Ocean>();
#else
                    _instance = FindObjectOfType<Ocean>();
#endif
                }

                return _instance;
            }
        }

        // Script references
        private bool _useComputeBuffer;
        public bool computeOverride;

        private ComputeBuffer waveBuffer;
        private float _maxWaveHeight;
        private float _waveHeight;

        [SerializeReference] public Data.OceanSettings settingsData = new Data.OceanSettings();
        [HideInInspector, SerializeField] private WaterResources resources;

        public DebugShading shadingDebug;

#if RENDERPASS
        // Render Passes
        public bool infiniteWater;
        private InfiniteWaterPass _infiniteWaterPass;
        private WaterFxPass _waterBufferPass;
        private WaterCausticsPass _causticsPass;

        // Runtime Materials
        private Material _causticMaterial;
#endif // RENDERPASS

        // Runtime Resources
        private Texture2D _rampTexture;

        // Shader props
        private static readonly int FoamMap = Shader.PropertyToID("_FoamMap");
        private static readonly int SurfaceMap = Shader.PropertyToID("_SurfaceMap");
        private static readonly int WaveHeight = Shader.PropertyToID("_WaveHeight");
        private static readonly int MaxWaveHeight = Shader.PropertyToID("_MaxWaveHeight");
        private static readonly int MaxDepth = Shader.PropertyToID("_MaxDepth");
        private static readonly int WaveCount = Shader.PropertyToID("_WaveCount");
        private static readonly int CubemapTexture = Shader.PropertyToID("_CubemapTexture");
        private static readonly int WaveDataBuffer = Shader.PropertyToID("_WaveDataBuffer");
        private static readonly int WaveData = Shader.PropertyToID("waveData");
        private static readonly int WaterFXShaderTag = Shader.PropertyToID("_WaterFXMap");
        private static readonly int DitherTexture = Shader.PropertyToID("_DitherPattern");
        private static readonly int BoatAttackWaterDebugPass = Shader.PropertyToID("_BoatAttack_Water_DebugPass");
        private static readonly int BoatAttackWaterDistanceBlend = Shader.PropertyToID("_BoatAttack_Water_DistanceBlend");
        private static readonly int AbsorptionColor = Shader.PropertyToID("_AbsorptionColor");
        private static readonly int ScatteringColor = Shader.PropertyToID("_ScatteringColor");
        private static readonly int BoatAttackWaterMicroWaveIntensity = Shader.PropertyToID("_BoatAttack_Water_MicroWaveIntensity");
        private static readonly int BoatAttackWaterFoamIntensity = Shader.PropertyToID("_BoatAttack_water_FoamIntensity");
        private static readonly int RampTexture = Shader.PropertyToID("_BoatAttack_RampTexture");
        private static readonly int CausticMap = Shader.PropertyToID("_CausticMap");
        private static readonly int SsrSettings = Shader.PropertyToID("_SSR_Settings");

        private int _reflectionTypes;
        public int ReflectionTypes
        {
            get
            {
                if (_reflectionTypes == 0)
                {
                    _reflectionTypes = Enum.GetValues(typeof(Data.ReflectionType)).Length;
                }

                return _reflectionTypes;
            }
        }

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
            else if (_instance != this)
            {
#if DEBUG
                Debug.LogError("Multiple Ocean Components cannot exist in tandem");
#endif // DEBUG

                SafeDestroy(this);
            }

            LoadResources();

            _useComputeBuffer = !computeOverride && SystemInfo.supportsComputeShaders
                && Application.platform != RuntimePlatform.WebGLPlayer;
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
            Init();
        }

        private void OnDisable()
        {
            Cleanup();
        }

        private void OnDestroy()
        {
            if (GerstnerWavesJobs._waveData.IsCreated)
                GerstnerWavesJobs._waveData.Dispose();
            
            if (_instance == this)
                _instance = null;
        }

        void Cleanup()
        {
            GerstnerWavesJobs.Cleanup();
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;

            waveBuffer?.Dispose();

#if RENDERPASS
            // pass cleanup
            _waterBufferPass?.Cleanup();
            _infiniteWaterPass?.Cleanup();
            _causticsPass?.Cleanup();
#endif // RENDERPASS

            PlanarReflections.Cleanup();
        }

        private void BeginCameraRendering(ScriptableRenderContext src, Camera cam)
        {
            if (!WaterUtility.CanRender(gameObject, cam) || _instance == null)
                return;

            if (settingsData.refType == Data.ReflectionType.PlanarReflection)
                PlanarReflections.Execute(src, cam);

#if RENDERPASS
            if (_causticMaterial == null)
            {
                _causticMaterial = CoreUtils.CreateEngineMaterial(resources.causticShader);
                _causticMaterial.SetTexture(CausticMap, resources.defaultSurfaceMap);
            }

            if (infiniteWater)
                _infiniteWaterPass ??= new InfiniteWaterPass(resources.defaultInfiniteWaterMesh, resources.infiniteWaterShader);

            _waterBufferPass ??= new WaterFxPass();
            _causticsPass ??= new WaterCausticsPass(_causticMaterial);

            var urpData = cam.GetUniversalAdditionalCameraData();

            if (infiniteWater)
                urpData.scriptableRenderer.EnqueuePass(_infiniteWaterPass);

            urpData.scriptableRenderer.EnqueuePass(_waterBufferPass);
            urpData.scriptableRenderer.EnqueuePass(_causticsPass);
#endif // RENDERPASS

            // Water matrix
            const float quantizeValue = 6.25f;
            const float forwards = 10f;
            const float yOffset = -0.25f;

            var newPos = cam.transform.TransformPoint(Vector3.forward * forwards);
            newPos.y = yOffset + _instance.transform.position.y;
            newPos.x = quantizeValue * (int)(newPos.x / quantizeValue);
            newPos.z = quantizeValue * (int)(newPos.z / quantizeValue);

            var blendDist = (settingsData.distanceBlend + 10) / 100f;

            var matrix = Matrix4x4.TRS(newPos, Quaternion.identity, Vector3.one * blendDist); // transform.localToWorldMatrix;
            int layer = gameObject.layer;

            foreach (var mesh in resources.defaultWaterMeshes)
            {
                Graphics.DrawMesh(mesh,
                    matrix,
                    resources.defaultSeaMaterial,
                    layer,
                    cam,
                    submeshIndex: 0,
                    properties: null,
                    ShadowCastingMode.Off,
                    receiveShadows: false, //true,
                    probeAnchor: null,
                    LightProbeUsage.Off);
            }
        }
        
        private static void SafeDestroy(Object o)
        {
            if (Application.isPlaying)
                Destroy(o);
            else
                DestroyImmediate(o);
        }

        void LoadResources()
        {
            if (resources == null)
            {
#if UNITY_EDITOR
                resources = AssetDatabase.LoadAssetAtPath<WaterResources>("Packages/com.unity.urp-water-system/Runtime/Data/WaterResources.asset");
#else
                resources = (WaterResources)Resources.Load("WaterResources");
#endif
            }
        }

        [ContextMenu("Init")]
        public void Init()
        {
            GenerateColorRamp();
            SetWaves();

            PlanarReflections.m_planeOffset = transform.position.y - 0.25f;
            PlanarReflections.m_settings = settingsData.planarSettings;
            PlanarReflections.m_settings.m_ClipPlaneOffset = 0; //transform.position.y;

            SetDebugMode(shadingDebug);

            // CPU side
            if (GerstnerWavesJobs.Initialized == false)
                GerstnerWavesJobs.Init();
        }

        private void LateUpdate()
        {
            if (GerstnerWavesJobs.Initialized)
                GerstnerWavesJobs.UpdateHeights();
        }

        [System.Diagnostics.Conditional("DEBUG")]
        public static void SetDebugMode(DebugShading mode)
        {
            if (mode != DebugShading.none)
            {
                Shader.EnableKeyword("BOAT_ATTACK_WATER_DEBUG_DISPLAY");
                Shader.SetGlobalInt(BoatAttackWaterDebugPass, (int)mode);
            }
            else
            {
                Shader.DisableKeyword("BOAT_ATTACK_WATER_DEBUG_DISPLAY");
            }
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public void FragWaveNormals(bool toggle)
        {
            var mat = GetComponent<Renderer>().sharedMaterial;
            if (toggle)
                mat.EnableKeyword("GERSTNER_WAVES");
            else
                mat.DisableKeyword("GERSTNER_WAVES");
        }

        private void SetWaves()
        {
            SetupWaves();
            NativeArray<float3> waves = GerstnerWavesJobs._waveData;

            // set default resources
            Shader.SetGlobalTexture(FoamMap, resources.defaultFoamMap);
            Shader.SetGlobalTexture(SurfaceMap, resources.defaultSurfaceMap);
            Shader.SetGlobalTexture(WaterFXShaderTag, resources.defaultWaterFX);
            Shader.SetGlobalTexture(DitherTexture, resources.ditherNoise);

            _maxWaveHeight = 0f;
            foreach (var w in waves)
            {
                _maxWaveHeight += w.x;
            }

            _maxWaveHeight = Mathf.Max(_maxWaveHeight / waves.Length, 0.5f);
            _waveHeight = transform.position.y;

            Shader.SetGlobalColor(AbsorptionColor, settingsData._absorptionColor.gamma);
            Shader.SetGlobalColor(ScatteringColor, settingsData._scatteringColor.linear);

            Shader.SetGlobalFloat(WaveHeight, _waveHeight);
            Shader.SetGlobalFloat(BoatAttackWaterMicroWaveIntensity, settingsData._microWaveIntensity);
            Shader.SetGlobalFloat(MaxWaveHeight, _maxWaveHeight);
            Shader.SetGlobalFloat(MaxDepth, settingsData._waterMaxVisibility);
            Shader.SetGlobalFloat(BoatAttackWaterDistanceBlend, settingsData.distanceBlend);
            Shader.SetGlobalFloat(BoatAttackWaterFoamIntensity, settingsData._foamIntensity);

            for (int i = 0; i < ReflectionTypes; i++)
            {
                Data.ReflectionType reflect = (Data.ReflectionType)i;

                if (settingsData.refType == reflect)
                    Shader.EnableKeyword(Data.GetReflectionKeyword(reflect));
                else
                    Shader.DisableKeyword(Data.GetReflectionKeyword(reflect));
            }

            if (settingsData.refType == Data.ReflectionType.ScreenSpaceReflection)
            {
                Vector4 settings = new Vector4(settingsData.SsrSettings.StepSize,
                    settingsData.SsrSettings.Thickness, 0, 0);

                Shader.SetGlobalVector(SsrSettings, settings);

                switch (settingsData.SsrSettings.Steps)
                {
                    case Data.SSRSteps.High:
                        Shader.EnableKeyword("_SSR_SAMPLES_HIGH");
                        Shader.DisableKeyword("_SSR_SAMPLES_MEDIUM");
                        break;
                    case Data.SSRSteps.Medium:
                        Shader.DisableKeyword("_SSR_SAMPLES_HIGH");
                        Shader.EnableKeyword("_SSR_SAMPLES_MEDIUM");
                        break;
                    default:
                        Shader.DisableKeyword("_SSR_SAMPLES_HIGH");
                        Shader.DisableKeyword("_SSR_SAMPLES_MEDIUM");
                        break;
                }
            }

            Shader.SetGlobalInt(WaveCount, waves.Length);

            // Check if the wave data can fit in a Graphics Buffer
            _useComputeBuffer &= waves.Length * 12 <= SystemInfo.maxGraphicsBufferSize;

            //GPU side
            if (_useComputeBuffer)
            {
                Shader.EnableKeyword("USE_STRUCTURED_BUFFER");
                waveBuffer?.Dispose();
#if ZERO
                waveBuffer = new ComputeBuffer(waves.Length, Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<Data.Wave>());
#else
                waveBuffer = new ComputeBuffer(waves.Length, 12); // Data.Wave has 3 floats
#endif // ZERO
                waveBuffer.SetData(waves);
                Shader.SetGlobalBuffer(WaveDataBuffer, waveBuffer);
            }
            else
            {
                Shader.DisableKeyword("USE_STRUCTURED_BUFFER");

                UnityEngine.Pool.ListPool<Vector4>.Get(out var waveData);
                Shader.SetGlobalVectorArray(WaveData, GetWaveData(waveData, waves));
                UnityEngine.Pool.ListPool<Vector4>.Release(waveData);
            }
        }

        private void GenerateColorRamp()
        {
            _rampTexture = resources.defaultFoamRamp;

            if (_rampTexture == null)
            {
                const int rampCount = 2;
                const int rampRes = 128;

                var pixelHeight = Mathf.CeilToInt(rampCount / 4.0f);

                _rampTexture = new Texture2D(rampRes, pixelHeight, TextureFormat.RGBA32,
                    mipCount: 0, linear: false, createUninitialized: true)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };

                var cols = _rampTexture.GetPixelData<Color32>(mipLevel: 0);
                for (var i = 0; i < rampRes; i++)
                {
                    float temp = i / (float)rampRes;
                    cols[i] = new Color(
                        Mathf.LinearToGammaSpace(settingsData._shoreFoamProfile.Evaluate(temp)), // Foam shore
                        Mathf.LinearToGammaSpace(settingsData._waveFoamProfile.Evaluate(temp)),  // Foam Gerstner waves
                        Mathf.LinearToGammaSpace(settingsData._waveDepthProfile.Evaluate(temp))); // Depth Gerstner waves
                }

                _rampTexture.Apply();
            }

            Shader.SetGlobalTexture(RampTexture, _rampTexture);
        }

        private System.Collections.Generic.List<Vector4> GetWaveData(
            System.Collections.Generic.List<Vector4> waveData, NativeArray<float3> waves)
        {
            if (waveData.Capacity < waves.Length)
                waveData.Capacity = waves.Length;

            for (int i = 0; i < waves.Length; i++)
            {
                waveData.Add((Vector3)waves[i]);
            }

            return waveData;
        }

        private void SetupWaves()
        {
            //create basic waves based off basic wave settings
            var basicWaves = settingsData._basicWaveSettings;
            var numWave = basicWaves.waveCount;
            if (!GerstnerWavesJobs._waveData.IsCreated)
            {
                GerstnerWavesJobs._waveData = new NativeArray<float3>(numWave,
                    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            var setupWavesJob = new SetupWavesJob
            {
                r = 1f / numWave,
                a = basicWaves.amplitude,
                d = basicWaves.direction,
                l = basicWaves.wavelength,
                randomSeed = settingsData.randomSeed,
                waveData = GerstnerWavesJobs._waveData,
            };
            
            setupWavesJob.RunByRef(numWave);
        }

        [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
        public struct SetupWavesJob : IJobFor
        {
            [ReadOnly] public float r;
            [ReadOnly] public float a;
            [ReadOnly] public float d;
            [ReadOnly] public float l;
            [ReadOnly] public int randomSeed;
            [WriteOnly] public NativeArray<float3> waveData;
            
            public void Execute(int i)
            {
                uint seed = math.max(1, unchecked((uint)(randomSeed + i)));
                var random = new Unity.Mathematics.Random(seed);
                var p = math.lerp(0.1f, 1.9f, i * r);
                var amp = a * p * random.NextFloat(0.66f, 1.24f);
                var dir = d + random.NextFloat(-90f, 90f);
                var len = math.PI2 / (l * p * random.NextFloat(0.75f, 1.2f));
                waveData[i] = new float3(amp, dir, len);
            }
        }

        [Serializable]
        public enum DebugMode { none, stationary, screen }

        [Serializable]
        public enum DebugShading
        {
            none,
            normalWS,
            Reflection,
            Refraction,
            Specular,
            SSS,
            Foam,
            FoamMask,
            WaterBufferA,
            WaterBufferB,
            Depth,
            WaterDepth,
            Fresnel,
        }
    }
}
