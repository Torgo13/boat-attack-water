using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WaterSystem.Rendering
{
    [DisallowMultipleRendererFeature("Water")]
    [Tooltip("Water")]
    public class WaterRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] bool infiniteWater;
        [SerializeField] Mesh infiniteWaterMesh;
        [SerializeField] Shader infiniteWaterShader;

        [SerializeField] Shader causticShader;
        [SerializeField] Texture2D defaultSurfaceMap;

        InfiniteWaterPass infiniteWaterPass;
        WaterFxPass waterFxPass;
        WaterCausticsPass waterCausticsPass;

        public override void AddRenderPasses(ScriptableRenderer renderer,
                                        ref RenderingData renderingData)
        {
            if (renderingData.cameraData.cameraType != CameraType.Game)
                return;

            if (Ocean.Instance == null)
                return;

            if (infiniteWater)
                renderer.EnqueuePass(infiniteWaterPass);

            renderer.EnqueuePass(waterFxPass);
            renderer.EnqueuePass(waterCausticsPass);
        }

        public override void SetupRenderPasses(ScriptableRenderer renderer,
                                            in RenderingData renderingData)
        {
            if (renderingData.cameraData.cameraType != CameraType.Game)
                return;

            waterCausticsPass.ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void Create()
        {
            Material causticMaterial = CoreUtils.CreateEngineMaterial(causticShader);
            causticMaterial.mainTexture = defaultSurfaceMap;

            if (infiniteWater)
                infiniteWaterPass = new InfiniteWaterPass(infiniteWaterMesh, infiniteWaterShader);

            waterFxPass = new WaterFxPass();
            waterCausticsPass = new WaterCausticsPass(causticMaterial);
        }

        protected override void Dispose(bool disposing)
        {
            infiniteWaterPass?.Cleanup();
            waterFxPass?.Cleanup();
            waterCausticsPass?.Cleanup();
        }
    }
}
