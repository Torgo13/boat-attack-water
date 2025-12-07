using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Serialization;
using Unity.Mathematics;

namespace UnityEngine.Rendering.Universal
{
    [ExecuteAlways]
    public class PlanarReflections : MonoBehaviour
    {
        [Serializable]
        public enum ResolutionMulltiplier
        {
            Full,
            Half,
            Third,
            Quarter
        }

        [Serializable]
        public class PlanarReflectionSettings
        {
            public ResolutionMulltiplier m_ResolutionMultiplier = ResolutionMulltiplier.Third;
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
#endif // WATER_RENDER_REQUEST

        // Cleanup all the objects we possibly have created
        private void OnDisable()
        {
            Cleanup();
        }

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
            if (Application.isEditor)
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

        private void UpdateReflectionCamera(Camera realCamera)
        {
            if (_reflectionCamera == null)
                _reflectionCamera = CreateMirrorObjects();

            // find out the reflection plane: position and normal in world space
            Vector3 pos = default;
            Vector3 normal = Vector3.up;
            if (target != null)
            {
                pos = target.transform.position + Vector3.up * m_planeOffset;
                normal = target.transform.up;
            }

            UpdateCamera(realCamera, _reflectionCamera);

            // Render reflection
            // Reflect camera around reflection plane
            var d = -Vector3.Dot(normal, pos) - m_settings.m_ClipPlaneOffset;
            var reflectionPlane = new Vector4(normal.x, normal.y, normal.z, d);

            var realCameraTransform = realCamera.transform;
            var _reflectionCameraTransform = _reflectionCamera.transform;

            CalculateReflectionMatrix(out var reflection, reflectionPlane);
            var oldPosition = realCameraTransform.position - new Vector3(0, pos.y * 2, 0);
            var newPosition = ReflectPosition(oldPosition);
            _reflectionCameraTransform.forward = Vector3.Scale(realCameraTransform.forward, new Vector3(1, -1, 1));
            var worldToCameraMatrix = realCamera.worldToCameraMatrix * reflection;
            _reflectionCamera.worldToCameraMatrix = worldToCameraMatrix;

            // Setup oblique projection matrix so that near plane is our reflection
            // plane. This way we clip everything below/above it for free.
            var clipPlane = CameraSpacePlane(worldToCameraMatrix, pos - Vector3.up * 0.1f, normal, 1.0f);
            var projection = realCamera.CalculateObliqueMatrix(clipPlane);
            _reflectionCamera.projectionMatrix = projection;
            _reflectionCamera.cullingMask = m_settings.m_ReflectLayers; // never render water layer
            _reflectionCameraTransform.position = newPosition;
        }

        // Calculates reflection matrix around the given plane
        private static void CalculateReflectionMatrix(out Matrix4x4 reflectionMat, in Vector4 plane)
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

        private float GetScaleValue()
        {
            switch(m_settings.m_ResolutionMultiplier)
            {
                case ResolutionMulltiplier.Full:
                    return 1f;
                case ResolutionMulltiplier.Half:
                    return 0.5f;
                case ResolutionMulltiplier.Third:
                    return 0.33f;
                case ResolutionMulltiplier.Quarter:
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
        private Vector4 CameraSpacePlane(in Matrix4x4 m, in Vector3 pos, in Vector3 normal, float sideSign)
        {
            var offsetPos = pos + normal * m_settings.m_ClipPlaneOffset;
            var cameraPosition = m.MultiplyPoint(offsetPos);
            var cameraNormal = m.MultiplyVector(normal).normalized * sideSign;
            return new Vector4(cameraNormal.x, cameraNormal.y, cameraNormal.z, -Vector3.Dot(cameraPosition, cameraNormal));
        }

        private Camera CreateMirrorObjects()
        {
            var go = new GameObject("Planar Reflections");
            var reflectionCamera = go.AddComponent<Camera>();
            var cameraData = go.AddComponent(typeof(UniversalAdditionalCameraData)) as UniversalAdditionalCameraData;

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

            var mainCamera = Camera.main;
            if (mainCamera.TryGetComponent<Skybox>(out var skybox))
            {
                var reflectionSkybox = go.AddComponent<Skybox>();
                reflectionSkybox.material = skybox.material;
            }

            return reflectionCamera;
        }

        private void PlanarReflectionTexture(Camera cam)
        {
            if (_reflectionTexture == null)
            {
                var useDynamicScale = _reflectionCamera.scaledPixelHeight != _reflectionCamera.pixelHeight;
                var res = ReflectionResolution(cam.pixelWidth, cam.pixelHeight, useDynamicScale ? 1.0f : GetScaleValue());
                bool useHdr10 = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float);
                RenderTextureFormat hdrFormat = useHdr10 ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.DefaultHDR;
                _reflectionTexture = RenderTexture.GetTemporary(res.x, res.y, 16,
                    GraphicsFormatUtility.GetGraphicsFormat(hdrFormat, true),
                    antiAliasing: 1, RenderTextureMemoryless.None | RenderTextureMemoryless.MSAA,
                    VRTextureUsage.None, useDynamicScale);
            }
            _reflectionCamera.targetTexture =  _reflectionTexture;
        }

        private static int2 ReflectionResolution(int pixelWidth, int pixelHeight, float scale)
        {
            var x = (int)(pixelWidth * scale);
            var y = (int)(pixelHeight * scale);
            return new int2(x, y);
        }

#if WATER_RENDER_REQUEST
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
            // Create a standard request
            var request = new RenderPipeline.StandardRequest();

            // Check if the request is supported by the active render pipeline
            if (RenderPipeline.SupportsRenderRequest(_reflectionCamera, request))
            {
                // Submit the render request to the active render pipeline with different destination textures
                request.destination = _reflectionCamera.targetTexture;

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
            private readonly bool _fog;
            private readonly int _maxLod;
            private readonly float _lodBias;

            public PlanarReflectionSettingData(float lodBias = default)
            {
                _fog = RenderSettings.fog;
                _maxLod = QualitySettings.maximumLODLevel;
                _lodBias = lodBias > 0.1f ? lodBias : QualitySettings.lodBias;
            }

            public void Set()
            {
                GL.invertCulling = true;
                RenderSettings.fog = false; // disable fog for now as it's incorrect with projection
                QualitySettings.maximumLODLevel = 1;
                QualitySettings.lodBias = Mathf.Max(0.1f, _lodBias * 0.5f);
            }

            public void Restore()
            {
                GL.invertCulling = false;
                RenderSettings.fog = _fog;
                QualitySettings.maximumLODLevel = _maxLod;
                QualitySettings.lodBias = Mathf.Max(0.1f, _lodBias);
            }
        }
    }
}
