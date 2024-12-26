using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace WaterSystem.Physics
{
    [ExecuteAlways]
    public class WaterLevelDebug : MonoBehaviour
    {
        public int2 arrayCount = new int2(1, 1);
        public float arraySpacing = 1f;

        private NativeArray<float3> samplePositions;
        private Data.WaveOutputData[] positions;

        private void OnValidate()
        {
            UpdateSamplePoints();
        }

        private void OnEnable()
        {
            UpdateSamplePoints();
        }

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
            GerstnerWavesJobs.RemoveSamplePoints(gameObject.GetInstanceID());

            if (samplePositions.IsCreated)
                samplePositions.Dispose();
        }

        private void Update()
        {
            if (transform.hasChanged)
                UpdateSamplePoints();

            GerstnerWavesJobs.UpdateSamplePoints(ref samplePositions, gameObject.GetInstanceID());
            GerstnerWavesJobs.GetData(gameObject.GetInstanceID(), ref positions);
        }

        private void UpdateSamplePoints()
        {
            if (!samplePositions.IsCreated)
            {
                samplePositions = new NativeArray<float3>(arrayCount.x * arrayCount.y, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (samplePositions.Length != arrayCount.x * arrayCount.y)
            {
                GerstnerWavesJobs.RemoveSamplePoints(gameObject.GetInstanceID());
                samplePositions.Dispose();
                samplePositions = new NativeArray<float3>(arrayCount.x * arrayCount.y, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            float3 pos = 0;
            for (var i = 0; i < arrayCount.x; i++)
            {
                pos.x = arraySpacing * i - arraySpacing * (arrayCount.x - 1) * 0.5f;
                for (var j = 0; j < arrayCount.y; j++)
                {
                    pos.z = arraySpacing * j - arraySpacing * (arrayCount.y - 1) * 0.5f;
                    samplePositions[i * arrayCount.y + j] = transform.TransformPoint(pos);
                }
            }

            if (positions == null || positions.Length != samplePositions.Length)
                positions = new Data.WaveOutputData[samplePositions.Length];
        }

        private void OnDrawGizmos()
        {
            var colA = new Color(1f, 1f, 1f, 0.025f);
            var colB = new Color(1f, 1f, 1f, 0.75f);

            for (var index = 0; index < samplePositions.Length; index++)
            {
                var samplePos = samplePositions[index];
                var finalPos = positions[index].Position;
                Gizmos.color = colA;
                Gizmos.DrawSphere(samplePos, 0.1f);
                Gizmos.DrawLine(samplePos, finalPos);
                Gizmos.color = colB;
                Gizmos.DrawSphere(finalPos, 0.1f);
            }
        }
    }
}
