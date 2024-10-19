using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WaterSystem.Rendering
{
    #region Water Effects Pass

    public class WaterFxPass : ScriptableRenderPass
    {
        private const string m_BufferATexture = "_WaterBufferA";
        private const string m_BufferBTexture = "_WaterBufferB";
        static readonly int WaterBufferA = Shader.PropertyToID(m_BufferATexture);
        static readonly int WaterBufferB = Shader.PropertyToID(m_BufferBTexture);
        private const string m_BufferDepthTexture = "_WaterBufferDepth";

#if UNITY_2022_1_OR_NEWER
        RTHandle[] multiTargets = new RTHandle[2];
        private RTHandle m_BufferTargetA, m_BufferTargetB;
        private RTHandle m_BufferTargetDepth;
#else
        private int m_BufferTargetA, m_BufferTargetB;
#endif

        //private const string k_RenderWaterFXTag = "Render Water FX";
        private readonly ShaderTagId m_WaterFXShaderTag = new ShaderTagId("WaterFX");

        // r = foam mask
        // g = normal.x
        // b = normal.z
        // a = displacement
        private readonly Color m_ClearColor = new Color(0.0f, 0.5f, 0.5f, 0.5f);

        private FilteringSettings m_FilteringSettings;

        public WaterFxPass()
        {
            //profilingSampler = new ProfilingSampler(k_RenderWaterFXTag);
            // Only render transparent objects
            m_FilteringSettings = new FilteringSettings(RenderQueueRange.transparent);
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
        }

        // Calling Configure since we are wanting to render into a RenderTexture and control cleat
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            RenderTextureDescriptor td = GetRTD(cameraTextureDescriptor.width, cameraTextureDescriptor.height);
            //float resolutionScale = GamingIsLove.Makinom.Maki.Game.Variables.GetFloat("resolutionScale");
            //Vector2 scaleFactor = new Vector2(resolutionScale, resolutionScale);

#if UNITY_2022_1_OR_NEWER
            RenderingUtils.ReAllocateIfNeeded(ref m_BufferTargetA, td, FilterMode.Bilinear, name: m_BufferATexture);
            RenderingUtils.ReAllocateIfNeeded(ref m_BufferTargetB, td, FilterMode.Bilinear, name: m_BufferBTexture);
            RenderingUtils.ReAllocateIfNeeded(ref m_BufferTargetDepth, td, FilterMode.Bilinear, name: m_BufferDepthTexture);
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
            {
                return;
            }

            DrawingSettings drawSettings = CreateDrawingSettings(m_WaterFXShaderTag, ref renderingData, SortingCriteria.CommonTransparent);

            CommandBuffer cmd = CommandBufferPool.Get();
            cmd.Clear();
            context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref m_FilteringSettings);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private RenderTextureDescriptor GetRTD(int width, int height)
        {
            return new RenderTextureDescriptor(width, height, SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.Default, 0)
            {
                // dimension
                dimension = TextureDimension.Tex2D,
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                stencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None,
                volumeDepth = 1,
                sRGB = false,
                memoryless = RenderTextureMemoryless.Depth
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
        private Mesh infiniteMesh;
        private Shader infiniteShader;
        private Material infiniteMaterial;
        static readonly int BumpScale = Shader.PropertyToID("_BumpScale");

        public InfiniteWaterPass(Mesh mesh, Shader shader)
        {
            if (mesh) infiniteMesh = mesh;
            if (shader) infiniteShader = shader;
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;

            if (cam.cameraType != CameraType.Game &&
                cam.cameraType != CameraType.SceneView ||
                cam.name.Contains("Reflections")) return;

            if (infiniteMesh == null)
            {
#if UNITY_EDITOR || DEBUG
                Debug.LogError("Infinite Water Pass Mesh is missing.");
#endif
                return;
            }

            if (infiniteShader)
            {
                if(infiniteMaterial == null)
                    infiniteMaterial = new Material(infiniteShader);
            }

            if (!infiniteMaterial || !infiniteMesh) return;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, new ProfilingSampler("Infinite Water")))
            {
                var probe = RenderSettings.ambientProbe;

                infiniteMaterial.SetFloat(BumpScale, 0.5f);

                // Create the matrix to position the caustics mesh.
                var position = cam.transform.position;
                var matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
                // Setup the CommandBuffer and draw the mesh with the infinite water material and matrix
                MaterialPropertyBlock matBloc = new MaterialPropertyBlock();
                matBloc.CopySHCoefficientArraysFrom(new[] { probe });
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
        private ProfilingSampler m_WaterCaustics_Profile = new ProfilingSampler(k_RenderWaterCausticsTag);
        private readonly Material WaterCausticMaterial;
        private Mesh m_mesh;
        private static readonly int WaterLevel = Shader.PropertyToID("_WaterLevel");
        private static readonly int MainLightDir = Shader.PropertyToID("_MainLightDir");

        public WaterCausticsPass(Material material)
        {
            WaterCausticMaterial = material;
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            // Stop the pass rendering in the preview or material missing
            if (cam.cameraType != CameraType.Game || WaterCausticMaterial == null)
            {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_WaterCaustics_Profile))
            {
                Matrix4x4 sunMatrix = RenderSettings.sun != null
                    ? RenderSettings.sun.transform.localToWorldMatrix
                    : Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(-45f, 45f, 0f), Vector3.one);
                WaterCausticMaterial.SetMatrix(MainLightDir, sunMatrix);
                float waterLevel = Ocean.Instance.transform.position.y;
                WaterCausticMaterial.SetFloat(WaterLevel, waterLevel);


                // Create mesh if needed
                if (m_mesh == null)
                {
                    m_mesh = GenerateCausticsMesh(1000f);
                }

                // Create the matrix to position the caustics mesh.
                Vector3 position = cam.transform.position;
                //position.y = 0; // TODO should read a global 'water height' variable.
                position.y = waterLevel;
                Matrix4x4 matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
                // Setup the CommandBuffer and draw the mesh with the caustic material and matrix
                cmd.DrawMesh(m_mesh, matrix, WaterCausticMaterial, 0, 0);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public void Cleanup()
        {
            /*if (WaterCausticMaterial != null)
            {
                CoreUtils.Destroy(WaterCausticMaterial);
            }*/
            if (m_mesh != null)
            {
                CoreUtils.Destroy(m_mesh);
            }
        }

        private Mesh GenerateCausticsMesh(float size, bool flat = true)
        {
            size *= 0.5f;

            Vector3[] verts = {
                new Vector3(-size, flat ? 0f : -size, flat ? -size : 0f),
                new Vector3(size, flat ? 0f : -size, flat ? -size : 0f),
                new Vector3(-size, flat ? 0f : size, flat ? size : 0f),
                new Vector3(size, flat ? 0f : size, flat ? size : 0f)
            };

            int[] tris = {
                0, 2, 1,
                2, 3, 1
            };

            Vector2[] uvs = {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f)
            };

            Mesh m = new Mesh
            {
                vertices = verts,
                triangles = tris,
                uv = uvs
            };

            return m;
        }
    }

    #endregion
}
