using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

#pragma warning disable CS0618 // Type or member is obsolete

namespace WaterSystem.Rendering
{
    [Unity.Burst.BurstCompile]
    public class PlanarReflections
    {
        [Serializable]
        public enum ResolutionModes
        {
            Full,
            Half,
            Third,
            Quarter,
            Multiplier,
            Custom,
        }

        [Serializable]
        public enum RendererMode
        {
            Match,
            Static,
            Offset
        }

        [Serializable]
        public class PlanarReflectionSettings
        {
            public ResolutionModes m_ResolutionMode = ResolutionModes.Third;
            public float m_ResolutionMultiplier = 1.0f;
            public int2 m_ResolutionCustom = new int2(320, 180);
            public float m_ClipPlaneOffset = 0.07f;
            public LayerMask m_ReflectLayers = -1;
            public bool m_Shadows;
            public bool m_ObliqueProjection = true;
            public RendererMode m_RendererMode;
            public int m_RendererIndex;
        }

        private sealed class PlanarReflectionObjects
        {
            public Camera Camera;
            public RenderTexture Texture;
        }

        public static PlanarReflectionSettings m_settings = new PlanarReflectionSettings();

        public static float m_planeOffset;

        readonly
        private static Dictionary<Camera, PlanarReflectionObjects> _reflectionObjects = new Dictionary<Camera, PlanarReflectionObjects>();
        private static readonly int _planarReflectionTextureId = Shader.PropertyToID("_PlanarReflectionTexture");

        public static event Action<ScriptableRenderContext, Camera> BeginPlanarReflections;

        public static void Cleanup()
        {
            foreach (var objects in _reflectionObjects)
            {
                if (objects.Value.Camera != null)
                {
                    CoreUtils.Destroy(objects.Value.Camera.gameObject);
                }

                if (objects.Value.Texture != null)
                {
                    RenderTexture.ReleaseTemporary(objects.Value.Texture);
                }
            }

            _reflectionObjects.Clear();
        }

        private static void UpdateCamera(Camera src, Camera dest)
        {
            if (dest == null)
                return;

            dest.CopyFrom(src);
            dest.useOcclusionCulling = false;
            dest.clearFlags = CameraClearFlags.SolidColor;
            dest.backgroundColor = Color.clear;
            if (dest.gameObject.TryGetComponent(out UniversalAdditionalCameraData camData))
            {
                camData.renderPostProcessing = camData.requiresDepthTexture = camData.requiresColorTexture = false; // set these to false (just in case)
                camData.renderShadows = m_settings.m_Shadows; // turn off shadows for the reflection camera based on settings
                switch (m_settings.m_RendererMode)
                {
                    case RendererMode.Static:
                        camData.SetRenderer(m_settings.m_RendererIndex);
                        break;
                    case RendererMode.Offset:
                        //TODO need API to get current index
                        break;
                    case RendererMode.Match:
                    default:
                        break;
                }
            }
        }

        private static void UpdateReflectionCamera(Camera realCamera)
        {
            if (_reflectionObjects[realCamera].Camera == null)
                _reflectionObjects[realCamera].Camera = CreateMirrorObjects();

            // find out the reflection plane: position and normal in world space
            Vector3 normal = Vector3.up;

            UpdateCamera(realCamera, _reflectionObjects[realCamera].Camera);

            // Render reflection
            // Reflect camera around reflection plane
            var reflectionPlane = new Vector4(normal.x, normal.y, normal.z, -m_planeOffset);
            var reflection = new Matrix4x4(); //Matrix4x4.identity; * Matrix4x4.Scale(new Vector3(1, -1, 1));

            CalculateReflectionMatrix(ref reflection, reflectionPlane);
            var newPosition = ReflectPosition(realCamera.transform.position);
            /*
            _reflectionObjects[realCamera].Camera.transform.forward = Vector3.Scale(realCamera.transform.forward, new Vector3(1, -1, 1));
            */
            _reflectionObjects[realCamera].Camera.worldToCameraMatrix = realCamera.worldToCameraMatrix * reflection;

            // Setup oblique projection matrix so that near plane is our reflection
            // plane. This way we clip everything below/above it for free.
            if (m_settings.m_ObliqueProjection)
            {
                var clipPlane = CameraSpacePlane(_reflectionObjects[realCamera].Camera, Vector3.down * 0.1f, normal, 1.0f);
                var projection = realCamera.CalculateObliqueMatrix(clipPlane);
                _reflectionObjects[realCamera].Camera.projectionMatrix = projection;
            }

            _reflectionObjects[realCamera].Camera.cullingMask = m_settings.m_ReflectLayers; // never render water layer
            _reflectionObjects[realCamera].Camera.transform.position = newPosition;
        }

        // Calculates reflection matrix around the given plane
        [Unity.Burst.BurstCompile(FloatMode = Unity.Burst.FloatMode.Fast)]
        private static void CalculateReflectionMatrix(ref Matrix4x4 reflectionMat, in Vector4 plane)
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

        private static float GetScaleValue()
        {
            switch (m_settings.m_ResolutionMode)
            {
                case ResolutionModes.Full:
                    return 1f;
                case ResolutionModes.Half:
                    return 0.5f;
                case ResolutionModes.Third:
                    return 0.33f;
                case ResolutionModes.Quarter:
                    return 0.25f;
                case ResolutionModes.Multiplier:
                    return m_settings.m_ResolutionMultiplier;
                default:
                    return 0.5f; // default to half res
            }
        }

#if ZERO
        // Compare two int2
        private static bool Int2Compare(int2 a, int2 b)
        {
            return a.x == b.x && a.y == b.y;
        }
#endif // ZERO

        // Given position/normal of the plane, calculates plane in camera space.
        private static Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
        {
            var offsetPos = pos + normal * (m_settings.m_ClipPlaneOffset + m_planeOffset);
            var m = cam.worldToCameraMatrix;
            var cameraPosition = m.MultiplyPoint(offsetPos);
            var cameraNormal = m.MultiplyVector(normal).normalized * sideSign;
            return new Vector4(cameraNormal.x, cameraNormal.y, cameraNormal.z, -Vector3.Dot(cameraPosition, cameraNormal));
        }

        private static Camera CreateMirrorObjects()
        {
            var go = new GameObject("Planar Reflections");
            var reflectionCamera = go.AddComponent<Camera>();
            var cameraData = go.AddComponent<UniversalAdditionalCameraData>();

            cameraData.requiresColorOption = CameraOverrideOption.Off;
            cameraData.requiresDepthOption = CameraOverrideOption.Off;

            reflectionCamera.depth = -10;
            reflectionCamera.enabled = false;
            go.hideFlags = HideFlags.HideAndDontSave;

            return reflectionCamera;
        }

        private static void PlanarReflectionTexture(PlanarReflectionObjects objects, int2 res)
        {
            if (objects.Texture == null)
            {
                objects.Texture = CreateTexture(res);
            }
            else if (objects.Texture.width != res.x)
            {
                RenderTexture.ReleaseTemporary(objects.Texture);
                objects.Texture = CreateTexture(res);
            }

            objects.Camera.targetTexture = objects.Texture;
            objects.Camera.forceIntoRenderTexture = true;
        }

        private static void UpdateReflectionObjects(Camera camera)
        {
            _ = _reflectionObjects.TryAdd(camera, new PlanarReflectionObjects());
            UpdateReflectionCamera(camera);
            PlanarReflectionTexture(_reflectionObjects[camera], ReflectionResolution(camera, 1f));
        }

        private static RenderTexture CreateTexture(int2 res)
        {
            bool useHdr10 = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.ARGB2101010);
            RenderTextureFormat hdrFormat = useHdr10 ? RenderTextureFormat.ARGB2101010 : RenderTextureFormat.DefaultHDR;

            return RenderTexture.GetTemporary(res.x, res.y, depthBuffer: 0,
                GraphicsFormatUtility.GetGraphicsFormat(hdrFormat, isSRGB: true));
        }

        private static int2 ReflectionResolution(Camera cam, float scale)
        {
            if (m_settings.m_ResolutionMode == ResolutionModes.Custom)
                return m_settings.m_ResolutionCustom;

            scale *= GetScaleValue();
            var x = (int)(cam.pixelWidth * scale);
            var y = (int)(cam.pixelHeight * scale);

            return new int2(x, y);
        }

        public static void Execute(ScriptableRenderContext context, Camera camera)
        {
            // Don't render planar reflections in reflections or previews
            if (camera.cameraType != CameraType.Game)
                return;

#if ZERO
            if (m_settings == null)
                return;
#endif // ZERO

            UpdateReflectionObjects(camera);

            GL.invertCulling = true;

            BeginPlanarReflections?.Invoke(context, _reflectionObjects[camera].Camera); // callback Action for PlanarReflection

            //Debug.LogError(UniversalRenderPipeline.SupportsRenderRequest(_reflectionObjects[camera].Camera, typeof(UniversalRenderPipeline.SingleCameraRequest)));
            /*
            var request = new RenderPipeline.StandardRequest();
            if (RenderPipeline.SupportsRenderRequest(_reflectionObjects[camera].Camera, request))
            {
                request.destination = _reflectionObjects[camera].Texture;
                RenderPipeline.SubmitRenderRequest(_reflectionObjects[camera].Camera, request);
            }
            */

            UniversalRenderPipeline.RenderSingleCamera(context, _reflectionObjects[camera].Camera); // render planar reflections

            GL.invertCulling = false;

            Shader.SetGlobalTexture(_planarReflectionTextureId, _reflectionObjects[camera].Texture); // Assign texture to water shader
        }

#if RENDERPASS
#else
        public class PlanarReflectionsPass : ScriptableRenderPass
        {
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var camera = renderingData.cameraData.camera;

                // Don't render planar reflections in reflections or previews
                if (camera.cameraType != CameraType.Game)
                    return;

#if ZERO
                if (m_settings == null)
                    return;
#endif // ZERO

                UpdateReflectionObjects(camera);

                GL.invertCulling = true;

                BeginPlanarReflections?.Invoke(context, _reflectionObjects[camera].Camera); // callback Action for PlanarReflection

                //Debug.LogError(UniversalRenderPipeline.SupportsRenderRequest(_reflectionObjects[camera].Camera, typeof(UniversalRenderPipeline.SingleCameraRequest)));
                /*
                if (RenderPipeline.SupportsRenderRequest(_reflectionObjects[camera].Camera, request))
                {
                    request.destination = _reflectionObjects[camera].Texture;
                    RenderPipeline.SubmitRenderRequest(_reflectionObjects[camera].Camera, request);
                }
                */
                UniversalRenderPipeline.RenderSingleCamera(context, _reflectionObjects[camera].Camera); // render planar reflections

                GL.invertCulling = false;

                Shader.SetGlobalTexture(_planarReflectionTextureId, _reflectionObjects[camera].Texture); // Assign texture to water shader
            }
        }
#endif // RENDERPASS

#if ZERO
        class PlanarReflectionSettingData
        {
            private readonly bool _fog;
            private readonly int _maxLod;
            private readonly float _lodBias;

            public PlanarReflectionSettingData()
            {
                _fog = RenderSettings.fog;
                _maxLod = QualitySettings.maximumLODLevel;
                _lodBias = QualitySettings.lodBias;
            }

            public void Set(bool fog)
            {
                GL.invertCulling = true;
                RenderSettings.fog = fog; // disable fog for now as it's incorrect with projection
                QualitySettings.maximumLODLevel = 1;
                QualitySettings.lodBias = _lodBias * 0.5f;
            }

            public void Restore()
            {
                GL.invertCulling = false;
                RenderSettings.fog = _fog;
                QualitySettings.maximumLODLevel = _maxLod;
                QualitySettings.lodBias = _lodBias;
            }
        }
#endif // ZERO
    }
}
