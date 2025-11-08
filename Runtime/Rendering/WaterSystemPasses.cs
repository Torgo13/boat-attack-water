using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WaterSystem.Rendering
{
    #region Water Effects Pass

    public class WaterFxPass : ScriptableRenderPass
    {
        private readonly int WaterBufferA = Shader.PropertyToID("_WaterBufferA");
        private readonly int WaterBufferB = Shader.PropertyToID("_WaterBufferB");

#if UNITY_2022_1_OR_NEWER
        readonly RTHandle[] multiTargets = new RTHandle[2];
        private RTHandle m_BufferTargetA, m_BufferTargetB;
        private RTHandle m_BufferTargetDepth;
#else
        private int m_BufferTargetA, m_BufferTargetB;
#endif

        private const string k_RenderWaterFXTag = "WaterFX";
        private readonly ShaderTagId m_WaterFXShaderTag = new ShaderTagId("WaterFX");

        // r = foam mask
        // g = normal.x
        // b = normal.z
        // a = displacement
        private readonly Color m_ClearColor = new Color(0.0f, 0.5f, 0.5f, 0.5f);
        private readonly UnityEngine.Experimental.Rendering.GraphicsFormat colorFormat;

        private FilteringSettings m_FilteringSettings;
        private RenderTextureDescriptor td;

        public WaterFxPass()
        {
            profilingSampler = new ProfilingSampler(k_RenderWaterFXTag);
            // Only render transparent objects
            m_FilteringSettings = new FilteringSettings(RenderQueueRange.transparent);
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;

            var renderTextureFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                ? RenderTextureFormat.ARGBHalf
                : RenderTextureFormat.Default;

            var graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(
                    renderTextureFormat, isSRGB: false);
            
#if UNITY_6000_3_OR_NEWER
            const UnityEngine.Experimental.Rendering.GraphicsFormatUsage formatUsage
                = UnityEngine.Experimental.Rendering.GraphicsFormatUsage.Linear;
#else
            const UnityEngine.Experimental.Rendering.FormatUsage formatUsage
                = UnityEngine.Experimental.Rendering.FormatUsage.Render;
#endif // UNITY_6000_3_OR_NEWER

            colorFormat = SystemInfo.GetCompatibleFormat(graphicsFormat, formatUsage);
        }

#if !ZERO
        // Calling Configure since we are wanting to render into a RenderTexture and control clear
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
#else
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
#endif // ZERO
        {
#if !ZERO
#else
            var cameraData = renderingData.cameraData;
            var cameraTextureDescriptor = new RectInt(0, 0, cameraData.scaledWidth, cameraData.scaledHeight);
            //var camera = cameraData.camera;
            //var cameraTextureDescriptor = new RectInt(0, 0, camera.pixelWidth, camera.pixelHeight);
#endif // ZERO

            int width = cameraTextureDescriptor.width / 2;
            int height = cameraTextureDescriptor.height / 2;

            if (td.width != width || td.height != height)
                td = GetRTD(width, height);

#if UNITY_2022_1_OR_NEWER
#if UNITY_6000_3_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_BufferTargetA, td, FilterMode.Bilinear, name: "_WaterBufferA");
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_BufferTargetB, td, FilterMode.Bilinear, name: "_WaterBufferB");
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_BufferTargetDepth, td, FilterMode.Bilinear, name: "_WaterBufferDepth");
#else
            RenderingUtils.ReAllocateIfNeeded(ref m_BufferTargetA, td, FilterMode.Bilinear, name: "_WaterBufferA");
            RenderingUtils.ReAllocateIfNeeded(ref m_BufferTargetB, td, FilterMode.Bilinear, name: "_WaterBufferB");
            RenderingUtils.ReAllocateIfNeeded(ref m_BufferTargetDepth, td, FilterMode.Bilinear, name: "_WaterBufferDepth");
#endif // UNITY_6000_3_OR_NEWER
            multiTargets[0] = m_BufferTargetA;
            multiTargets[1] = m_BufferTargetB;
            cmd.SetGlobalTexture(WaterBufferA, m_BufferTargetA.nameID);
            cmd.SetGlobalTexture(WaterBufferB, m_BufferTargetB.nameID);
#else
            cmd.GetTemporaryRT(WaterBufferA, td, FilterMode.Bilinear);
            cmd.GetTemporaryRT(WaterBufferB, td, FilterMode.Bilinear);
            RenderTargetIdentifier[] multiTargets = { m_BufferTargetA, m_BufferTargetB };
#endif
            ConfigureTarget(multiTargets, m_BufferTargetDepth);
            // clear the screen with a specific color for the packed data
            ConfigureClear(ClearFlag.Color, m_ClearColor);

#if UNITY_2021_1_OR_NEWER
            ConfigureDepthStoreAction(RenderBufferStoreAction.DontCare);
#endif
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (renderingData.cameraData.camera.cameraType != CameraType.Game)
                return;

            var drawSettings = CreateDrawingSettings(m_WaterFXShaderTag, ref renderingData, SortingCriteria.CommonTransparent);

            CommandBuffer cmd = CommandBufferPool.Get();
            cmd.Clear();
            context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref m_FilteringSettings);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private RenderTextureDescriptor GetRTD(int width, int height)
        {
            return new RenderTextureDescriptor(width, height, colorFormat, depthBufferBits: 0, mipCount: 0)
            {
                sRGB = false,
                memoryless = RenderTextureMemoryless.Depth,
            };
        }

        public void Cleanup()
        {
            m_BufferTargetA?.Release();
            m_BufferTargetB?.Release();
            m_BufferTargetDepth?.Release();
        }
    }

#endregion

    #region InfiniteWater Pass

    public class InfiniteWaterPass : ScriptableRenderPass
    {
        private readonly Mesh infiniteMesh;
        private readonly Material infiniteMaterial;
        private readonly SphericalHarmonicsL2[] lightProbes = new SphericalHarmonicsL2[1];
        readonly MaterialPropertyBlock matBloc = new MaterialPropertyBlock();
        private readonly int BumpScale = Shader.PropertyToID("_BumpScale");

        public InfiniteWaterPass(Mesh mesh, Shader shader)
        {
            infiniteMesh = mesh;
            infiniteMaterial = new Material(shader);
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        }

        public void Cleanup()
        {
            CoreUtils.Destroy(infiniteMaterial);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;

            if (cam.cameraType != CameraType.Game &&
                cam.cameraType != CameraType.SceneView ||
                cam.name.Contains("Reflections",
                    System.StringComparison.OrdinalIgnoreCase))
                return;

            if (infiniteMesh == null)
            {
#if DEBUG
                Debug.LogError("Infinite Water Pass Mesh is missing.");
#endif // DEBUG
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, new ProfilingSampler("Infinite Water")))
            {
                var probe = RenderSettings.ambientProbe;

                infiniteMaterial.SetFloat(BumpScale, 0.5f);

                // Create the matrix to position the caustics mesh.
                var position = cam.transform.position;
                var matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
                // Set up the CommandBuffer and draw the mesh with the infinite water material and matrix
                lightProbes[0] = probe;
                matBloc.CopySHCoefficientArraysFrom(lightProbes);
                cmd.DrawMesh(infiniteMesh, matrix, infiniteMaterial, 0, 0, matBloc);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    #endregion

    #region Caustics Pass

    public class WaterCausticsPass : ScriptableRenderPass
    {
        private const string k_RenderWaterCausticsTag = "Render Water Caustics";
        private readonly ProfilingSampler m_WaterCaustics_Profile = new ProfilingSampler(k_RenderWaterCausticsTag);
        private readonly Material WaterCausticMaterial;
        private Mesh m_mesh;
        private Transform sunTransform;
        private readonly float waterLevel;
        private readonly int MainLightDir = Shader.PropertyToID("_MainLightDir");
        private readonly int WaterLevel = Shader.PropertyToID("_WaterLevel");

        public WaterCausticsPass(Material material)
        {
            WaterCausticMaterial = material;
            var ocean = Ocean.Instance;
            waterLevel = ocean != null
                ? ocean.transform.position.y
                : 2;
        }

#if RENDERPASS
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }
#endif // RENDERPASS

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cam = renderingData.cameraData.camera;

            // Stop the pass rendering in the preview
            if (cam.cameraType != CameraType.Game)
                return;

            if (m_mesh == null)
                m_mesh = GenerateCausticsMesh(1000f);

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_WaterCaustics_Profile))
            {
                bool sunTransformFound = sunTransform != null;
                if (!sunTransformFound)
                {
                    var sun = RenderSettings.sun;
                    if (sun != null)
                    {
                        sunTransform = sun.transform;
                    }
                }
                
                var sunMatrix = sunTransformFound
                    ? sunTransform.localToWorldMatrix
                    : Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(-45f, 45f, 0f), Vector3.one);
                WaterCausticMaterial.SetMatrix(MainLightDir, sunMatrix);
                WaterCausticMaterial.SetFloat(WaterLevel, waterLevel);

                // Create the matrix to position the caustics mesh.
                var position = cam.transform.position;
                position.y = waterLevel;
                var matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);

                // Set up the CommandBuffer and draw the mesh with the caustic material and matrix
                cmd.DrawMesh(m_mesh, matrix, WaterCausticMaterial, 0, 0);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public void Cleanup()
        {
            CoreUtils.Destroy(m_mesh);
            CoreUtils.Destroy(WaterCausticMaterial);
        }

        private static Mesh GenerateCausticsMesh(float size, bool flat = true)
        {
            size *= 0.5f;

            var verts = new Unity.Collections.NativeArray<Vector3>(4,
                Unity.Collections.Allocator.Temp, Unity.Collections.NativeArrayOptions.UninitializedMemory);
            verts[0] = new Vector3(-size, flat ? 0f : -size, flat ? -size : 0f);
            verts[1] = new Vector3( size, flat ? 0f : -size, flat ? -size : 0f);
            verts[2] = new Vector3(-size, flat ? 0f :  size, flat ?  size : 0f);
            verts[3] = new Vector3( size, flat ? 0f :  size, flat ?  size : 0f);

            var tris = new Unity.Collections.NativeArray<ushort>(6,
                Unity.Collections.Allocator.Temp, Unity.Collections.NativeArrayOptions.UninitializedMemory);
            tris[0] = 0;
            tris[1] = 2;
            tris[2] = 1;
            tris[3] = 2;
            tris[4] = 3;
            tris[5] = 1;

            var uvs = new Unity.Collections.NativeArray<Vector2>(4,
                Unity.Collections.Allocator.Temp, Unity.Collections.NativeArrayOptions.UninitializedMemory);
            uvs[0] = new Vector2(0f, 0f);
            uvs[1] = new Vector2(1f, 0f);
            uvs[2] = new Vector2(0f, 1f);
            uvs[3] = new Vector2(1f, 1f);

            Mesh m = new Mesh();

            m.SetVertices(verts);
            m.SetIndices(tris, MeshTopology.Triangles, submesh: 0);
            m.SetUVs(0, uvs);

            return m;
        }
    }

    #endregion
}
