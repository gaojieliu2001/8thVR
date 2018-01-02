using UnityEngine;

public class XRVideoTextureController : MonoBehaviour {
  private XRController xr;

  #if !UNITY_EDITOR
  void Start() {
    xr = GameObject.FindWithTag("XRController").GetComponent<XRController>();

    // Set reality texture onto our material. Make sure it's unlit to avoid appearing washed out.
    // Note that this requires Unlit/Texture to be included in the unity project.
    Renderer r = GetComponent<Renderer>();
    r.material.shader = xr.GetVideoTextureShader();
    r.material.mainTexture = xr.GetRealityTexture();
    r.material.SetInt("_ScreenOrientation", (int) Screen.orientation);
  }
  #endif
}

