#if UNITY_EDITOR
using System.IO;
using System.Reflection;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace WaterSystem
{
    #region ScriptableRenderPass

    class DepthSave : ScriptableRenderPass
    {
        private Shader _shader;

        private RenderTexture depthTarget;
        private readonly Material _mat;
        private readonly ProfilingSampler _profiler = new ProfilingSampler("DepthSave");

        public DepthSave(Shader shader, RenderTexture depthTarget)
        {
            this.depthTarget = depthTarget;
            _mat = CoreUtils.CreateEngineMaterial(shader);
            renderPassEvent = RenderPassEvent.AfterRendering;
        }


        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_mat == null)
            {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, _profiler)) // makes sure we have profiling ability
            {
                cmd.Blit(renderingData.cameraData.targetTexture, depthTarget, _mat, 0);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    #endregion

    public static class DepthBaking
    {
        static float YOffset;
        static float Range;

        public static void CaptureDepth(int tileResolution, int size, Transform objTransform, LayerMask mask, float range, float offset)
        {
            PackageInfo package = PackageInfo.FindForAssembly(Assembly.GetAssembly(typeof(DepthBaking)));
            Shader depthCopyShader = AssetDatabase.LoadAssetAtPath<Shader>(
                $"{package.assetPath}/Runtime/Shaders/Utility/SceneDepth.shadergraph");

            if (depthCopyShader == null)
            {
#if UNITY_EDITOR || DEBUG
                Debug.LogError("Failed to load SceneDepth shader for baking.");
#endif
                return;
            }

            Range = range;
            YOffset = offset;
            CreateDepthCamera(out Camera depthCam, objTransform.position, size, mask);

            RenderTexture buffer = RenderTexture.GetTemporary(tileResolution, tileResolution, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            RenderTexture bufferDepth = RenderTexture.GetTemporary(tileResolution, tileResolution, 0, RenderTextureFormat.R8);

            DepthSave pass = new DepthSave(depthCopyShader, bufferDepth);


            RenderPipelineManager.beginCameraRendering += (context, camera) =>
            {
                if (camera == depthCam)
                {
                    depthCam.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(pass);
                }
            };

            depthCam.targetTexture = buffer;
            depthCam.Render();

            AsyncGPUReadback.Request(bufferDepth, 0, TextureFormat.R8, GetData);

            if (buffer)
            {
                RenderTexture.ReleaseTemporary(buffer);
            }

            if (bufferDepth)
            {
                RenderTexture.ReleaseTemporary(bufferDepth);
            }

            if (depthCam)
            {
                Object.DestroyImmediate(depthCam.gameObject);
            }
        }

        private static void GetData(AsyncGPUReadbackRequest obj)
        {
            if (obj.hasError)
            {
#if UNITY_EDITOR || DEBUG
                Debug.LogError("Depth save failed.");
#endif
                return;
            }

            NativeArray<float> data = obj.GetData<float>();

            Texture2D tex = new Texture2D(obj.width, obj.height, TextureFormat.R8, true);
            tex.SetPixelData(data, 0);
            tex.Apply();
            SaveTile(tex.EncodeToPNG());

            Object.DestroyImmediate(tex);
        }

        static void SaveTile(byte[] data)
        {
            Scene activeScene = DepthGenerator.Current.gameObject.scene;
            //var sceneName = activeScene.name.Split('.')[0];
            string path = activeScene.path.Split('.')[0];
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            string filename = $"{DepthGenerator.Current.gameObject.name}_DepthTile.png";
            File.WriteAllBytes($"{path}/{filename}", data);

            AssetDatabase.Refresh();

            TextureImporter import = (TextureImporter)AssetImporter.GetAtPath($"{path}/{filename}");
            TextureImporterSettings settings = new TextureImporterSettings();
            import.ReadTextureSettings(settings);
            settings.textureType = TextureImporterType.SingleChannel;
            settings.readable = true;
            settings.singleChannelComponent = TextureImporterSingleChannelComponent.Red;
            import.SetTextureSettings(settings);
            import.SaveAndReimport();

            DepthGenerator.Current.depthTile = AssetDatabase.LoadAssetAtPath<Texture2D>($"{path}/{filename}");
        }

        static void CreateDepthCamera(out Camera camera, Vector3 position, float size, LayerMask mask)
        {
            //Generate the camera
            GameObject go = new GameObject("depthCamera") { hideFlags = HideFlags.HideAndDontSave }; //create the cameraObject
            Camera cam = go.AddComponent<Camera>();

            // setup camera props
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = size * 0.5f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = Range + YOffset; // settingsData._waterMaxVisibility + depthExtra;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.cameraType = CameraType.Game;
            cam.cullingMask = mask;

            // transform
            Transform t = cam.transform;
            t.position = position + Vector3.up * YOffset;
            t.up = Vector3.forward; //face the camera down

            // setup additional data
            UniversalAdditionalCameraData additionalCamData = cam.GetUniversalAdditionalCameraData();
            additionalCamData.renderShadows = false;
            additionalCamData.requiresColorOption = CameraOverrideOption.Off;
            additionalCamData.requiresDepthOption = CameraOverrideOption.On;
            camera = cam;
        }
    }
}
#endif