using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;

namespace WaterSystem
{
    [DisallowMultipleComponent, ExecuteAlways]
    public class WaterEvent : MonoBehaviour
    {
        public List<WaterCollisionState> collisionEvents;
        public List<WaterSubmergedState> submergedEvents;
        [ReadOnly, HideInInspector] public WaterEventType currentState;
        private WaterEventType _previousState;

        private NativeArray<float3> _samplePosition;
        private Data.WaveOutputData[] _waveResults = new Data.WaveOutputData[1];
        private bool _prevSubmerged;
        private bool _submerged;

        private void OnEnable()
        {
            UpdateSamplePoint();
        }

        private void OnDisable()
        {
            GerstnerWavesJobs.RemoveSamplePoints(gameObject.GetInstanceID());
            Cleanup();
        }

        private void Update()
        {
            if (transform.hasChanged)
                UpdateSamplePoint();

            GerstnerWavesJobs.UpdateSamplePoints(ref _samplePosition, gameObject.GetInstanceID());
            GerstnerWavesJobs.GetData(gameObject.GetInstanceID(), ref _waveResults);

            CheckState();

            if (!Application.isPlaying)
                return;

            for (int i = 0, collisionEventsCount = collisionEvents.Count; i < collisionEventsCount; i++)
            {
                if (_previousState != currentState)
                {
                    collisionEvents[i].Invoke(currentState);
                }
            }

            for (int i = 0, submergedEventsCount = submergedEvents.Count; i < submergedEventsCount; i++)
            {
                if (_prevSubmerged != _submerged)
                {
                    submergedEvents[i].Invoke(currentState == WaterEventType.Submerged);
                }
            }
        }

        private void UpdateSamplePoint()
        {
            if (!_samplePosition.IsCreated)
            {
                _samplePosition = new NativeArray<float3>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            _samplePosition[0] = transform.position;
        }

        private void CheckState()
        {
            _previousState = currentState;
            _prevSubmerged = _submerged;
            
            var facing = math.dot(_waveResults[0].Normal, math.normalize((float3)transform.position - _waveResults[0].Position));
            _submerged = facing < 0.0f;

            if (_submerged)
            {
                currentState = _prevSubmerged ? WaterEventType.Submerged : WaterEventType.Entered;
            }
            else
            {
                currentState = _prevSubmerged ? WaterEventType.Exited : WaterEventType.None;
            }
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void Cleanup()
        {
            if (_samplePosition.IsCreated)
                _samplePosition.Dispose();
        }

        private void OnDrawGizmosSelected()
        {
#if UNITY_EDITOR
            Color color;
            switch (currentState)
            {
                case WaterEventType.Submerged:
                    color = Color.blue;
                    break;
                case WaterEventType.Entered:
                    color = Color.green;
                    break;
                case WaterEventType.Exited:
                    color = Color.red;
                    break;
                default:
                    color = Color.white;
                    break;
            }

            Handles.color = color;
            var normal = _waveResults[0].Position + _waveResults[0].Normal;

            Handles.DrawWireDisc(_waveResults[0].Position.xyz, _waveResults[0].Normal.xyz, 0.5f, 1f);
            Handles.DrawLine(_waveResults[0].Position.xyz, normal.xyz, 1f);
#endif
        }
    }

    [Serializable] public class WaterCollisionState : UnityEvent<WaterEventType> { }
    [Serializable] public class WaterSubmergedState : UnityEvent<bool> { }

    public enum WaterEventType
    {
        None,
        Submerged,
        Entered,
        Exited
    }
}
