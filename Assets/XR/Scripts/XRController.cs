using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using XRInternal;

public class XRController : MonoBehaviour {
  private XRNativeBridge bridge;
  private bool running;
  private long lastRealityMicros;
  private long updateNumber;
  private Texture2D realityTexture = null;
  private XRResponseRef currentXRResponse = null;
  private XREnvironmentRef xrEnvironment = null;
  private long currentRealityUpdateNumber;
  private Camera cam = null;
  private Vector3 origin = new Vector3(0, 0, 0);
  private float scale = 1.0f;
  private bool explicitlyPaused = false;


  public bool enableLighting = true;
  public bool enableCamera = true;
  public bool enableSurfaces = true;

  public Matrix4x4 GetCameraIntrinsics() {
    XRResponseRef r = GetCurrentReality();
    Matrix4x4 np = Matrix4x4.zero;
    if (cam != null) {
      np = cam.projectionMatrix;
    }

    if (r == null || r.ptr.cameraIntrinsicMatrix44f == null) {
      return np;
    }

    float[] intrinsics = r.ptr.cameraIntrinsicMatrix44f;

    for (int i = 0; i < 4; ++i) {
      for (int j = 0; j < 4; ++j) {
        np[i, j] = intrinsics[j * 4 + i];
      }
    }

    return np;
  }

  public Vector3 GetCameraPosition() {
    XRResponseRef r = GetCurrentReality();
    return RecenterAndScale(
      new Vector3(
        r.ptr.cameraExtrinsicPositionX,
        r.ptr.cameraExtrinsicPositionY,
        r.ptr.cameraExtrinsicPositionZ));
  }

  public Quaternion GetCameraRotation() {
    XRResponseRef r = GetCurrentReality();
    return new Quaternion(
      r.ptr.cameraExtrinsicRotationX,
      r.ptr.cameraExtrinsicRotationY,
      r.ptr.cameraExtrinsicRotationZ,
      r.ptr.cameraExtrinsicRotationW);
  }

  public void UpdateCameraProjectionMatrix(Camera cam, Vector3 origin, float scale) {
    if (xrEnvironment.ptr.capabilityPositionTracking !=
      XREnvironmentConstants.POSITION_TRACKING_ROTATION_AND_POSITION) {
      float scaleAdjustment = origin.y;
      this.scale = scaleAdjustment;
    } else {
       this.scale = scale;
    }
    this.origin = origin;
    this.cam = cam;
    ConfigureXR();
  }

  public float GetLightExposure() {
    XRResponseRef r = GetCurrentReality();
    return r.ptr.lightingGlobalExposure;
  }

  public long GetActiveSurfaceId() {
    XRResponseRef r = GetCurrentReality();
    return r.ptr.surfacesActiveSurfaceIdTimeMicros;
  }

  public Mesh GetActiveSurfaceMesh() {
    Dictionary<long, Mesh> meshes = GetSurfaces();
    if (!meshes.Any()) {
      return null;
    }

    // Get the active surface.
    long activeSurface = GetActiveSurfaceId();

    // Don't update anything if there is no active surface or it is invalid.
    if (activeSurface == 0
      || activeSurface == Int64.MinValue
      || !meshes.ContainsKey(activeSurface)) {
      return null;
    }
    return meshes[activeSurface];
  }

  public Mesh GetSurfaceWithId(long id) {
    Dictionary<long, Mesh> meshes = GetSurfaces();
    if (!meshes.Any()) {
      return null;
    }
    return meshes[id];
  }

  public float GetRealityTextureAspectRatio() {
      return (float)xrEnvironment.ptr.realityImageWidth
        / (float)xrEnvironment.ptr.realityImageHeight;
  }

  public Texture2D GetRealityTexture() {
    if (realityTexture == null) {
      // Create a texture
      if (xrEnvironment.ptr.realityImageTexture != IntPtr.Zero) {
        realityTexture = Texture2D.CreateExternalTexture(
          xrEnvironment.ptr.realityImageWidth,
          xrEnvironment.ptr.realityImageHeight,
          TextureFormat.RGBA32,
          false,
          false,
          xrEnvironment.ptr.realityImageTexture);
      } else {
      realityTexture = new Texture2D(
        xrEnvironment.ptr.realityImageWidth,
        xrEnvironment.ptr.realityImageHeight,
        TextureFormat.RGBA32,
        false);
    }
      // Set point filtering just so we can see the pixels clearly
      realityTexture.filterMode = FilterMode.Point;
      // Call Apply() so it's actually uploaded to the GPU
      realityTexture.Apply();

      if (xrEnvironment.ptr.realityImageTexture == IntPtr.Zero) {
        // Pass texture pointer to the plugin only if it doesn't manage one itself.
        bridge.SetManagedCameraTexture(
            realityTexture.GetNativeTexturePtr(), realityTexture.width, realityTexture.height);
      }
    }

    return realityTexture;
  }

  public Shader GetVideoShader() {
    switch(xrEnvironment.ptr.realityImageShader) {
      case XREnvironmentConstants.IMAGE_SHADER_ARCORE:
        return Shader.Find("Unlit/ARCoreCameraShader");
      case XREnvironmentConstants.IMAGE_SHADER_DEFAULT:
      default:
        return Shader.Find("Unlit/XRCameraShader");
    }
  }

  public Shader GetVideoTextureShader() {
    switch(xrEnvironment.ptr.realityImageShader) {
      case XREnvironmentConstants.IMAGE_SHADER_ARCORE:
        return Shader.Find("Unlit/ARCoreTextureShader");
      case XREnvironmentConstants.IMAGE_SHADER_DEFAULT:
      default:
        return Shader.Find("Unlit/Texture");
    }
  }

  // NOTE: This function is temporary and will go away once native surface detection
  // is supported on older phones.
  public bool InternalIsUsingFixedSurfaces() {
    return xrEnvironment.ptr.capabilitySurfaceEstimation
      == XREnvironmentConstants.SURFACE_ESTIMATION_FIXED_SURFACES;
  }

  public void Recenter() {
    if (InternalIsUsingFixedSurfaces()) {
      bridge.Recenter();
    }
  }

  // Awake is called first at app startup.
  void Awake() {
    running = false;
    bridge = new XRNativeBridge();
    bridge.Create();
    xrEnvironment = bridge.GetXREnvironment();
    Application.targetFrameRate = 60;
  }

  // Start is called after OnEnable, but it is only called once for a given script, while OnEnable
  // is called after every call to OnDisable.
  IEnumerator Start() {
    lastRealityMicros = 0;
    updateNumber = 0;
    currentRealityUpdateNumber = -1;
    yield return StartCoroutine("CallPluginAtEndOfFrames");
  }

  // OnEnable is called after every OnDisable. It is called before Start the first time Start is
  // called, but Start is only called once.
  void OnEnable() {
    ConfigureXR();
  }

  void Update() {
    if (!explicitlyPaused) {
      RunIfPaused();
    }

    updateNumber++;

    XRResponseRef r = GetCurrentReality();
    if (lastRealityMicros >= r.ptr.eventIdTimeMicros) {
      return;
    }
    lastRealityMicros = r.ptr.eventIdTimeMicros;
  }

  void OnApplicationPause(bool isPaused) {
    explicitlyPaused = isPaused;
    if (!isPaused) {
      RunIfPaused();
      return;
    }

    if (!running) {
      return;
    }
    running = false;
    bridge.Pause();
  }

  void OnDisable() {
    if (running) {
      OnApplicationPause(true);
    }
  }

  void OnDestroy() {
    bridge.Destroy();
  }

  void OnApplicationQuit() {
    bridge.Destroy();
  }

  private void RunIfPaused() {
    if (running) {
      return;
    }
    running = true;
    bridge.Resume();
  }

  private void ConfigureXR() {
    if (bridge == null) {
      return;
    }

    XRConfigurationRef config = bridge.GetMutableXRConfiguration();
    config.ptr.maskLighting = enableLighting;
    config.ptr.maskCamera = enableCamera;
    config.ptr.maskSurfaces = enableSurfaces;

    if (cam != null) {
      config.ptr.graphicsIntrinsicsTextureWidth = (int)cam.pixelRect.width;
      config.ptr.graphicsIntrinsicsTextureHeight = (int)cam.pixelRect.height;
      config.ptr.graphicsIntrinsicsNearClip = cam.nearClipPlane;
      config.ptr.graphicsIntrinsicsFarClip = cam.farClipPlane;
      config.ptr.graphicsIntrinsicsDigitalZoomHorizontal = 1.0f;
      config.ptr.graphicsIntrinsicsDigitalZoomVertical = 1.0f;
    }

    bridge.CommitConfiguration();
  }

  private XRResponseRef GetCurrentReality() {
    if (currentXRResponse == null || currentRealityUpdateNumber < updateNumber) {
      currentRealityUpdateNumber = updateNumber;
      currentXRResponse = bridge.GetCurrentRealityXR();
    }
    return currentXRResponse;
  }

  private Vector3 RecenterAndScale(Vector3 p) {
    Vector3 o = origin;
    float s = scale;
    return new Vector3(o.x + p.x * s, o.y + p.y * s, o.z + p.z * s);
  }

  private Dictionary<long, Mesh> GetSurfaces() {
    Dictionary<long, Mesh> surfaces = new Dictionary<long, Mesh>();

    XRResponseRef r = GetCurrentReality();

    for (int i = 0; i < r.ptr.surfacesSetSurfacesCount; ++i) {
      // Extract basic info about this mesh.
      long id = r.ptr.surfacesSetSurfacesIdTimeMicros[i];

      int beginFaceIndex = r.ptr.surfacesSetSurfacesFacesBeginIndex[i];
      int endFaceIndex = r.ptr.surfacesSetSurfacesFacesEndIndex[i];
      int beginVerticesIndex = r.ptr.surfacesSetSurfacesVerticesBeginIndex[i];
      int endVerticesIndex = r.ptr.surfacesSetSurfacesVerticesEndIndex[i];

      // Build the vertex and normal arrays.
      int nVertices = endVerticesIndex - beginVerticesIndex;
      Vector3[] vertices = new Vector3[nVertices];
      Vector3[] normals = new Vector3[nVertices];
      Vector2[] uvs = new Vector2[nVertices];
      for (int j = 0; j < nVertices; ++j) {
        int vertexIndex = (beginVerticesIndex + j) * 3;
        vertices[j] = RecenterAndScale(
          new Vector3(
            r.ptr.surfacesSetVertices[vertexIndex],
            r.ptr.surfacesSetVertices[vertexIndex + 1],
            r.ptr.surfacesSetVertices[vertexIndex + 2]));
        normals[j] = Vector3.up;
        float u = vertices[j][0];
        float v = vertices[j][2];
        uvs[j] = new Vector2(u, v);
      }

      // We can just directly copy over the triangles (they are stored in consecutive sets of three
      // vertex indices) as long as we offset the vertex indices.
      int nFaces = endFaceIndex - beginFaceIndex;
      int[] triangles = new int[nFaces * 3];
      for (int j = 0; j < nFaces; ++j) {
        int v0 = r.ptr.surfacesSetFaces[3 * (j + beginFaceIndex)] - beginVerticesIndex;
        int v1 = r.ptr.surfacesSetFaces[3 * (j + beginFaceIndex) + 1] - beginVerticesIndex;
        int v2 = r.ptr.surfacesSetFaces[3 * (j + beginFaceIndex) + 2] - beginVerticesIndex;
        triangles[3 * j] = v0;
        triangles[3 * j + 1] = v1;
        triangles[3 * j + 2] = v2;
      }

      Mesh mesh = new Mesh();
      mesh.vertices = vertices;
      mesh.normals = normals;
      mesh.uv = uvs;
      mesh.triangles = triangles;

      surfaces.Add(id, mesh);
    }

    return surfaces;
  }

  private IEnumerator CallPluginAtEndOfFrames() {
    IntPtr renderEventFunc = bridge.GetRenderEventFunc();
    while (renderEventFunc != IntPtr.Zero) {
      // Wait until all frame rendering is done.
      yield return new WaitForEndOfFrame();


      if (realityTexture != null && xrEnvironment.ptr.realityImageTexture == IntPtr.Zero) {
        // Issue a plugin event such that it is called from the Unity render thread.
        GL.IssuePluginEvent(renderEventFunc, 1 /* eventID */);
      }
    }
  }

}
