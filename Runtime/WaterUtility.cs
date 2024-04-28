//https://github.com/Unity-Technologies/boat-attack-water/compare/main...OndrejPetrzilka:boat-attack-water:main
using UnityEngine;

public static class WaterUtility
{
    public static bool CanRender(GameObject water, Camera camera)
    {
        if (camera.cameraType == CameraType.Preview ||
            camera.orthographic || camera.fieldOfView < 5 ||
            (camera.cullingMask & (1 << water.layer)) == 0)
        {
            return false;
        }

#if UNITY_EDITOR
        if (camera.cameraType == CameraType.SceneView)
        {
            return UnityEditor.SceneManagement.StageUtility.IsGameObjectRenderedByCamera(water, camera);
        }
#endif
        return true;
    }
}
