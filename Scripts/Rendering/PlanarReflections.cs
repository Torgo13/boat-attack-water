using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Serialization;
using Unity.Mathematics;

namespace UnityEngine.Rendering.Universal
{
    [ExecuteAlways]
    sealed
    public class PlanarReflections : MonoBehaviour
    {
        [Serializable]
        public enum ResolutionMultiplier
        {
            Full,
            Half,
            Third,
            Quarter
        }

        [Serializable]
        public class PlanarReflectionSettings
        {
            public ResolutionMultiplier m_ResolutionMultiplier = ResolutionMultiplier.Third;
            public float m_ClipPlaneOffset = 0.07f;
            public LayerMask m_ReflectLayers = -1;
            public bool m_Shadows;
        }

        [SerializeField]
        public PlanarReflectionSettings m_settings = new PlanarReflectionSettings();

        public GameObject target;
        [FormerlySerializedAs("camOffset")] public float m_planeOffset;

        private static Camera _reflectionCamera;
        private RenderTexture _reflectionTexture;
        private readonly int _planarReflectionTextureId = Shader.PropertyToID("_PlanarReflectionTexture");

        private int2 _oldReflectionTextureSize;

        public static event Action<ScriptableRenderContext, Camera> BeginPlanarReflections;

#if WATER_RENDER_REQUEST
#else
        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += ExecutePlanarReflections;
        }

        // Cleanup all the objects we possibly have created
        private void OnDisable()
        {
            Cleanup();
        }
#endif // WATER_RENDER_REQUEST

        private void OnDestroy()
        {
            Cleanup();
        }

        private void Cleanup()
        {
#if WATER_RENDER_REQUEST
#else
            RenderPipelineManager.beginCameraRendering -= ExecutePlanarReflections;
#endif // WATER_RENDER_REQUEST

            if (_reflectionCamera)
            {
                _reflectionCamera.targetTexture = null;
                SafeDestroy(_reflectionCamera.gameObject);
            }
            if (_reflectionTexture)
            {
                RenderTexture.ReleaseTemporary(_reflectionTexture);
            }
        }

        private static void SafeDestroy(Object obj)
        {
            if (!Application.isPlaying)
            {
                DestroyImmediate(obj);
            }
            else
            {
                Destroy(obj);
            }
        }

        private void UpdateCamera(Camera src, Camera dest)
        {
            if (dest == null) return;

            dest.CopyFrom(src);
            dest.useOcclusionCulling = false;
            if (dest.TryGetComponent(out UniversalAdditionalCameraData camData))
            {
                camData.renderShadows = m_settings.m_Shadows; // turn off shadows for the reflection camera
            }

            dest.allowMSAA = false;
            dest.allowDynamicResolution = true;
            dest.forceIntoRenderTexture = true;
        }

        private void UpdateReflectionCamera([System.Diagnostics.CodeAnalysis.NotNull] Camera realCamera)
        {
            if (_reflectionCamera == null)
                _reflectionCamera = CreateMirrorObjects(realCamera);

            // find out the reflection plane: position and normal in world space
            Vector3 pos = default;
            Vector3 normal = Vector3.up;
            if (target != null)
            {
                pos = target.transform.position + Vector3.up * m_planeOffset;
                normal = target.transform.up;
            }

            UpdateCamera(realCamera, _reflectionCamera);

            var realCameraTransform = realCamera.transformHandle;
            var _reflectionCameraTransform = _reflectionCamera.transformHandle;

#if ZERO
            // Render reflection
            // Reflect camera around reflection plane
            var d = -Vector3.Dot(normal, pos) - m_settings.m_ClipPlaneOffset;
            var reflectionPlane = new Vector4(normal.x, normal.y, normal.z, d);

            CalculateReflectionMatrix(out var reflection, reflectionPlane);
            var oldPosition = realCameraTransform.position - new Vector3(0, pos.y * 2, 0);
            var newPosition = ReflectPosition(oldPosition);
            var forward = Quaternion.LookRotation(Vector3.Scale(realCameraTransform.forward, new Vector3(1, -1, 1)));
            var worldToCameraMatrix = realCamera.worldToCameraMatrix * reflection;
            _reflectionCamera.worldToCameraMatrix = worldToCameraMatrix;

            // Setup oblique projection matrix so that near plane is our reflection
            // plane. This way we clip everything below/above it for free.
            CameraSpacePlane(out var clipPlane, worldToCameraMatrix, pos - Vector3.up * 0.1f, normal, 1.0f, m_settings.m_ClipPlaneOffset);
#else
            var output = new Unity.Collections.NativeArray<Matrix4x4>(2,
                Unity.Collections.Allocator.TempJob, Unity.Collections.NativeArrayOptions.UninitializedMemory);

            var updateReflectionCameraJob = new UpdateReflectionCameraJob
            {
                output = output,
                normal = normal,
                pos = pos,
                m_ClipPlaneOffset = m_settings.m_ClipPlaneOffset,
                realCameraPosition = realCameraTransform.position,
                realCameraForward = realCameraTransform.forward,
                realCameraWorldToCameraMatrix = realCamera.worldToCameraMatrix,
            };

            Unity.Jobs.IJobExtensions.RunByRef(ref updateReflectionCameraJob);

            _reflectionCamera.worldToCameraMatrix = output[0];
            var clipPlane = output[1].GetColumn(0);
            Vector3 newPosition = output[1].GetColumn(1);
            var forward = output.Reinterpret<Quaternion>(4 * 4 * sizeof(float))[6];

            output.Dispose();
#endif // ZERO

            var projection = realCamera.CalculateObliqueMatrix(clipPlane);
            _reflectionCamera.projectionMatrix = projection;
            _reflectionCamera.cullingMask = m_settings.m_ReflectLayers; // never render water layer
            _reflectionCameraTransform.SetPositionAndRotation(newPosition, forward);
        }

        [Unity.Burst.BurstCompile]
        struct UpdateReflectionCameraJob : Unity.Jobs.IJob
        {
            [Unity.Collections.NativeFixedLength(2)]
            [Unity.Collections.WriteOnly] public Unity.Collections.NativeArray<Matrix4x4> output;
            [Unity.Collections.ReadOnly] public Matrix4x4 realCameraWorldToCameraMatrix;
            [Unity.Collections.ReadOnly] public Vector3 normal;
            [Unity.Collections.ReadOnly] public float m_ClipPlaneOffset;
            [Unity.Collections.ReadOnly] public Vector3 pos;
            [Unity.Collections.ReadOnly] public Vector3 realCameraPosition;
            [Unity.Collections.ReadOnly] public Vector3 realCameraForward;

            public void Execute()
            {
                // Render reflection
                // Reflect camera around reflection plane
                var d = -Vector3.Dot(normal, pos) - m_ClipPlaneOffset;
                var reflectionPlane = new Vector4(normal.x, normal.y, normal.z, d);

                CalculateReflectionMatrix(out var reflection, reflectionPlane);
                var oldPosition = realCameraPosition - new Vector3(0, pos.y * 2, 0);
                var newPosition = ReflectPosition(oldPosition);
                var _reflectionCameraForward = Quaternion.LookRotation(ReflectPosition(realCameraForward));
                var worldToCameraMatrix = realCameraWorldToCameraMatrix * reflection;

                // Setup oblique projection matrix so that near plane is our reflection
                // plane. This way we clip everything below/above it for free.
                CameraSpacePlane(out var clipPlane, worldToCameraMatrix, pos - Vector3.up * 0.1f, normal, 1.0f, m_ClipPlaneOffset);

                output[0] = worldToCameraMatrix;
                output[1] = new Matrix4x4(clipPlane, newPosition,
                    new Vector4(_reflectionCameraForward.x, _reflectionCameraForward.y,
                        _reflectionCameraForward.z, _reflectionCameraForward.w), default);
            }

            // Calculates reflection matrix around the given plane
            private static void CalculateReflectionMatrix(out Matrix4x4 reflectionMat, Vector4 plane)
            {
                reflectionMat.m00 = (1F - 2F * plane[0] * plane[0]);
                reflectionMat.m01 = (-2F * plane[0] * plane[1]);
                reflectionMat.m02 = (-2F * plane[0] * plane[2]);
                reflectionMat.m03 = (-2F * plane[3] * plane[0]);

                reflectionMat.m10 = (-2F * plane[1] * plane[0]);
                reflectionMat.m11 = (1F - 2F * plane[1] * plane[1]);
                reflectionMat.m12 = (-2F * plane[1] * plane[2]);
                reflectionMat.m13 = (-2F * plane[3] * plane[1]);

                reflectionMat.m20 = (-2F * plane[2] * plane[0]);
                reflectionMat.m21 = (-2F * plane[2] * plane[1]);
                reflectionMat.m22 = (1F - 2F * plane[2] * plane[2]);
                reflectionMat.m23 = (-2F * plane[3] * plane[2]);

                reflectionMat.m30 = 0F;
                reflectionMat.m31 = 0F;
                reflectionMat.m32 = 0F;
                reflectionMat.m33 = 1F;
            }

            private static Vector3 ReflectPosition(Vector3 pos)
            {
                var newPos = new Vector3(pos.x, -pos.y, pos.z);
                return newPos;
            }
        }

        private float GetScaleValue()
        {
            switch(m_settings.m_ResolutionMultiplier)
            {
                case ResolutionMultiplier.Full:
                    return 1f;
                case ResolutionMultiplier.Half:
                    return 0.5f;
                case ResolutionMultiplier.Third:
                    return 0.33f;
                case ResolutionMultiplier.Quarter:
                    return 0.25f;
                default:
                    return 0.5f; // default to half res
            }
        }

        // Compare two int2
        private static bool Int2Compare(int2 a, int2 b)
        {
            return a.x == b.x && a.y == b.y;
        }

        // Given position/normal of the plane, calculates plane in camera space.
        private static void CameraSpacePlane(out Vector4 clipPlane, Matrix4x4 m, Vector3 pos, Vector3 normal, float sideSign, float clipPlaneOffset)
        {
            var offsetPos = pos + normal * clipPlaneOffset;
            var cameraPosition = m.MultiplyPoint(offsetPos);
            var cameraNormal = m.MultiplyVector(normal).normalized * sideSign;
            clipPlane = new Vector4(cameraNormal.x, cameraNormal.y, cameraNormal.z, -Vector3.Dot(cameraPosition, cameraNormal));
        }

        [JetBrains.Annotations.NotNull]
        private Camera CreateMirrorObjects([System.Diagnostics.CodeAnalysis.NotNull] Camera realCamera)
        {
            var go = new GameObject("Planar Reflections");
            var reflectionCamera = go.AddComponent<Camera>();
            var cameraData = go.AddComponent<UniversalAdditionalCameraData>();

            cameraData.requiresColorOption = CameraOverrideOption.Off;
            cameraData.requiresDepthOption = CameraOverrideOption.Off;
#if ZERO // Use the default renderer
            cameraData.SetRenderer(1);
#endif // ZERO

            transform.GetPositionAndRotation(out var pos, out var rot);
            reflectionCamera.transform.SetPositionAndRotation(pos, rot);
            reflectionCamera.depth = -10;
            reflectionCamera.enabled = false;
            go.hideFlags = HideFlags.HideAndDontSave;

            if (realCamera.TryGetComponent<Skybox>(out var skybox))
            {
                var reflectionSkybox = go.AddComponent<Skybox>();
                reflectionSkybox.material = skybox.material;
            }

            return reflectionCamera;
        }

        private void PlanarReflectionTexture(Camera cam)
        {
            if (_reflectionTexture == null || !_reflectionTexture.IsCreated())
            {
                var useDynamicScale = _reflectionCamera.scaledPixelHeight != _reflectionCamera.pixelHeight;
                var res = ReflectionResolution(cam.pixelWidth, cam.pixelHeight, useDynamicScale ? 1.0f : GetScaleValue());
                bool useHdr10 = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float);
                RenderTextureFormat hdrFormat = useHdr10 ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.DefaultHDR;
                _reflectionTexture = RenderTexture.GetTemporary(res.x, res.y, 16,
                    GraphicsFormatUtility.GetGraphicsFormat(hdrFormat, true),
                    antiAliasing: 1, RenderTextureMemoryless.MSAA,
                    VRTextureUsage.None, useDynamicScale);
            }
            _reflectionCamera.targetTexture =  _reflectionTexture;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int2 ReflectionResolution(int pixelWidth, int pixelHeight, float scale)
        {
            var x = (int)(pixelWidth * scale);
            var y = (int)(pixelHeight * scale);
            return new int2(x, y);
        }

#if WATER_RENDER_REQUEST
        static readonly RenderPipeline.StandardRequest request = new RenderPipeline.StandardRequest();

        public void ExecutePlanarReflections(ScriptableRenderContext context, Camera camera)
#else
        private void ExecutePlanarReflections(ScriptableRenderContext context, Camera camera)
#endif // WATER_RENDER_REQUEST
        {
#if WATER_RENDER_REQUEST
            if (camera == _reflectionCamera)
                return;
#endif // WATER_RENDER_REQUEST

            // we dont want to render planar reflections in reflections or previews
            if (camera.cameraType == CameraType.Reflection || camera.cameraType == CameraType.Preview)
                return;

            UpdateReflectionCamera(camera); // create reflected camera
            PlanarReflectionTexture(camera); // create and assign RenderTexture

            var data = new PlanarReflectionSettingData(); // save quality settings and lower them for the planar reflections
            data.Set(); // set quality settings

            Shader.EnableKeyword("_PLANAR_REFLECTION_CAMERA");

            BeginPlanarReflections?.Invoke(context, _reflectionCamera); // callback Action for PlanarReflection

#if WATER_RENDER_REQUEST
            // Check if the request is supported by the active render pipeline
            if (RenderPipeline.SupportsRenderRequest(_reflectionCamera, request))
            {
                // Submit the render request to the active render pipeline with different destination textures
                request.destination = _reflectionTexture; //_reflectionCamera.targetTexture;

                // Render camera and fill texture2D with its view
                RenderPipeline.SubmitRenderRequest(_reflectionCamera, request);
            }
#else
            UniversalRenderPipeline.RenderSingleCamera(context, _reflectionCamera); // render planar reflections
#endif // WATER_RENDER_REQUEST

            data.Restore(); // restore the quality settings
            Shader.SetGlobalTexture(_planarReflectionTextureId, _reflectionTexture); // Assign texture to water shader
            Shader.DisableKeyword("_PLANAR_REFLECTION_CAMERA");
        }

        readonly struct PlanarReflectionSettingData
        {
            private readonly bool _useFog;
            private readonly bool _useLod;

            private readonly bool _fog;
            private readonly int _maxLod;
            private readonly float _lodBias;

            public PlanarReflectionSettingData(bool useFog = false, float lodBias = 0)
            {
                _useFog = useFog;
                _useLod = lodBias != 0;

                if (_useFog)
                    _fog = RenderSettings.fog;
                else
                    _fog = false;

                if (!_useLod)
                {
                    _maxLod = 0;
                    _lodBias = 0.1f;
                    return;
                }

                _maxLod = QualitySettings.maximumLODLevel;

                if (lodBias > 0.1f)
                    _lodBias = lodBias;
                else
                    _lodBias = QualitySettings.lodBias;
            }

            public void Set()
            {
                GL.invertCulling = true;
                if (_useFog)
                    RenderSettings.fog = false; // disable fog for now as it's incorrect with projection

                if (!_useLod)
                    return;

                QualitySettings.maximumLODLevel = 1;
                QualitySettings.lodBias = Mathf.Max(0.1f, _lodBias * 0.5f);
            }

            public void Restore()
            {
                GL.invertCulling = false;
                if (_useFog)
                    RenderSettings.fog = _fog;

                if (!_useLod)
                    return;

                QualitySettings.maximumLODLevel = _maxLod;
                QualitySettings.lodBias = Mathf.Max(0.1f, _lodBias);
            }
        }
    }
}
