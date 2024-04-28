using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace WaterSystem
{
    //[ExecuteAlways]
    [AddComponentMenu("URP Water System/Depth Generator")]
    public class DepthGenerator : MonoBehaviour
    {
        // Global data
        public static DepthGenerator Current;
        public static DepthData _depthData;
        // Global depth values
        public static NativeArray<half> _globalDepthValues;
        //private static int _depthValueCount;

        // Static vars
        private static readonly int Depth = Shader.PropertyToID("_Depth");

        [SerializeField] internal Texture2D depthTile;
        [HideInInspector, SerializeField] private Mesh mesh;
        [HideInInspector, SerializeField] private Material debugMaterial;
        [HideInInspector, SerializeField] private Shader shader;
        //private Camera _depthCam;
        private Material _material;
        private Transform _transform;
        private Vector3 _positionWS;

        public int size = 250;
        public int tileRes = 1024;

        public half range = (half)20.0;
        public half offset = (half)4.0;
        public LayerMask mask;

        //private static readonly float maxDepth = -999f;

#if UNITY_EDITOR
        [ContextMenu("Capture Depth")]
        public void CaptureDepth()
        {
            DepthBaking.CaptureDepth(tileRes, size, transform, mask, range, offset);
            Current = this;
        }
#endif

        private void OnEnable()
        {
            _transform = transform;
            _positionWS = _transform.position;

            if (depthTile == null)
            {
#if UNITY_EDITOR
                Scene activeScene = gameObject.scene;
                //var sceneName = activeScene.name.Split('.')[0];
                string path = activeScene.path.Split('.')[0];
                string file = $"{gameObject.name}_DepthTile.png";
                try
                {
                    depthTile = AssetDatabase.LoadAssetAtPath<Texture2D>($"{path}/{file}");
                    //StoreDepthValues();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Failed to load {GetType().Name} tile, please make sure it is generated:{e}");
                    throw;
                }
#elif DEBUG
                Debug.LogWarning($"{GetType().Name} on gameobject {gameObject.name} is missing tile texture");
#endif
            }

            _depthData = CompileDepthData();
            if (!_globalDepthValues.IsCreated)
            {
                _globalDepthValues = new NativeArray<half>(tileRes * tileRes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            StoreDepthValues();
        }

        public static void CleanUp()
        {
            if (_globalDepthValues.IsCreated)
            {
                _globalDepthValues.Dispose();
            }
        }

        private void LateUpdate()
        {
            if (shader && !_material)
            {
                _material = CoreUtils.CreateEngineMaterial(shader);
            }

            if (!depthTile || !_material)
            {
                return;
            }

            Matrix4x4 matrix = Matrix4x4.TRS(_positionWS, Quaternion.Euler(90, 0, 0), new Vector3(size, size, 0f));
            _material.SetTexture(Depth, depthTile);
            Graphics.DrawMesh(mesh, matrix, _material, 0);
        }


        private void OnDrawGizmosSelected()
        {
#if UNITY_EDITOR
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(_transform.position, new Vector3(size, 0f, size));
            if (mesh && depthTile)
            {
                Matrix4x4 matrix = Matrix4x4.TRS(_transform.position, Quaternion.Euler(90, 0, 0), new Vector3(size, size, 0f));
                debugMaterial.mainTexture = depthTile;
                debugMaterial.SetPass(0);
                Graphics.DrawMeshNow(mesh, matrix);
            }
#endif
        }

        [BurstCompile]
        public struct WaterDepth : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Position;
            [ReadOnly] public NativeArray<half> DepthValues;
            [ReadOnly] public DepthData DepthData;

            [WriteOnly] public NativeArray<float> Depth;

            public void Execute(int index)
            {
                float2 UVpos = GetUVPosition(Position[index]);
                Depth[index] = UVpos.x is > 1.0f or < 0.0f || UVpos.y is > 1.0f or < 0.0f
                    ? -999.0f
                    : GetDepth(UVpos);
            }

            private readonly float2 GetUVPosition(float3 position)
            {
                position.x -= DepthData.PositionWS.x;
                position.z -= DepthData.PositionWS.z;
                position /= DepthData.Size;
                position += 0.5f;

                return position.zx;
            }

            private float GetDepth(float2 UVPos)
            {
                UVPos = math.clamp(UVPos, 0.0f, 0.999f);

                int index = (int)(UVPos.x * DepthData.TileRes) * DepthData.TileRes + (int)(UVPos.y * DepthData.TileRes);
                float depth = 1.0f - DepthValues[index];
                return -(depth * (DepthData.Range + DepthData.Offset)) + DepthData.Offset;
            }
        }

        private void StoreDepthValues()
        {
            if (!depthTile)
            {
                return;
            }

            NativeArray<Color> pixels = depthTile.GetPixelData<Color>(0);
            int width = depthTile.width; int height = depthTile.height;
            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < height; j++)
                {
                    int index = i * width + j;
                    _globalDepthValues[index] = (half)pixels[index].r;
                }
            }
        }

        private DepthData CompileDepthData()
        {
            return new DepthData(
                _positionWS,
                size,
                tileRes,
                range,
                offset);
        }

        [Serializable]
        public struct DepthData
        {
            public readonly int TileRes;
            public readonly int Size;
            public readonly half Range;
            public readonly half Offset;
            public readonly float3 PositionWS;

            public DepthData(float3 position, int size, int tileRes, half range, half offset)
            {
                PositionWS = position;
                Size = size;
                TileRes = tileRes;
                Offset = offset;
                Range = range;
            }
        }
    }
}