using System;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;
using WaterSystem.Rendering;
using Unity.Collections;
using Unity.Mathematics;

namespace WaterSystem
{
    //[ExecuteAlways, DisallowMultipleComponent]
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

        // Render Passes
        public bool infiniteWater;
        private InfiniteWaterPass _infiniteWaterPass;
        private WaterFxPass _waterBufferPass;
        private WaterCausticsPass _causticsPass;

        // Runtime Materials
        private Material _causticMaterial;

        // Runtime Resources
        private Texture2D _rampTexture;

        // Shader props
        private static readonly int FoamMap = Shader.PropertyToID("_FoamMap");
        private static readonly int SurfaceMap = Shader.PropertyToID("_SurfaceMap");
        private static readonly int WaveHeight = Shader.PropertyToID("_WaveHeight");
        private static readonly int MaxWaveHeight = Shader.PropertyToID("_MaxWaveHeight");
        private static readonly int MaxDepth = Shader.PropertyToID("_MaxDepth");
        private static readonly int WaveCount = Shader.PropertyToID("_WaveCount");
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

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
            else if (_instance != this)
            {
#if UNITY_EDITOR || DEBUG
                Debug.LogError("Multiple Ocean Components cannot exist in tandem");
#endif // DEBUG
                //SafeDestroy(this);
            }
        }

        private void OnEnable()
        {
            LoadResources();

            _useComputeBuffer = !computeOverride && SystemInfo.supportsComputeShaders
                && Application.platform != RuntimePlatform.WebGLPlayer;

            RenderPipelineManager.beginCameraRendering += BeginCameraRendering;

            Init();
        }

        private void OnDisable()
        {
            Cleanup();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        void Cleanup()
        {
            GerstnerWavesJobs.Cleanup();
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;

            waveBuffer?.Dispose();

            // pass cleanup
            _waterBufferPass?.Cleanup();
            _infiniteWaterPass?.Cleanup();
            _causticsPass?.Cleanup();

            PlanarReflections.Cleanup();
        }

        private void BeginCameraRendering(ScriptableRenderContext src, Camera cam)
        {
            if (!WaterUtility.CanRender(gameObject, cam) || _instance == null)
                return;

            if (settingsData.refType == Data.ReflectionType.PlanarReflection)
                PlanarReflections.Execute(src, cam);

            if (_causticMaterial == null)
            {
                _causticMaterial = resources.causticMaterial;
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

            foreach (var mesh in resources.defaultWaterMeshes)
            {
                Graphics.DrawMesh(mesh,
                    matrix,
                    resources.defaultSeaMaterial,
                    gameObject.layer,
                    cam,
                    0,
                    null,
                    ShadowCastingMode.Off,
                    false, //true,
                    null,
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
            {
                GerstnerWavesJobs.UpdateHeights();
            }
        }

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

        public void FragWaveNormals(bool toggle)
        {
            var mat = GetComponent<Renderer>().sharedMaterial;
            if (toggle)
                mat.EnableKeyword("GERSTNER_WAVES");
            else
                mat.DisableKeyword("GERSTNER_WAVES");
        }

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

        private void SetWaves()
        {
            SetupWaves();

            // set default resources
            Shader.SetGlobalTexture(FoamMap, resources.defaultFoamMap);
            Shader.SetGlobalTexture(SurfaceMap, resources.defaultSurfaceMap);
            Shader.SetGlobalTexture(WaterFXShaderTag, resources.defaultWaterFX);
            Shader.SetGlobalTexture(DitherTexture, resources.ditherNoise);

            _maxWaveHeight = 0f;
            foreach (var w in GerstnerWavesJobs._waveData)
            {
                _maxWaveHeight += w.x;
            }

            _maxWaveHeight = Mathf.Max(_maxWaveHeight / GerstnerWavesJobs._waveData.Length, 0.5f);
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

            int waveCount = GerstnerWavesJobs._waveData.Length;
            Shader.SetGlobalInt(WaveCount, waveCount);

            // Check if the wave data can fit in a Graphics Buffer
            _useComputeBuffer &= waveCount * 12 <= SystemInfo.maxGraphicsBufferSize;

            //GPU side
            if (_useComputeBuffer)
            {
                Shader.EnableKeyword("USE_STRUCTURED_BUFFER");
                if (waveBuffer != null)
                    waveBuffer.Dispose();

                //waveBuffer = new ComputeBuffer(waveCount, Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<Data.Wave>());
                waveBuffer = new ComputeBuffer(waveCount, 12); // Data.Wave has 3 floats
                waveBuffer.SetData(GerstnerWavesJobs._waveData);
                Shader.SetGlobalBuffer(WaveDataBuffer, waveBuffer);
            }
            else
            {
                Shader.DisableKeyword("USE_STRUCTURED_BUFFER");
                int waveDataLength = GerstnerWavesJobs._waveData.Length;
                using var _0 = UnityEngine.Pool.ListPool<Vector4>.Get(out var waveData);
                if (waveData.Capacity < waveDataLength)
                    waveData.Capacity = waveDataLength;

                for (int i = 0; i < waveDataLength; i++)
                {
                    float3 wave = GerstnerWavesJobs._waveData[i];
                    waveData.Add(new Vector4(wave.x, wave.y, wave.z));
                }

                Shader.SetGlobalVectorArray(WaveData, waveData);
            }
        }

        private void GenerateColorRamp()
        {
            _rampTexture = resources.defaultFoamRamp;
            //TODO Fix null error when _rampTexture isn't allocated in resources
            if (_rampTexture == null)
            {
                const int rampCount = 2;
                const int rampRes = 128;

                var pixelHeight = Mathf.CeilToInt(rampCount / 4.0f);

                _rampTexture = new Texture2D(rampRes, pixelHeight, TextureFormat.RGBA32, 0, false, true)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };

                NativeArray<Color> cols = _rampTexture.GetPixelData<Color>(0);
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

        private void SetupWaves()
        {
            //create basic waves based off basic wave settings
            var backupSeed = Random.state;
            Random.InitState(settingsData.randomSeed);
            var basicWaves = settingsData._basicWaveSettings;
            var a = basicWaves.amplitude;
            var d = basicWaves.direction;
            var l = basicWaves.wavelength;
            var numWave = basicWaves.waveCount;
            if (!GerstnerWavesJobs._waveData.IsCreated)
            {
                GerstnerWavesJobs._waveData = new NativeArray<float3>(numWave, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            var r = 1f / numWave;

            for (var i = 0; i < numWave; i++)
            {
                var p = Mathf.Lerp(0.1f, 1.9f, i * r);
                var amp = a * p * Random.Range(0.66f, 1.24f);
                var dir = d + Random.Range(-90f, 90f);
                var len = Mathf.PI * 2f / (l * p * Random.Range(0.75f, 1.2f));
                GerstnerWavesJobs._waveData[i] = new float3(amp, dir, len);
                Random.InitState(settingsData.randomSeed + i + 1);
            }

            Random.state = backupSeed;
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
