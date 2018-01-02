using UnityEngine;
using System;
using System.Linq;
using System.Collections.Generic;

public class XRSurfaceController : MonoBehaviour {

  private const int STARTING_DISTANCE = 10000;
  private const float GROUND_DISTANCE_GUESS = 1.2f; // ~4ft
  private const float MAX_GROUND_HEIGHT = 0.3f;

  // If true, XRSurfaceController will update the rendered mesh and the collider mesh of the surface
  // so that it matches the detected surface. This allows for interactions like shadows that clip
  // to surface boundaries, and objects that can fall off surfaces.
  public bool deformToSurface = false;

  // If true, game object will appear as placed in the scene prior to surface detection.
  // If false, game object will be moved out of view until surface is detected.
  // When surface is detected, object will be moved to surface position.
  public bool displayImmediately = false;

  // If true, only place on "ground" surfaces (< MAX_GROUND_HEIGHT)
  // If false, use any deteced surface.
  public bool groundOnly = false;

  // If true, place on first detected surface and don't move
  // If false, move game object to active surface
  public bool lockToFirstSurface = true;

  #if !UNITY_EDITOR
  private XRController xr;
  private bool surfaceFound = false;
  private float groundHeightGuess;

  private MeshFilter meshFilter = null;
  private MeshCollider meshCollider = null;

  private long surfaceId = Int64.MinValue;
  Dictionary<long, Vector3> centerMap;

  void Start() {
    centerMap = new Dictionary<long, Vector3>();

    xr = GameObject.FindWithTag("XRController").GetComponent<XRController>();
    // Add MeshFilter and MeshCollider if not already added.
    meshFilter = gameObject.GetComponent<MeshFilter>();
    if (deformToSurface && meshFilter == null) {
      meshFilter = gameObject.AddComponent<MeshFilter>();
    }

    meshCollider = gameObject.GetComponent<MeshCollider>();
    if (deformToSurface && meshCollider == null) {
      meshCollider = gameObject.AddComponent<MeshCollider>();
      meshCollider.sharedMesh = meshFilter.mesh;
    }

    if (groundOnly) {
      // Start by moving object a reasonable guess for the ground height.
      groundHeightGuess = Camera.main.transform.position.y - GROUND_DISTANCE_GUESS;
      SetHeight(groundHeightGuess);
    }

    if (!displayImmediately && !xr.InternalIsUsingFixedSurfaces()) {
      // Move surface very far in the distance until there is a detected surface.
      transform.position = new Vector3(STARTING_DISTANCE, transform.position.y, STARTING_DISTANCE);
    }

    if (xr.InternalIsUsingFixedSurfaces() && lockToFirstSurface) {
      surfaceFound = true;
    }
  }

  private void SetHeight(float h) {
    transform.position = new Vector3 (transform.position.x, h, transform.position.z);
  }

  private Vector3 GetVertexCenter(Mesh mesh) {
    double x = 0.0;
    double y = 0.0;
    double z = 0.0;

    foreach (Vector3 vertex in mesh.vertices) {
      x += vertex.x;
      y += vertex.y;
      z += vertex.z;
    }
    double il = 1.0 / mesh.vertices.Length;

    return new Vector3((float)(x * il), (float)(y * il), (float)(z * il));
  }

  private void UpdateSurface(long id, Mesh mesh) {

    // If looking at a different surface than before
    if (id != surfaceId) {
      
      Vector3 vertexCenter = GetVertexCenter(mesh);

      if (groundOnly) {
        float y = vertexCenter.y;

        // Check to see if surface can be considered ground
        if (y < (groundHeightGuess + MAX_GROUND_HEIGHT)) {
          if (!centerMap.ContainsKey(id)) {
            centerMap.Add(id, vertexCenter);
          }
          if (!displayImmediately && !surfaceFound) {
            transform.position = centerMap[id];
          }

          if (lockToFirstSurface) {
            // Don't move, just lower to the ground surface
            SetHeight(y);
          } else {
            transform.position = centerMap[id];
          }
          surfaceFound = true;
        }

      } else {

        if (!centerMap.ContainsKey(id)) {
          centerMap.Add(id, vertexCenter);
        }

        transform.position = centerMap[id];
        surfaceId = id;
        surfaceFound = true;
      }
    }
  }

  void DeformMesh(Mesh mesh) {
    // Set the mesh vertices relative to the transform center.
    Mesh relMesh = new Mesh();
    Vector3[] relVertices = new Vector3[mesh.vertices.Length];
    Vector3 anchor = transform.position;
    for (int i = 0; i < mesh.vertices.Length; ++i) {
      relVertices[i] = new Vector3(
        mesh.vertices[i].x - anchor.x,
        0.0f,
        mesh.vertices[i].z - anchor.z);
    }
    relMesh.vertices = relVertices;
    relMesh.normals = mesh.normals;
    relMesh.uv = mesh.uv;
    relMesh.triangles = mesh.triangles;

    meshFilter.mesh = relMesh;
    meshCollider.sharedMesh = relMesh;
  }

  void Update() {
    
    Mesh mesh = !lockToFirstSurface || surfaceId == Int64.MinValue ? xr.GetActiveSurfaceMesh() : xr.GetSurfaceWithId(surfaceId);
    // If no mesh, reset the id to default and don't change anything.
    if (mesh == null) {
      surfaceId = Int64.MinValue;
      return;
    }

    if (!lockToFirstSurface || !surfaceFound) {
      UpdateSurface(xr.GetActiveSurfaceId(), mesh);
    }

    if (deformToSurface && !xr.InternalIsUsingFixedSurfaces()) {
      DeformMesh(mesh);
    }
  }
  #endif
}
