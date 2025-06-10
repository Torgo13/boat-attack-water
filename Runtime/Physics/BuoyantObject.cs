// Buoyancy.cs
// by Alex Zhdankin
// Version 2.1
//
// http://forum.unity3d.com/threads/72974-Buoyancy-script
//
// Terms of use: do whatever you like
//
// Further tweaks by Andre McGrail
//
//

using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using UnityPhysics = UnityEngine.Physics;

namespace WaterSystem.Physics
{
    public class BuoyantObject : MonoBehaviour
    {
        public BuoyancyType _buoyancyType; // type of buoyancy to calculate
        public float density; // density of the object, this is calculated off its volume and mass
        public float volume; // volume of the object, this is calculated via its colliders
        public float voxelResolution = 0.51f; // voxel resolution, represents the half size of a voxel when creating the voxel representation
        private Bounds _voxelBounds; // bounds of the voxels
        public Vector3 centerOfMass = Vector3.zero; // Center Of Mass offset
        public float waterLevelOffset = 0f;

        private const float Dampner = 0.005f;
        private const float WaterDensity = 1000;

        private float _baseDrag; // reference to original drag
        private float _baseAngularDrag; // reference to original angular drag
        private int _guid; // GUID for the height system
        private float3 _localArchimedesForce;

        readonly private NativeList<Vector3> _voxels = new NativeList<Vector3>(Allocator.Persistent); // voxel position
        private NativeArray<float3> _samplePoints; // sample points for height calc
        [NonSerialized] public Data.WaveOutputData[] WaveResults;
        private NativeArray<float3> _velocity; // voxel velocity for buoyancy

        List<Collider> colliders; // colliders attached to this object
        private Rigidbody _rb;
        private DebugDrawing[] _debugInfo; // For drawing force gizmos
        [NonSerialized] public float PercentSubmerged;

        [ContextMenu("Initialize")]
        private void Init()
        {
            _voxels.Clear();

            switch (_buoyancyType)
            {
                case BuoyancyType.NonPhysical:
                    SetupVoxels();
                    SetupData();
                    break;
                case BuoyancyType.NonPhysicalVoxel:
                    SetupColliders();
                    SetupVoxels();
                    SetupData();
                    break;
                case BuoyancyType.Physical:
                    SetupVoxels();
                    SetupData();
                    SetupPhysical();
                    break;
                case BuoyancyType.PhysicalVoxel:
                    SetupColliders();
                    SetupVoxels();
                    SetupData();
                    SetupPhysical();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void SetupVoxels()
        {
            if (_buoyancyType == BuoyancyType.NonPhysicalVoxel || _buoyancyType == BuoyancyType.PhysicalVoxel)
            {
                SliceIntoVoxels();
            }
            else
            {
                _voxels.Clear();
                _voxels.Add(centerOfMass);
            }
        }

        private void SetupData()
        {
            _debugInfo = new DebugDrawing[_voxels.Length];
            WaveResults = new Data.WaveOutputData[_voxels.Length];
            _samplePoints = new NativeArray<float3>(_voxels.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        private void OnEnable()
        {
            _guid = gameObject.GetInstanceID();
            Init();
            LocalToWorldConversion();
        }

        private void SetupColliders()
        {
            if (colliders == null)
                colliders = UnityEngine.Pool.ListPool<Collider>.Get();

            // The object must have a Collider
            GetComponentsInChildren(colliders);
            if (colliders.Count != 0)
                return;

            colliders.Add(gameObject.AddComponent<BoxCollider>());
#if DEBUG
            Debug.LogError($"Buoyancy:Object \"{name}\" had no coll. BoxCollider has been added.");
#endif // DEBUG
        }

        private void Update()
        {
#if STATIC_EVERYTHING
            var dt = 0.0f;
#else
            var dt = Time.deltaTime;
#endif
            switch (_buoyancyType)
            {
                case BuoyancyType.NonPhysical:
                    {
                        var t = transform;
                        var vec = t.position;
                        _samplePoints[0] = vec;
                        vec.y = WaveResults[0].Position.y + waterLevelOffset;
                        t.position = vec;
                        var up = t.up;
                        t.up = Vector3.Slerp(up, WaveResults[0].Normal, dt);
                        break;
                    }
                case BuoyancyType.NonPhysicalVoxel:
                    // do the voxel non-physical
                    break;
                case BuoyancyType.Physical:
                case BuoyancyType.PhysicalVoxel:
                    LocalToWorldJob.CompleteJob(_guid);
                    GetVelocityPoints();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            GerstnerWavesJobs.UpdateSamplePoints(ref _samplePoints, _guid);
            GerstnerWavesJobs.GetData(_guid, ref WaveResults);
        }

        private void FixedUpdate()
        {
            var submergedAmount = 0f;

            switch (_buoyancyType)
            {
                case BuoyancyType.PhysicalVoxel:
                    {
                        LocalToWorldJob.CompleteJob(_guid);
                        //Debug.Log("new pass: " + gameObject.name);
                        UnityPhysics.autoSyncTransforms = false;

                        for (var i = 0; i < _voxels.Length; i++)
                            BuoyancyForce(_samplePoints[i], _velocity[i], WaveResults[i].Position.y + waterLevelOffset, ref submergedAmount, ref _debugInfo[i]);

                        UnityPhysics.SyncTransforms();
                        UnityPhysics.autoSyncTransforms = true;
                        UpdateDrag(submergedAmount);
                        break;
                    }
                case BuoyancyType.Physical:
                    //LocalToWorldJob.CompleteJob(_guid);
                    BuoyancyForce(Vector3.zero, _velocity[0], WaveResults[0].Position.y + waterLevelOffset, ref submergedAmount, ref _debugInfo[0]);
                    //UpdateDrag(submergedAmount);
                    break;
                case BuoyancyType.NonPhysical:
                    break;
                case BuoyancyType.NonPhysicalVoxel:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void LateUpdate() { LocalToWorldConversion(); }

        private void OnDestroy()
        {
            CleanUp();
        }

        void CleanUp()
        {
            if (_samplePoints.IsCreated)
            {
                _samplePoints.Dispose();
            }

            if (_voxels.IsCreated)
            {
                _voxels.Dispose();
            }

            if (_buoyancyType == BuoyancyType.Physical || _buoyancyType == BuoyancyType.PhysicalVoxel)
            {
                LocalToWorldJob.Cleanup(_guid);
            }

            if (_velocity.IsCreated)
            {
                _velocity.Dispose();
            }

            if (colliders != null)
            {
                UnityEngine.Pool.ListPool<Collider>.Release(colliders);
            }
        }

        private void LocalToWorldConversion()
        {
            if (_buoyancyType != BuoyancyType.Physical && _buoyancyType != BuoyancyType.PhysicalVoxel)
                return;

            var transformMatrix = transform.localToWorldMatrix;
            LocalToWorldJob.ScheduleJob(_guid, transformMatrix);
        }

        private void BuoyancyForce(Vector3 position, float3 velocity, float waterHeight, ref float submergedAmount, ref DebugDrawing debug)
        {
            debug.Position = position;
            debug.WaterHeight = waterHeight;
            debug.Force = Vector3.zero;

            if (position.y - voxelResolution >= waterHeight)
                return;

            var k = math.clamp(waterHeight - (position.y - voxelResolution), 0f, 1f);

            submergedAmount += k / _voxels.Length;

            var localDampingForce = Dampner * _rb.mass * -velocity;
            var force = localDampingForce + math.sqrt(k) * _localArchimedesForce;
            _rb.AddForceAtPosition(force, position);

            debug.Force = force; // For drawing force Gizmos
            //Debug.Log(string.Format("Position: {0:f1} -- Force: {1:f2} -- Height: {2:f2}\nVelocity: {3:f2} -- Damp: {4:f2} -- Mass: {5:f1} -- K: {6:f2}", wp, force, waterLevel, velocity, localDampingForce, RB.mass, localArchimedesForce));
        }

        private void UpdateDrag(float submergedAmount)
        {
            PercentSubmerged = math.lerp(PercentSubmerged, submergedAmount, 0.25f);
            _rb.drag = _baseDrag + _baseDrag * (PercentSubmerged * 10f);
            _rb.angularDrag = _baseAngularDrag + PercentSubmerged * 0.5f;
        }

        private void GetVelocityPoints()
        {
            for (var i = 0; i < _voxels.Length; i++) { _velocity[i] = _rb.GetPointVelocity(_samplePoints[i]); }
        }

        private void SliceIntoVoxels()
        {
            var t = transform;
            t.GetPositionAndRotation(out var pos, out var rot);
            var size = t.localScale;
            t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            t.localScale = Vector3.one;

            _voxels.Clear();

            var rawBounds = VoxelBounds();
            _voxelBounds = rawBounds;
            _voxelBounds.size = RoundVector(rawBounds.size, voxelResolution);
            for (var ix = -_voxelBounds.extents.x; ix < _voxelBounds.extents.x; ix += voxelResolution)
            {
                for (var iy = -_voxelBounds.extents.y; iy < _voxelBounds.extents.y; iy += voxelResolution)
                {
                    for (var iz = -_voxelBounds.extents.z; iz < _voxelBounds.extents.z; iz += voxelResolution)
                    {
                        var res = voxelResolution * 0.5f;
                        var p = new Vector3(res + ix, res + iy, res + iz) + _voxelBounds.center;

                        var inside = false;
                        foreach (var t1 in colliders)
                        {
                            if (PointIsInsideCollider(t1, p))
                            {
                                inside = true;
                                break;
                            }
                        }

                        if (inside)
                            _voxels.Add(p);
                    }
                }
            }

            t.SetPositionAndRotation(pos, rot);
            t.localScale = size;
            volume = Mathf.Min(rawBounds.size.x * rawBounds.size.y * rawBounds.size.z, Mathf.Pow(voxelResolution, 3f) * _voxels.Length);
            if (gameObject.TryGetComponent<Rigidbody>(out var rb))
            {
                density = rb.mass / volume;
            }
        }

        private Bounds VoxelBounds()
        {
            var bounds = colliders[0].bounds;
            foreach (var nextCollider in colliders)
            {
                bounds.Encapsulate(nextCollider.bounds);
            }

            return bounds;
        }

        private static Vector3 RoundVector(Vector3 vec, float rounding)
        {
            return new Vector3(Mathf.Ceil(vec.x / rounding) * rounding, Mathf.Ceil(vec.y / rounding) * rounding, Mathf.Ceil(vec.z / rounding) * rounding);
        }

        private bool PointIsInsideCollider(Collider c, Vector3 p)
        {
            var cp = UnityPhysics.ClosestPoint(p, c, Vector3.zero, Quaternion.identity);
			return Vector3.Distance(cp, p) < 0.01f;
        }

        private void SetupPhysical()
        {
            if (!TryGetComponent(out _rb))
            {
                _rb = gameObject.AddComponent<Rigidbody>();
#if DEBUG
                Debug.LogWarning($"Buoyancy:Object \"{name}\" had no Rigidbody. Rigidbody has been added.");
#endif // DEBUG
            }

            _rb.centerOfMass = centerOfMass + _voxelBounds.center;
            _baseDrag = _rb.drag;
            _baseAngularDrag = _rb.angularDrag;

            _velocity = new NativeArray<float3>(_voxels.Length,
                Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            var archimedesForceMagnitude = WaterDensity * Mathf.Abs(UnityPhysics.gravity.y) * volume;
            _localArchimedesForce = new float3(0, archimedesForceMagnitude, 0) / _voxels.Length;
            LocalToWorldJob.SetupJob(_guid, _voxels, ref _samplePoints);
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void OnDrawGizmosSelected()
        {
            const float gizmoSize = 0.05f;
            var t = transform;
            Gizmos.matrix = t.localToWorldMatrix;
            Color c = Color.yellow;

            if (_voxels.IsCreated)
            {
                Gizmos.color = c;

                foreach (var p in _voxels)
                {
                    Gizmos.DrawCube(p, new Vector3(gizmoSize, gizmoSize, gizmoSize));
                }
            }

            c.a = 0.25f;
            Gizmos.color = c;

            if (voxelResolution >= 0.1f)
            {
                Gizmos.DrawWireCube(_voxelBounds.center, _voxelBounds.size);
                Vector3 center = _voxelBounds.center;
                float y = center.y - _voxelBounds.extents.y;
                for (float x = -_voxelBounds.extents.x; x < _voxelBounds.extents.x; x += voxelResolution)
                {
                    Gizmos.DrawLine(new Vector3(x, y, -_voxelBounds.extents.z + center.z), new Vector3(x, y, _voxelBounds.extents.z + center.z));
                }

                for (float z = -_voxelBounds.extents.z; z < _voxelBounds.extents.z; z += voxelResolution)
                {
                    Gizmos.DrawLine(new Vector3(-_voxelBounds.extents.x, y, z + center.z), new Vector3(_voxelBounds.extents.x, y, z + center.z));
                }
            }
            else
                _voxelBounds = VoxelBounds();

            Gizmos.color = Color.red;
            Gizmos.DrawSphere(_voxelBounds.center + centerOfMass, 0.2f);

            Gizmos.matrix = Matrix4x4.identity;

            if (_debugInfo != null)
            {
                foreach (DebugDrawing debug in _debugInfo)
                {
                    bool inWater = debug.Force.sqrMagnitude > 0f;
                    Gizmos.color = inWater ? Color.red : Color.cyan;
                    Gizmos.DrawCube(debug.Position, new Vector3(gizmoSize, gizmoSize, gizmoSize)); // drawCenter
                    var water = debug.Position;
                    water.y = debug.WaterHeight;
                    Gizmos.DrawLine(debug.Position, water); // draw the water line
                    Gizmos.DrawSphere(water, gizmoSize * (inWater ? 2f : 1f));
                    if (_buoyancyType == BuoyancyType.Physical || _buoyancyType == BuoyancyType.PhysicalVoxel)
                    {
                        Gizmos.color = Color.red;
                        Gizmos.DrawRay(debug.Position, debug.Force / _rb.mass); // draw force
                    }
                }
            }
        }

        private struct DebugDrawing
        {
            public Vector3 Force;
            public Vector3 Position;
            public float WaterHeight;
        }

        public enum BuoyancyType
        {
            NonPhysical,
            NonPhysicalVoxel,
            Physical,
            PhysicalVoxel
        }
    }
}
