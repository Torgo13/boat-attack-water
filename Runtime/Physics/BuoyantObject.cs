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
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityPhysics = UnityEngine.Physics;

namespace WaterSystem.Physics
{
    public class BuoyantObject : MonoBehaviour
    {
        public BuoyancyType _buoyancyType; // type of buoyancy to calculate
        public float density; // density of the object, this is calculated off it's volume and mass
        public float volume; // volume of the object, this is calculated via it's colliders
        public float voxelResolution = 0.51f; // voxel resolution, represents the half size of a voxel when creating the voxel representation
        private Bounds _voxelBounds; // bounds of the voxels
        public Vector3 centerOfMass = Vector3.zero; // Center Of Mass offset
        public float waterLevelOffset;

        private const float Dampner = 0.005f;
        private const float WaterDensity = 1000;

        private float _baseDrag; // reference to original drag
        private float _baseAngularDrag; // reference to original angular drag
        private int _guid; // GUID for the height system
        private float3 _localArchimedesForce;

        private Vector3[] _voxels; // voxel position
        private NativeArray<float3> _samplePoints; // sample points for height calc
        [NonSerialized] public Data.WaveOutputData[] WaveResults;
        private float3[] _velocity; // voxel velocity for buoyancy

        [SerializeField] Collider[] colliders; // colliders attached ot this object
        private Rigidbody _rb;
        private DebugDrawing[] _debugInfo; // For drawing force gizmos
        [NonSerialized] public float PercentSubmerged;

        [ContextMenu("Initialize")]
        private void Init()
        {
            _voxels = null;

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
            if (_buoyancyType is BuoyancyType.NonPhysicalVoxel or BuoyancyType.PhysicalVoxel)
            {
                SliceIntoVoxels();
            }
            else
            {
                _voxels = new Vector3[1];
                _voxels[0] = centerOfMass;
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
            // The object must have a Collider
            colliders = GetComponentsInChildren<Collider>();
            if (colliders.Length != 0)
            {
                return;
            }

            colliders = new Collider[1];
            colliders[0] = gameObject.AddComponent<BoxCollider>();
#if UNITY_EDITOR || DEBUG
            Debug.LogError($"Buoyancy:Object \"{name}\" had no coll. BoxCollider has been added.");
#endif
        }

        private void Update()
        {
#if STATIC_EVERYTHING
            var dt = 0.0f;
#else
            float dt = Time.deltaTime;
#endif
            switch (_buoyancyType)
            {
                case BuoyancyType.NonPhysical:
                    {
                        Transform t = transform;
                        Vector3 vec = t.position;
                        _samplePoints[0] = vec;
                        vec.y = WaveResults[0].Position.y + waterLevelOffset;
                        t.position = vec;
                        Vector3 up = t.up;
                        t.up = Vector3.Slerp(up, WaveResults[0].Normal, dt);
                        break;
                    }
                case BuoyancyType.NonPhysicalVoxel:
                    // do the voxel non-physical
                    break;
                case BuoyancyType.Physical:
                    LocalToWorldJob.CompleteJob(_guid);
                    GetVelocityPoints();
                    break;
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
            float submergedAmount = 0f;

            switch (_buoyancyType)
            {
                case BuoyancyType.PhysicalVoxel:
                    {
                        LocalToWorldJob.CompleteJob(_guid);
                        //Debug.Log("new pass: " + gameObject.name);
                        UnityPhysics.autoSyncTransforms = false;

                        int voxelCount = _voxels.Length;
                        for (int i = 0; i < voxelCount; i++)
                        {
                            BuoyancyForce(_samplePoints[i], _velocity[i], WaveResults[i].Position.y + waterLevelOffset, ref submergedAmount, ref _debugInfo[i]);
                        }

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
            if (_buoyancyType is BuoyancyType.Physical or BuoyancyType.PhysicalVoxel)
            {
                LocalToWorldJob.Cleanup(_guid);
            }
        }

        private void LocalToWorldConversion()
        {
            if (_buoyancyType != BuoyancyType.Physical && _buoyancyType != BuoyancyType.PhysicalVoxel)
            {
                return;
            }

            Matrix4x4 transformMatrix = transform.localToWorldMatrix;
            LocalToWorldJob.ScheduleJob(_guid, transformMatrix);
        }

        private void BuoyancyForce(Vector3 position, float3 velocity, float waterHeight, ref float submergedAmount, ref DebugDrawing debug)
        {
            debug.Position = position;
            debug.WaterHeight = waterHeight;
            debug.Force = Vector3.zero;

            if (position.y - voxelResolution >= waterHeight)
            {
                return;
            }

            float k = math.clamp(waterHeight - (position.y - voxelResolution), 0f, 1f);

            submergedAmount += k / _voxels.Length;

            float3 force = Dampner * _rb.mass * -velocity + math.sqrt(k) * _localArchimedesForce;
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
            int voxelCount = _voxels.Length;
            for (int i = 0; i < voxelCount; i++) { _velocity[i] = _rb.GetPointVelocity(_samplePoints[i]); }
        }

        private void SliceIntoVoxels()
        {
            Transform t = transform;
            Quaternion rot = t.rotation;
            Vector3 pos = t.position;
            Vector3 size = t.localScale;
            t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            t.localScale = Vector3.one;

            _voxels = null;
            List<Vector3> points = new List<Vector3>();

            Bounds rawBounds = VoxelBounds();
            _voxelBounds = rawBounds;
            _voxelBounds.size = RoundVector(rawBounds.size, voxelResolution);
            for (float ix = -_voxelBounds.extents.x; ix < _voxelBounds.extents.x; ix += voxelResolution)
            {
                for (float iy = -_voxelBounds.extents.y; iy < _voxelBounds.extents.y; iy += voxelResolution)
                {
                    for (float iz = -_voxelBounds.extents.z; iz < _voxelBounds.extents.z; iz += voxelResolution)
                    {
                        float res = voxelResolution * 0.5f;
                        Vector3 p = new Vector3(res + ix, res + iy, res + iz) + _voxelBounds.center;

                        bool inside = false;
                        int collidersLength = colliders.Length;
                        for (int i = 0; i < collidersLength; i++)
                        {
                            if (PointIsInsideCollider(colliders[i], p))
                            {
                                inside = true;
                                break;
                            }
                        }
                        if (inside)
                        {
                            points.Add(p);
                        }
                    }
                }
            }

            _voxels = points.ToArray();
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
            Bounds bounds = colliders[0].bounds;
            int collidersLength = colliders.Length;
            for (int i = 0; i < collidersLength; i++)
            {
                bounds.Encapsulate(colliders[i].bounds);
            }
            return bounds;
        }

        private static Vector3 RoundVector(Vector3 vec, float rounding)
        {
            return new Vector3(Mathf.Ceil(vec.x / rounding) * rounding, Mathf.Ceil(vec.y / rounding) * rounding, Mathf.Ceil(vec.z / rounding) * rounding);
        }

        private static bool PointIsInsideCollider(Collider c, Vector3 p)
        {
            return Vector3.Distance(UnityPhysics.ClosestPoint(p, c, Vector3.zero, Quaternion.identity), p) < 0.01f;
        }

        private void SetupPhysical()
        {
            if (!TryGetComponent<Rigidbody>(out _rb))
            {
                _rb = gameObject.AddComponent<Rigidbody>();
#if UNITY_EDITOR || DEBUG
                Debug.LogWarning($"Buoyancy:Object \"{name}\" had no Rigidbody. Rigidbody has been added.");
#endif
            }
            _rb.centerOfMass = centerOfMass + _voxelBounds.center;
            _baseDrag = _rb.drag;
            _baseAngularDrag = _rb.angularDrag;

            _velocity = new float3[_voxels.Length];
            _localArchimedesForce = new float3(0f, WaterDensity * Mathf.Abs(UnityPhysics.gravity.y) * volume, 0f) / _voxels.Length;
            LocalToWorldJob.SetupJob(_guid, _voxels, ref _samplePoints);
        }

        private void OnDrawGizmosSelected()
        {
            const float gizmoSize = 0.05f;
            Transform t = transform;
            Gizmos.matrix = t.localToWorldMatrix;
            Color c = Color.yellow;

            if (_voxels != null)
            {
                Gizmos.color = c;

                foreach (Vector3 p in _voxels)
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
            {
                _voxelBounds = VoxelBounds();
            }

            Gizmos.color = Color.red;
            Gizmos.DrawSphere(_voxelBounds.center + centerOfMass, 0.2f);

            Gizmos.matrix = Matrix4x4.identity;

            if (_debugInfo == null) { return; }
            foreach (DebugDrawing debug in _debugInfo)
            {
                bool inWater = debug.Force.sqrMagnitude > 0f;
                Gizmos.color = inWater ? Color.red : Color.cyan;
                Gizmos.DrawCube(debug.Position, new Vector3(gizmoSize, gizmoSize, gizmoSize)); // drawCenter
                Vector3 water = debug.Position;
                water.y = debug.WaterHeight;
                Gizmos.DrawLine(debug.Position, water); // draw the water line
                Gizmos.DrawSphere(water, gizmoSize * (inWater ? 2f : 1f));
                if (_buoyancyType is BuoyancyType.Physical or BuoyancyType.PhysicalVoxel)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawRay(debug.Position, debug.Force / _rb.mass); // draw force
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