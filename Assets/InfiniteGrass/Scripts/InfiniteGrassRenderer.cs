using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Unity.Burst.Intrinsics.X86.Avx;

[ExecuteAlways]
public class InfiniteGrassRenderer : MonoBehaviour
{
    [HideInInspector] public static InfiniteGrassRenderer instance;//Global ref of the script

    [Header("Internal")]
    public Material grassMaterial;
    public ComputeBuffer argsBuffer;
    public ComputeBuffer tBuffer;//Just a temp buffer to preview the visible grass count

    [Header("Grass Properties")]
    public float spacing = 0.5f;//Spacing between blades, Please don't make it too low
    public float drawDistance = 300;
    public float subdivisionDistance = 100;//Distance where grass mesh subdivisions fade out
    public float fullDensityDistance = 30;//Distance around the camera kept at full density
    [Tooltip("Controls how quickly grass fades with distance (higher is steeper)")]
    public float densityFalloffExponent = 4f;
    [Tooltip("Extra height applied near the Subdivision Distance")] public float subdivisionHeightBoost = 0f;
    public int grassMeshSubdivision = 5;//How many sections you will have in your grass blade mesh, 0 will give a triangle, having more sections will make the wind animation and the curvature looks better
    public float textureUpdateThreshold = 10.0f;//The distance that the camera should move before we update the "Data Textures"

    [Header("Max Buffer Count (Millions)")]
    public float maxBufferCount = 2;//The number we gonna use to initialize the positions buffer
    //Don't make it too high cause that gonna impact performance, usually 2 - 3 should be enough unless you are using a crazy spacing
    //Also don't make it too low cause it's gonna negativly impact the performance

    [Header("Debug (Enabling this will make the performance drop a lot)")]
    public bool previewVisibleGrassCount = false;

    private Mesh cachedGrassMesh;

    private void OnEnable()
    {
        instance = this;
    }

    private void OnDisable()
    {
        instance = null;

        argsBuffer?.Release();
        tBuffer?.Release();
    }

    void LateUpdate()
    {
        argsBuffer?.Release();
        tBuffer?.Release();

        if (spacing == 0 || grassMaterial == null) return;

        Bounds cameraBounds = CalculateCameraBounds(Camera.main);
        Vector2 centerPos = new Vector2(Mathf.Floor(Camera.main.transform.position.x / textureUpdateThreshold) * textureUpdateThreshold, Mathf.Floor(Camera.main.transform.position.z / textureUpdateThreshold) * textureUpdateThreshold);
        
        //Args Buffer ---------------------------------------------------------------------------------
        argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        tBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);

        uint[] args = new uint[5];
        args[0] = (uint)GetGrassMeshCache().GetIndexCount(0);
        args[1] = (uint)(maxBufferCount * 1000000);
        args[2] = (uint)GetGrassMeshCache().GetIndexStart(0);
        args[3] = (uint)GetGrassMeshCache().GetBaseVertex(0);
        args[4] = 0;
        argsBuffer.SetData(args);

        //Material Setup ------------------------------------------------------------
        grassMaterial.SetVector("_CenterPos", centerPos);
        grassMaterial.SetFloat("_DrawDistance", drawDistance);
        grassMaterial.SetFloat("_SubdivisionDistance", subdivisionDistance);
        grassMaterial.SetFloat("_SubdivisionHeightBoost", subdivisionHeightBoost);
        grassMaterial.SetFloat("_TextureUpdateThreshold", textureUpdateThreshold);
        grassMaterial.SetFloat("_MaxSubdivision", grassMeshSubdivision);

        //Big Draw Call -------------------------------------------------------------
        Graphics.DrawMeshInstancedIndirect(GetGrassMeshCache(), 0, grassMaterial, cameraBounds, argsBuffer);
    }

    private void OnGUI()
    {
        if (previewVisibleGrassCount)
        {
            GUI.contentColor = Color.black;
            GUIStyle style = new GUIStyle();
            style.fontSize = 25;

            uint[] count = new uint[1];
            tBuffer.GetData(count);//Reading back data from GPU

            //Recalculating the GridSize used for dispatching
            Bounds cameraBounds = CalculateCameraBounds(Camera.main);
            Vector2Int gridSize = new Vector2Int(Mathf.CeilToInt(cameraBounds.size.x / spacing), Mathf.CeilToInt(cameraBounds.size.z / spacing));

            GUI.Label(new Rect(50, 50, 400, 200), "Dispatch Size : " + gridSize.x + "x" + gridSize.y + " = " + (gridSize.x * gridSize.y), style);
            GUI.Label(new Rect(50, 80, 400, 200), "Visible Grass Count : " + count[0], style);
        }
    }

    int oldSubdivision = -1;
    public Mesh GetGrassMeshCache() //Code to generate the grass blade mesh based on the subdivision value
    {
        if (!cachedGrassMesh || oldSubdivision != grassMeshSubdivision)//Dont update unless its necessary
        {
            cachedGrassMesh = new Mesh();

            Vector3[] vertices = new Vector3[3 + 4 * grassMeshSubdivision];//Total number of vertices
            int[] triangles = new int[(1 + 2 * grassMeshSubdivision) * 3];//(Total number of faces) * 3

            for (int i = 0; i < grassMeshSubdivision; i++)
            {
                float y1 = (float)i / (grassMeshSubdivision + 1);
                float y2 = (float)(i + 1) / (grassMeshSubdivision + 1);

                Vector3 bottomLeft = new Vector3(-0.25f, y1);
                Vector3 bottomRight = new Vector3(0.25f, y1);
                Vector3 topLeft = new Vector3(-0.25f, y2);
                Vector3 topRight = new Vector3(0.25f, y2);

                int bottomLeftIndex = i * 4;
                int bottomRightIndex = i * 4 + 1;
                int topLeftIndex = i * 4 + 2;
                int topRightIndex = i * 4 + 3;

                vertices[bottomLeftIndex] = bottomLeft;
                vertices[bottomRightIndex] = bottomRight;
                vertices[topLeftIndex] = topLeft;
                vertices[topRightIndex] = topRight;

                //First Face
                triangles[i * 6] = bottomLeftIndex;
                triangles[i * 6 + 1] = topRightIndex;
                triangles[i * 6 + 2] = bottomRightIndex;
                //Second Face
                triangles[i * 6 + 3] = bottomLeftIndex;
                triangles[i * 6 + 4] = topLeftIndex;
                triangles[i * 6 + 5] = topRightIndex;
            }

            //Finally the last triangle on top
            vertices[grassMeshSubdivision * 4] = new Vector3(-0.25f, (float)grassMeshSubdivision / (grassMeshSubdivision + 1));
            vertices[grassMeshSubdivision * 4 + 1] = new Vector3(0, 1);
            vertices[grassMeshSubdivision * 4 + 2] = new Vector3(0.25f, (float)grassMeshSubdivision / (grassMeshSubdivision + 1));

            triangles[grassMeshSubdivision * 6] = grassMeshSubdivision * 4;
            triangles[grassMeshSubdivision * 6 + 1] = grassMeshSubdivision * 4 + 1;
            triangles[grassMeshSubdivision * 6 + 2] = grassMeshSubdivision * 4 + 2;

            cachedGrassMesh.SetVertices(vertices);
            cachedGrassMesh.SetTriangles(triangles, 0);

            oldSubdivision = grassMeshSubdivision;
        }
        
        return cachedGrassMesh;
    }
    Bounds CalculateCameraBounds(Camera camera)
    {
        Vector3 ntopLeft = camera.ViewportToWorldPoint(new Vector3(0, 1, camera.nearClipPlane));
        Vector3 ntopRight = camera.ViewportToWorldPoint(new Vector3(1, 1, camera.nearClipPlane));
        Vector3 nbottomLeft = camera.ViewportToWorldPoint(new Vector3(0, 0, camera.nearClipPlane));
        Vector3 nbottomRight = camera.ViewportToWorldPoint(new Vector3(1, 0, camera.nearClipPlane));

        Vector3 ftopLeft = camera.ViewportToWorldPoint(new Vector3(0, 1, drawDistance));
        Vector3 ftopRight = camera.ViewportToWorldPoint(new Vector3(1, 1, drawDistance));
        Vector3 fbottomLeft = camera.ViewportToWorldPoint(new Vector3(0, 0, drawDistance));
        Vector3 fbottomRight = camera.ViewportToWorldPoint(new Vector3(1, 0, drawDistance));

        float[] xValues = new float[] { ftopLeft.x, ftopRight.x, ntopLeft.x, ntopRight.x, fbottomLeft.x, fbottomRight.x, nbottomLeft.x, nbottomRight.x };
        float startX = xValues.Max();
        float endX = xValues.Min();

        float[] yValues = new float[] { ftopLeft.y, ftopRight.y, ntopLeft.y, ntopRight.y, fbottomLeft.y, fbottomRight.y, nbottomLeft.y, nbottomRight.y };
        float startY = yValues.Max();
        float endY = yValues.Min();

        float[] zValues = new float[] { ftopLeft.z, ftopRight.z, ntopLeft.z, ntopRight.z, fbottomLeft.z, fbottomRight.z, nbottomLeft.z, nbottomRight.z };
        float startZ = zValues.Max();
        float endZ = zValues.Min();

        Vector3 center = new Vector3((startX + endX) / 2, (startY + endY) / 2, (startZ + endZ) / 2);
        Vector3 size = new Vector3(Mathf.Abs(startX - endX), Mathf.Abs(startY - endY), Mathf.Abs(startZ - endZ));

        Bounds bounds = new Bounds(center, size);
        bounds.Expand(1);
        return bounds;
    }

}
