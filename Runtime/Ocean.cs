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
#if UNITY_2023_1_OR_NEWER
                    _instance = (Ocean)FindFirstObjectByType(typeof(Ocean));
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
        //private InfiniteWaterPass _infiniteWaterPass;
        private WaterFxPass _waterBufferPass;
        private WaterCausticsPass _causticsPass;

        // Runtime Materials
        private Material _causticMaterial;

        // Runtime Resources
        private Texture2D _rampTexture;

        // Shader props
        private static readonly int CameraRoll = Shader.PropertyToID("_CameraRoll");
        private static readonly int InvViewProjection = Shader.PropertyToID("_InvViewProjection");
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
        private const string _SSR_SAMPLES_MEDIUM = "_SSR_SAMPLES_MEDIUM";
        private const string _SSR_SAMPLES_HIGH = "_SSR_SAMPLES_HIGH";
        private const string USE_STRUCTURED_BUFFER = "USE_STRUCTURED_BUFFER";

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
#endif
                //SafeDestroy(this);
            }
        }

        void OnEnable()
        {
            LoadResources();

            _useComputeBuffer = !computeOverride && SystemInfo.supportsComputeShaders && Application.platform != RuntimePlatform.WebGLPlayer;

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
            {
                _instance = null;
            }
        }

        void Cleanup()
        {
            GerstnerWavesJobs.Cleanup();
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;

            if (waveBuffer != null)
            {
                waveBuffer.Dispose();
                waveBuffer = null;
            }

            // pass cleanup
            //_infiniteWaterPass = null;

            if (_waterBufferPass != null)
            {
                _waterBufferPass.Cleanup();
                _waterBufferPass = null;
            }

            if (_causticsPass != null)
            {
                _causticsPass.Cleanup();
                _causticsPass = null;
            }

            PlanarReflections.Cleanup();
        }

        private void BeginCameraRendering(ScriptableRenderContext src, Camera cam)
        {
            if (!WaterUtility.CanRender(gameObject, cam) || _instance == null)
            {
                return;
            }

            // Fallback to Screen Space Reflections if the renderscale has been changed
            /*if (settingsData.refType == Data.ReflectionType.PlanarReflection && ((UniversalRenderPipelineAsset)QualitySettings.renderPipeline).renderScale != 1f)
            {
                settingsData.refType = Data.ReflectionType.ScreenSpaceReflection;
            }*/

            if (settingsData.refType == Data.ReflectionType.PlanarReflection)
            {
                PlanarReflections.Execute(src, cam);
            }

            if (_causticMaterial == null)
            {
                _causticMaterial = resources.causticMaterial;
                _causticMaterial.SetTexture(CausticMap, resources.defaultSurfaceMap);
            }

            //_infiniteWaterPass ??= new InfiniteWaterPass(resources.defaultInfiniteWaterMesh, resources.infiniteWaterShader);
            if (_waterBufferPass == null)
            {
                _waterBufferPass = new WaterFxPass();
            }
            if (_causticsPass == null)
            {
                _causticsPass = new WaterCausticsPass(_causticMaterial);
            }

            UniversalAdditionalCameraData urpData = cam.GetUniversalAdditionalCameraData();
            //urpData.scriptableRenderer.EnqueuePass(_infiniteWaterPass);
            urpData.scriptableRenderer.EnqueuePass(_waterBufferPass);
            urpData.scriptableRenderer.EnqueuePass(_causticsPass);

            float roll = cam.transform.localEulerAngles.z;
            Shader.SetGlobalFloat(CameraRoll, roll);
            Shader.SetGlobalMatrix(InvViewProjection,
                (GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix).inverse);

            // Water matrix
            const float quantizeValue = 6.25f;
            const float forwards = 10f;
            const float yOffset = -0.25f;

            Vector3 newPos = cam.transform.TransformPoint(Vector3.forward * forwards);
            newPos.y = yOffset + _instance.transform.position.y;
            newPos.x = quantizeValue * (int)(newPos.x / quantizeValue);
            newPos.z = quantizeValue * (int)(newPos.z / quantizeValue);

            float blendDist = (settingsData.distanceBlend + 10) / 100f;

            Matrix4x4 matrix = Matrix4x4.TRS(newPos, Quaternion.identity, Vector3.one * blendDist); // transform.localToWorldMatrix;

            int defaultWaterMeshesLength = resources.defaultWaterMeshes.Length;
            for (int i = 0; i < defaultWaterMeshesLength; i++)
            {
                Graphics.DrawMesh(resources.defaultWaterMeshes[i],
                    matrix,
                    resources.defaultSeaMaterial,
                    gameObject.layer,
                    cam,
                    0,
                    null,
                    ShadowCastingMode.Off,
                    false,
                    null,
                    LightProbeUsage.Off);
            }
        }

        private static void SafeDestroy(Object obj, bool immediate = false)
        {
            if (obj != null)
            {
#if UNITY_EDITOR
                if (immediate)
                {
                    DestroyImmediate(obj);
                }
                else
                {
                    EditorApplication.delayCall += () => DestroyImmediate(obj);
                }
#else
                Object.Destroy(obj);
#endif
            }
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
            if (!GerstnerWavesJobs.Initialized)
            {
                GerstnerWavesJobs.Init();
            }
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

        int _reflectionTypes;
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
            int waveDataLength = GerstnerWavesJobs._waveData.Length;
            for (int i = 0; i < waveDataLength; i++)
            {
                _maxWaveHeight += GerstnerWavesJobs._waveData[i].x;
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
                {
                    Shader.EnableKeyword(Data.GetReflectionKeyword(reflect));
                }
                else
                {
                    Shader.DisableKeyword(Data.GetReflectionKeyword(reflect));
                }
            }

            if (settingsData.refType == Data.ReflectionType.ScreenSpaceReflection)
            {
                Vector4 settings = new Vector4(settingsData.SsrSettings.StepSize,
                        settingsData.SsrSettings.Thickness, 0, 0);
                Shader.SetGlobalVector(SsrSettings, settings);
                switch (settingsData.SsrSettings.Steps)
                {
                    case Data.SSRSteps.High:
                        Shader.EnableKeyword(_SSR_SAMPLES_HIGH);
                        Shader.DisableKeyword(_SSR_SAMPLES_MEDIUM);
                        break;
                    case Data.SSRSteps.Medium:
                        Shader.DisableKeyword(_SSR_SAMPLES_HIGH);
                        Shader.EnableKeyword(_SSR_SAMPLES_MEDIUM);
                        break;
                    default:
                        Shader.DisableKeyword(_SSR_SAMPLES_HIGH);
                        Shader.DisableKeyword(_SSR_SAMPLES_MEDIUM);
                        break;
                }
            }

            int waveCount = GerstnerWavesJobs._waveData.Length;
            Shader.SetGlobalInt(WaveCount, waveCount);

            //GPU side
            if (_useComputeBuffer && waveCount * 12 <= SystemInfo.maxGraphicsBufferSize)
            {
                Shader.EnableKeyword(USE_STRUCTURED_BUFFER);
                if (waveBuffer != null)
                {
                    waveBuffer.Dispose();
                }
                //waveBuffer = new ComputeBuffer(waveCount, Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<Data.Wave>());
                waveBuffer = new ComputeBuffer(waveCount, 12); // Data.Wave has 3 floats
                waveBuffer.SetData(GerstnerWavesJobs._waveData);
                Shader.SetGlobalBuffer(WaveDataBuffer, waveBuffer);
            }
            else
            {
                Shader.DisableKeyword(USE_STRUCTURED_BUFFER);
                Vector4[] waveData = new Vector4[GerstnerWavesJobs._waveData.Length];
                int wavesCount = GerstnerWavesJobs._waveData.Length;
                for (int i = 0; i < wavesCount; i++)
                {
                    float3 wave = GerstnerWavesJobs._waveData[i];
                    waveData[i] = new Vector4(wave.x, wave.y, wave.z);
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

                int pixelHeight = Mathf.CeilToInt(rampCount / 4.0f);

                _rampTexture = new Texture2D(rampRes, pixelHeight, TextureFormat.RGBA32, 0, false, true)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };

                NativeArray<Color32> cols = _rampTexture.GetPixelData<Color32>(0);
                for (int i = 0; i < rampRes; i++)
                {
                    float temp = i / (float)rampRes;
                    cols[i] = new Color32(
                        (byte)(255f * Mathf.LinearToGammaSpace(settingsData._shoreFoamProfile.Evaluate(temp))), // Foam shore
                        (byte)(255f * Mathf.LinearToGammaSpace(settingsData._waveFoamProfile.Evaluate(temp))),  // Foam Gerstner waves
                        (byte)(255f * Mathf.LinearToGammaSpace(settingsData._waveDepthProfile.Evaluate(temp))), // Depth Gerstner waves
                        255);
                }

                _rampTexture.Apply();
                cols.Dispose();
            }

            Shader.SetGlobalTexture(RampTexture, _rampTexture);
        }

        private void SetupWaves()
        {
            //create basic waves based off basic wave settings
            Random.State backupSeed = Random.state;
            Random.InitState(settingsData.randomSeed);
            Data.BasicWaves basicWaves = settingsData._basicWaveSettings;
            float a = basicWaves.amplitude;
            float d = basicWaves.direction;
            float l = basicWaves.wavelength;
            int numWave = basicWaves.waveCount;
            if (!GerstnerWavesJobs._waveData.IsCreated)
            {
                GerstnerWavesJobs._waveData = new NativeArray<float3>(numWave, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            float r = 1f / numWave;

            for (int i = 0; i < numWave; i++)
            {
                float p = Mathf.Lerp(0.1f, 1.9f, i * r);
                float amp = a * p * Random.Range(0.66f, 1.24f);
                float dir = d + Random.Range(-90f, 90f);
                float len = Mathf.PI * 2f / (l * p * Random.Range(0.75f, 1.2f));
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
