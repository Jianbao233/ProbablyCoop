using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>QA-only offscreen rendering of the actual scene and actual Canvas objects.
/// Standalone batch mode does not populate the screen backbuffer. No OS window/input API.
/// Overlay canvases are temporarily routed through a dedicated camera, then restored.</summary>
internal static class NativeOffscreenCapture
{
    internal static object Bounds(RectTransform root)
    {
        var corners = new Vector3[4]; root.GetWorldCorners(corners);
        var canvas = root.GetComponentInParent<Canvas>();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var points = corners.Select(p => RectTransformUtility.WorldToScreenPoint(camera, p)).ToArray();
        return new { x = points.Min(p => p.x), y = Screen.height - points.Max(p => p.y),
            w = points.Max(p => p.x) - points.Min(p => p.x), h = points.Max(p => p.y) - points.Min(p => p.y),
            screenW = Screen.width, screenH = Screen.height };
    }
    private sealed class CanvasState
    {
        internal Canvas Canvas;
        internal RenderMode Mode;
        internal Camera Camera;
        internal float Distance;
    }
    internal static void Capture(string name)
    {
        if ((!NativeArchitectureQa.Active || !NativeArchitectureQa.GraphicsRequested) &&
            (!NativeQaDisplay.IsIsolatedQa || SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)) return;
        using var timing=NativePerformance.Measure("qa.render",NativePerformance.FrameWork.QaRender);
        int width = Screen.width, height = Screen.height;
        var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        var canvasStates = new List<CanvasState>();
        var layers = new Dictionary<GameObject, int>();
        GameObject cameraObject = null; Texture2D image = null;
        try
        {
            RenderTexture.active = target;
            GL.Clear(true, true, Color.black);
            Canvas.ForceUpdateCanvases();
            var sceneCameras = Resources.FindObjectsOfTypeAll<Camera>()
                .Where(c => c.gameObject.scene.IsValid() && c.enabled && c.gameObject.activeInHierarchy)
                .OrderBy(c => c.depth).ToArray();
            foreach (var camera in sceneCameras)
            {
                var original = camera.targetTexture;
                try { camera.targetTexture = target; camera.Render(); }
                finally { camera.targetTexture = original; }
            }
            var overlay = Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(c => c.gameObject.scene.IsValid() && c.enabled && c.gameObject.activeInHierarchy &&
                    c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
            if (overlay.Length > 0)
            {
                cameraObject = new GameObject("ProbablyCoop offscreen QA camera") { hideFlags = HideFlags.HideAndDontSave };
                var uiCamera = cameraObject.AddComponent<Camera>();
                uiCamera.enabled = false; uiCamera.orthographic = true;
                uiCamera.orthographicSize = height / 2f; uiCamera.aspect = (float)width / height;
                uiCamera.nearClipPlane = 0.1f; uiCamera.farClipPlane = 200;
                uiCamera.transform.position = new Vector3(0, 0, -100);
                uiCamera.clearFlags = CameraClearFlags.Depth; uiCamera.cullingMask = 1 << 31;
                uiCamera.targetTexture = target;
                foreach (var canvas in overlay)
                {
                    canvasStates.Add(new CanvasState { Canvas = canvas, Mode = canvas.renderMode,
                        Camera = canvas.worldCamera, Distance = canvas.planeDistance });
                    foreach (var transform in canvas.GetComponentsInChildren<Transform>(true))
                    {
                        if (!layers.ContainsKey(transform.gameObject)) layers.Add(transform.gameObject, transform.gameObject.layer);
                        transform.gameObject.layer = 31;
                    }
                    canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = uiCamera; canvas.planeDistance = 100;
                }
                Canvas.ForceUpdateCanvases(); uiCamera.Render();
            }
            RenderTexture.active = target;
            image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0); image.Apply();
            var pixels = image.GetPixels32();
            int nonblack = 0;
            foreach (var pixel in pixels) if (pixel.r > 3 || pixel.g > 3 || pixel.b > 3) nonblack++;
            if (nonblack < pixels.Length / 20)
                throw new InvalidOperationException("Offscreen output is blank; cannot count as visual evidence");
            ModHelper.WriteModDataFileBytes("probably_coop", "architecture-" + name + ".png", image.EncodeToPNG());
            Debug.Log($"[probably_coop architecture] offscreen {name} {width}x{height} cameras={sceneCameras.Length} overlays={overlay.Length} nonblack={nonblack}");
        }
        finally
        {
            foreach (var state in canvasStates)
            {
                state.Canvas.renderMode = state.Mode; state.Canvas.worldCamera = state.Camera; state.Canvas.planeDistance = state.Distance;
            }
            foreach (var pair in layers) if (pair.Key != null) pair.Key.layer = pair.Value;
            Canvas.ForceUpdateCanvases();
            RenderTexture.active = previous;
            if (image != null) UnityEngine.Object.Destroy(image);
            if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject);
            RenderTexture.ReleaseTemporary(target);
        }
    }
}
