using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#pragma warning disable CS0618 // Type or member is obsolete

namespace WaterSystem.Rendering
{
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

        private class PlanarReflectionObjects
        {
            public Camera Camera;
            public RenderTexture Texture;
        }

        public static PlanarReflectionSettings m_settings = new PlanarReflectionSettings();

        public static float m_planeOffset;

        private static Dictionary<Camera, PlanarReflectionObjects> _reflectionObjects = new Dictionary<Camera, PlanarReflectionObjects>();
        private static readonly int _planarReflectionTextureId = Shader.PropertyToID("_PlanarReflectionTexture");
        private const string _PlanarReflections = "Planar Reflections";

        public static event Action<ScriptableRenderContext, Camera> BeginPlanarReflections;

        private static VolumetricCloudsURP volumetricCloudsURP;

        public static void Cleanup()
        {
            foreach (var objects in _reflectionObjects)
            {
                if (objects.Value.Camera != null)
                {
                    CoreUtils.Destroy(objects.Value.Camera.gameObject);
                    //objects.Value.Camera = null;
                }

                if (objects.Value.Texture != null)
                {
                    RenderTexture.ReleaseTemporary(objects.Value.Texture);
                    //objects.Value.Texture.Release();
                    //objects.Value.Texture = null;
                }
                //RenderTexture.ReleaseTemporary(objects.Value);

                /*if (objects.Key != null)
                {
                    CoreUtils.Destroy(objects.Key.gameObject);
                }*/
            }
            _reflectionObjects.Clear();
            //_reflectionTextures.Clear();
        }

        private static void UpdateCamera(Camera src, Camera dest)
        {
            if (dest == null) { return; }

            dest.CopyFrom(src);
            dest.useOcclusionCulling = false;
            dest.clearFlags = CameraClearFlags.SolidColor;
            dest.backgroundColor = Color.clear;
            if (dest.gameObject.TryGetComponent<UniversalAdditionalCameraData>(out var camData))
            {
                camData.renderPostProcessing = camData.requiresDepthTexture = camData.requiresColorTexture = false; // set these to false (just in case)
                camData.renderShadows = m_settings.m_Shadows; // turn off shadows for the reflection camera based on settings
            }
        }

        private static void UpdateReflectionCamera(Camera realCamera)
        {
            if (_reflectionObjects[realCamera].Camera == null)
            {
                _reflectionObjects[realCamera].Camera = CreateMirrorObjects();

                // Get the VolumetricCloudsURP renderer feature to disable it before rendering
                /*UniversalRenderPipelineAsset urpAsset = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
                FieldInfo RenderDataList_FieldInfo = urpAsset.GetType().GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);
                ScriptableRendererData[] renderDataList = (ScriptableRendererData[])RenderDataList_FieldInfo.GetValue(urpAsset);*/
                ScriptableRendererData[] renderDataList = ((UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline).m_RendererDataList;
                List<ScriptableRendererFeature> features = renderDataList[0].rendererFeatures;
                foreach (var rendererFeature in features)
                {
                    if (rendererFeature is VolumetricCloudsURP volumetricCloudsFeature)
                    {
                        volumetricCloudsURP = volumetricCloudsFeature;
                        break;
                    }
                }
            }

            // find out the reflection plane: position and normal in world space
            Vector3 normal = Vector3.up;

            UpdateCamera(realCamera, _reflectionObjects[realCamera].Camera);

            // Render reflection
            // Reflect camera around reflection plane
            Vector4 reflectionPlane = new Vector4(normal.x, normal.y, normal.z, -m_planeOffset);

            Matrix4x4 reflection = Matrix4x4.identity * Matrix4x4.Scale(new Vector3(1, -1, 1));

            CalculateReflectionMatrix(ref reflection, reflectionPlane);
            Vector3 newPosition = ReflectPosition(realCamera.transform.position);
            //_reflectionObjects[realCamera].Camera.transform.forward = Vector3.Scale(realCamera.transform.forward, new Vector3(1, -1, 1));
            _reflectionObjects[realCamera].Camera.worldToCameraMatrix = realCamera.worldToCameraMatrix * reflection;

            // Setup oblique projection matrix so that near plane is our reflection
            // plane. This way we clip everything below/above it for free.
            if (m_settings.m_ObliqueProjection)
            {
                _reflectionObjects[realCamera].Camera.projectionMatrix = realCamera.CalculateObliqueMatrix(CameraSpacePlane(_reflectionObjects[realCamera].Camera, Vector3.down * 0.1f, normal, 1.0f));
            }
            _reflectionObjects[realCamera].Camera.cullingMask = m_settings.m_ReflectLayers; // never render water layer
            _reflectionObjects[realCamera].Camera.transform.position = newPosition;
        }

        // Calculates reflection matrix around the given plane
        private static void CalculateReflectionMatrix(ref Matrix4x4 reflectionMat, Vector4 plane)
        {
            reflectionMat.m00 = 1F - 2F * plane[0] * plane[0];
            reflectionMat.m01 = -2F * plane[0] * plane[1];
            reflectionMat.m02 = -2F * plane[0] * plane[2];
            reflectionMat.m03 = -2F * plane[3] * plane[0];

            reflectionMat.m10 = -2F * plane[1] * plane[0];
            reflectionMat.m11 = 1F - 2F * plane[1] * plane[1];
            reflectionMat.m12 = -2F * plane[1] * plane[2];
            reflectionMat.m13 = -2F * plane[3] * plane[1];

            reflectionMat.m20 = -2F * plane[2] * plane[0];
            reflectionMat.m21 = -2F * plane[2] * plane[1];
            reflectionMat.m22 = 1F - 2F * plane[2] * plane[2];
            reflectionMat.m23 = -2F * plane[3] * plane[2];

            reflectionMat.m30 = 0F;
            reflectionMat.m31 = 0F;
            reflectionMat.m32 = 0F;
            reflectionMat.m33 = 1F;
        }

        private static Vector3 ReflectPosition(Vector3 pos)
        {
            return new Vector3(pos.x, -pos.y, pos.z);
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

        // Given position/normal of the plane, calculates plane in camera space.
        private static Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
        {
            Vector3 offsetPos = pos + normal * (m_settings.m_ClipPlaneOffset + m_planeOffset);
            Matrix4x4 m = cam.worldToCameraMatrix;
            Vector3 cameraPosition = m.MultiplyPoint(offsetPos);
            Vector3 cameraNormal = m.MultiplyVector(normal).normalized * sideSign;
            return new Vector4(cameraNormal.x, cameraNormal.y, cameraNormal.z, -Vector3.Dot(cameraPosition, cameraNormal));
        }

        private static Camera CreateMirrorObjects()
        {
            GameObject go = new GameObject(_PlanarReflections);
            Camera reflectionCamera = go.AddComponent<Camera>();
            UniversalAdditionalCameraData cameraData = go.AddComponent<UniversalAdditionalCameraData>();

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
        }

        private static void UpdateReflectionObjects(Camera camera)
        {
            _reflectionObjects.TryAdd(camera, new PlanarReflectionObjects());
            UpdateReflectionCamera(camera);
            PlanarReflectionTexture(_reflectionObjects[camera], ReflectionResolution(camera, 1f /*UniversalRenderPipeline.asset.renderScale*/));
        }

        private static RenderTexture CreateTexture(int2 res)
        {
            //bool useHdr10 = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float);
            //RenderTextureFormat hdrFormat = useHdr10 ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.DefaultHDR;
#if UNITY_ANDROID || UNITY_IOS
            RenderTextureFormat hdrFormat = RenderTextureFormat.ARGBHalf;
#else
            RenderTextureFormat hdrFormat = RenderTextureFormat.ARGBFloat;
#endif
            return RenderTexture.GetTemporary(res.x, res.y, 0, GraphicsFormatUtility.GetGraphicsFormat(hdrFormat, true));
        }

        private static int2 ReflectionResolution(Camera cam, float scale)
        {
            if (m_settings.m_ResolutionMode == ResolutionModes.Custom)
            {
                return m_settings.m_ResolutionCustom;
            }

            scale *= GetScaleValue();

            return new int2((int)(cam.pixelWidth * scale), (int)(cam.pixelHeight * scale));
        }

        public static void Execute(ScriptableRenderContext context, Camera camera)
        {
            // Don't render planar reflections in reflections or previews
            if (camera.cameraType != CameraType.Game || m_settings == null) { return; }

            UpdateReflectionObjects(camera);

            GL.invertCulling = true;
            volumetricCloudsURP.SetActive(false);

            // callback Action for PlanarReflection
            if (BeginPlanarReflections != null) { BeginPlanarReflections(context, _reflectionObjects[camera].Camera); }

            //Debug.LogError(UniversalRenderPipeline.SupportsRenderRequest(_reflectionObjects[camera].Camera, typeof(UniversalRenderPipeline.SingleCameraRequest)));
            //UniversalRenderPipeline.SubmitRenderRequest(_reflectionObjects[camera].Camera, typeof(UniversalRenderPipeline.SingleCameraRequest));
            UniversalRenderPipeline.RenderSingleCamera(context, _reflectionObjects[camera].Camera); // render planar reflections

            GL.invertCulling = false;
            volumetricCloudsURP.SetActive(true);

            Shader.SetGlobalTexture(_planarReflectionTextureId, _reflectionObjects[camera].Texture); // Assign texture to water shader
        }
    }
}
