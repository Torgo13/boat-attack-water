using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static class LocalToWorldJob
{
    private static readonly Dictionary<int, TransformLocalToWorld> Data = new Dictionary<int, TransformLocalToWorld>();

    [BurstCompile]
    struct LocalToWorldConvertJob : IJobFor
    {
        [NativeMatchesParallelForLength]
        [WriteOnly] public NativeArray<float3> PositionsWorld;
        [ReadOnly] public Matrix4x4 Matrix;
        [NativeMatchesParallelForLength]
        [ReadOnly] public NativeArray<float3> PositionsLocal;

        // The code actually running on the job
        public void Execute(int i)
        {
            Vector4 pos;
            pos.x = PositionsLocal[i].x;
            pos.y = PositionsLocal[i].y;
            pos.z = PositionsLocal[i].z;
            pos.w = 1f;
            pos = Matrix * pos;
            PositionsWorld[i] = new float3(pos.x, pos.y, pos.z);
        }
    }

    public static void SetupJob(int guid, Vector3[] positions, ref NativeArray<float3> output)
    {
        var jobData = new TransformLocalToWorld
        {
            PositionsWorld = output,
            PositionsLocal = new NativeArray<float3>(positions.Length, Allocator.Persistent)
        };

        for (var i = 0; i < positions.Length; i++)
            jobData.PositionsLocal[i] = positions[i];

        Data.Add(guid, jobData);
    }

    public static void ScheduleJob(int guid, in Matrix4x4 localToWorld)
    {
        var data = Data[guid];
        if (data.Processing)
            return;

        data.Job = new LocalToWorldConvertJob()
        {
            PositionsWorld = data.PositionsWorld,
            PositionsLocal = data.PositionsLocal,
            Matrix = localToWorld
        };

        data.Handle = data.Job.Schedule(data.PositionsLocal.Length, dependency: default);
        data.Processing = true;
        Data[guid] = data;
        JobHandle.ScheduleBatchedJobs();
    }

    public static void CompleteJob(int guid)
    {
        var data = Data[guid];
        data.Handle.Complete();
        data.Processing = false;
        Data[guid] = data;
    }

    public static void Cleanup(int guid)
    {
        if (!Data.Remove(guid, out var data)) return;
        data.Handle.Complete();
        data.PositionsWorld.Dispose();
        data.PositionsLocal.Dispose();
    }

    struct TransformLocalToWorld
    {
        public NativeArray<float3> PositionsLocal;
        public NativeArray<float3> PositionsWorld;
        public JobHandle Handle;
        public LocalToWorldConvertJob Job;
        public bool Processing;
    }
}