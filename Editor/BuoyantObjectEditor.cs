using System;
using UnityEditor;
using UnityEngine;

namespace WaterSystem.Physics
{
    [CustomEditor(typeof(BuoyantObject))]
    public class BuoyantObjectEditor : Editor
    {
        private BuoyantObject obj;
        [SerializeField]
        private bool _heightsDebugBool;
        [SerializeField]
        private bool _generalSettingsBool;

        private void OnEnable()
        {
            obj = serializedObject.targetObject as BuoyantObject;
        }

        public override void OnInspectorGUI()
        {
            _generalSettingsBool = EditorGUILayout.Foldout(_generalSettingsBool, "General Settings");
            if (_generalSettingsBool)
            {
                base.OnInspectorGUI();
            }

            if (EditorGUILayout.BeginFoldoutHeaderGroup(_heightsDebugBool, "Height Debug Values"))
            {
                if (obj.WaveResults != null)
                {
                    for (var i = 0; i < obj.WaveResults.Length; i++)
                    {
                        var h = obj.WaveResults[i];
                        EditorGUILayout.LabelField($"{i})Wave(heights):", $"X:{h.Position.x:00.00} Y:{h.Position.y:00.00} Z:{h.Position.z:00.00}");
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Height debug info only available in playmode.", MessageType.Info);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }
    }
}
