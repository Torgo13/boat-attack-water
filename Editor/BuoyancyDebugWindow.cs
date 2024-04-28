using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using WaterSystem;

public class BuoyancyDebugWindow : EditorWindow
{
    private Vector2 _scrollPosition;
    // Add menu named "My Window" to the Window menu
    [MenuItem("Window/URP Water System/Buoyancy Debug")]
    private static void Init()
    {
        // Get existing open window or if none, make a new one:
        BuoyancyDebugWindow window = (BuoyancyDebugWindow)GetWindow(typeof(BuoyancyDebugWindow));
        window.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("URP Water System : Buoyancy Debug", EditorStyles.largeLabel);

        EditorGUILayout.HelpBox(
            $"Total Objects:{GerstnerWavesJobs.Registry.Count} Sample Point Count:{GerstnerWavesJobs._positionCount}",
            MessageType.None);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
        int count = 0;
        foreach (KeyValuePair<int, int2> registryEntry in GerstnerWavesJobs.Registry)
        {
            Object obj = EditorUtility.InstanceIDToObject(registryEntry.Key);
            Rect box = EditorGUILayout.BeginHorizontal();
            if (count % 2 == 0)
            {
                GUI.Box(box, GUIContent.none);
            }

            EditorGUILayout.LabelField($"{obj.name}", $"GUID:{registryEntry.Key}");
            EditorGUILayout.LabelField($"indicies:{registryEntry.Value.x}-{registryEntry.Value.y}",
                $"size:{registryEntry.Value.y - registryEntry.Value.x}");
            if (GUILayout.Button("Ping Object"))
            {
                EditorGUIUtility.PingObject(registryEntry.Key);
            }
            count++;
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();
    }
}