using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

namespace WaterSystem
{
    public static class LocalToWorldJob
    {
        private static readonly Dictionary<int, TransformLocalToWorld> Data = new Dictionary<int, TransformLocalToWorld>();

        [BurstCompile]
        struct LocalToWorldConvertJob : IJobParallelFor
        {
            [WriteOnly] public NativeArray<float3> PositionsWorld;
            [ReadOnly] public Matrix4x4 Matrix;
            [ReadOnly] public NativeArray<float3> PositionsLocal;

            // The code actually running on the job
            public void Execute(int i)
            {
                var pos = float4.zero;
                pos.xyz = PositionsLocal[i];
                pos.w = 1f;
                pos = Matrix * pos;
                PositionsWorld[i] = pos.xyz;
            }
        }

        public static void SetupJob(int guid, NativeList<Vector3> positions, ref NativeArray<float3> output)
        {
            var jobData = new TransformLocalToWorld
            {
                PositionsWorld = output,
                PositionsLocal = new NativeArray<float3>(positions.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)
            };

            for (var i = 0; i < positions.Length; i++)
                jobData.PositionsLocal[i] = positions[i];

            Data.Add(guid, jobData);
        }

        public static void ScheduleJob(int guid, Matrix4x4 localToWorld)
        {
            if (Data[guid].Processing)
                return;

            Data[guid].Job = new LocalToWorldConvertJob
            {
                PositionsWorld = Data[guid].PositionsWorld,
                PositionsLocal = Data[guid].PositionsLocal,
                Matrix = localToWorld
            };

            Data[guid].Handle = Data[guid].Job.Schedule(Data[guid].PositionsLocal.Length, 32);
            Data[guid].Processing = true;
            JobHandle.ScheduleBatchedJobs();
        }

        public static void CompleteJob(int guid)
        {
            Data[guid].Handle.Complete();
            Data[guid].Processing = false;
        }

        public static void Cleanup(int guid)
        {
            if (!Data.TryGetValue(guid, out TransformLocalToWorld value))
                return;

            value.Handle.Complete();
            value.PositionsWorld.Dispose();
            value.PositionsLocal.Dispose();
            Data.Remove(guid);
        }

        class TransformLocalToWorld
        {
            public NativeArray<float3> PositionsLocal;
            public NativeArray<float3> PositionsWorld;
            public JobHandle Handle;
            public LocalToWorldConvertJob Job;
            public bool Processing;
        }
    }
}
