using System.Collections;
using System.Collections.Generic;
using static System.Runtime.InteropServices.Marshal;
using UnityEngine;

public class ModelGrass : MonoBehaviour {
    public int fieldSize = 100;
    public int chunkDensity = 1;
    public int numChunks = 1;
    public float displacementStrength = 200.0f;
    public Material grassMaterial;
    public Mesh grassMesh;
    public Texture heightMap;

    [Range(0, 1000.0f)]
    public float distanceCutoff = 1000.0f;

    [Header("Wind")]
    public float windSpeed = 1.0f;
    public float frequency = 1.0f;
    public float windStrength = 1.0f;

    private ComputeShader initializeGrassShader, generateWindShader, cullGrassShader;
    private ComputeBuffer voteBuffer, scanBuffer, groupSumArrayBuffer, scannedGroupSumBuffer;

    private RenderTexture wind;

    private int numInstancesPerChunk, chunkDimension, numThreadGroups, numVoteThreadGroups, numGroupScanThreadGroups, numWindThreadGroups, numGrassInitThreadGroups;

    GrassChunk[] chunks;
    uint[] args;

    Bounds fieldBounds;

    private struct GrassData {
        public Vector4 position;
        public Vector2 uv;
        public float displacement;
    }

    private struct GrassChunk {
        public ComputeBuffer argsBuffer;
        public ComputeBuffer positionsBuffer;
        public ComputeBuffer culledPositionsBuffer;
        public Bounds bounds;
        public Material material;
        private bool isDisabled;
        public ModelGrass model;

        public bool IsDisabled
        {
            set
            {
                if (value == isDisabled) return;
                //if (value)
                //{
                //    FreeChunk();
                //}
                //else
                //{
                //    FillChunk();
                //}
                isDisabled = value;
            }
            get
            {
                return isDisabled;
            }
        }

        public void FillChunk()
        {
            argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);

            argsBuffer.SetData(model.args);

            positionsBuffer = new ComputeBuffer(model.numInstancesPerChunk, SizeOf(typeof(GrassData)));
            culledPositionsBuffer = new ComputeBuffer(model.numInstancesPerChunk, SizeOf(typeof(GrassData)));
        }

        public void FreeChunk()
        {
            positionsBuffer.Release();
            positionsBuffer = null;
            culledPositionsBuffer.Release();
            culledPositionsBuffer = null;
            argsBuffer.Release();
            argsBuffer = null;
        }
    }


    void OnEnable() {
        numInstancesPerChunk = Mathf.CeilToInt(fieldSize / numChunks) * chunkDensity;
        chunkDimension = numInstancesPerChunk;
        numInstancesPerChunk *= numInstancesPerChunk;
        
        numThreadGroups = Mathf.CeilToInt(numInstancesPerChunk / 128.0f);
        if (numThreadGroups > 128) {
            int powerOfTwo = 128;
            while (powerOfTwo < numThreadGroups)
                powerOfTwo *= 2;
            
            numThreadGroups = powerOfTwo;
        } else {
            while (128 % numThreadGroups != 0)
                numThreadGroups++;
        }
        numVoteThreadGroups = Mathf.CeilToInt(numInstancesPerChunk / 128.0f);
        numGroupScanThreadGroups = Mathf.CeilToInt(numInstancesPerChunk / 1024.0f);

        initializeGrassShader = Resources.Load<ComputeShader>("GrassChunkPoint");
        generateWindShader = Resources.Load<ComputeShader>("WindNoise");
        cullGrassShader = Resources.Load<ComputeShader>("CullGrass");

        voteBuffer = new ComputeBuffer(numInstancesPerChunk, 4);
        scanBuffer = new ComputeBuffer(numInstancesPerChunk, 4);
        groupSumArrayBuffer = new ComputeBuffer(numThreadGroups, 4);
        scannedGroupSumBuffer = new ComputeBuffer(numThreadGroups, 4);

        initializeGrassShader.SetInt("_Dimension", fieldSize);
        initializeGrassShader.SetInt("_ChunkDimension", chunkDimension);
        initializeGrassShader.SetInt("_Scale", chunkDensity);
        initializeGrassShader.SetInt("_NumChunks", numChunks);
        initializeGrassShader.SetTexture(0, "_HeightMap", heightMap);
        initializeGrassShader.SetFloat("_DisplacementStrength", displacementStrength);

        wind = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        wind.enableRandomWrite = true;
        wind.Create();
        numWindThreadGroups = Mathf.CeilToInt(wind.height / 8.0f);

        args = new uint[5] { 0, 0, 0, 0, 0 };
        args[0] = (uint)grassMesh.GetIndexCount(0);
        args[1] = (uint)0;
        args[2] = (uint)grassMesh.GetIndexStart(0);
        args[3] = (uint)grassMesh.GetBaseVertex(0);

        initializeChunks();

        fieldBounds = new Bounds(Vector3.zero, new Vector3(-fieldSize, displacementStrength * 2, fieldSize));
    }

    void initializeChunks() {
        chunks = new GrassChunk[numChunks * numChunks];

        for (int x = 0; x < numChunks; ++x) {
            for (int y = 0; y < numChunks; ++y) {
                chunks[x + y * numChunks] = initializeGrassChunk(x, y);
            }
        }
    }

    GrassChunk initializeGrassChunk(int xOffset, int yOffset)
    {
        GrassChunk chunk = new GrassChunk();
        chunk.model = this;

        chunk.FillChunk();
        int chunkDim = Mathf.CeilToInt(fieldSize / numChunks);

        Vector3 c = new Vector3(0.0f, 0.0f, 0.0f);

        c.y = 0.0f;
        c.x = -(chunkDim * 0.5f * numChunks) + chunkDim * xOffset;
        c.z = -(chunkDim * 0.5f * numChunks) + chunkDim * yOffset;
        c.x += chunkDim * 0.5f;
        c.z += chunkDim * 0.5f;

        chunk.bounds = new Bounds(c, new Vector3(-chunkDim, 10.0f, chunkDim));

        initializeGrassShader.SetInt("_XOffset", xOffset);
        initializeGrassShader.SetInt("_YOffset", yOffset);
        initializeGrassShader.SetBuffer(0, "_GrassDataBuffer", chunk.positionsBuffer);
        initializeGrassShader.Dispatch(0, Mathf.CeilToInt(fieldSize / numChunks) * chunkDensity, Mathf.CeilToInt(fieldSize / numChunks) * chunkDensity, 1);

        chunk.material = new Material(grassMaterial);
        chunk.material.SetBuffer("positionBuffer", chunk.culledPositionsBuffer);
        chunk.material.SetFloat("_DisplacementStrength", displacementStrength);
        chunk.material.SetTexture("_WindTex", wind);
        chunk.material.SetInt("_ChunkNum", xOffset + yOffset * numChunks);

        return chunk;
    }

    void CullGrass(GrassChunk chunk, Matrix4x4 VP) {
        if (chunk.IsDisabled) return;
        //Reset Args
        chunk.argsBuffer.SetData(args);

        // Vote
        cullGrassShader.SetMatrix("MATRIX_VP", VP);
        cullGrassShader.SetBuffer(0, "_GrassDataBuffer", chunk.positionsBuffer);
        cullGrassShader.SetBuffer(0, "_VoteBuffer", voteBuffer);
        cullGrassShader.SetVector("_CameraPosition", Camera.main.transform.position);
        cullGrassShader.SetFloat("_Distance", distanceCutoff);
        cullGrassShader.Dispatch(0, numVoteThreadGroups, 1, 1);

        // Scan Instances
        cullGrassShader.SetBuffer(1, "_VoteBuffer", voteBuffer);
        cullGrassShader.SetBuffer(1, "_ScanBuffer", scanBuffer);
        cullGrassShader.SetBuffer(1, "_GroupSumArray", groupSumArrayBuffer);
        cullGrassShader.Dispatch(1, numThreadGroups, 1, 1);

        // Scan Groups
        cullGrassShader.SetInt("_NumOfGroups", numThreadGroups);
        cullGrassShader.SetBuffer(2, "_GroupSumArrayIn", groupSumArrayBuffer);
        cullGrassShader.SetBuffer(2, "_GroupSumArrayOut", scannedGroupSumBuffer);
        cullGrassShader.Dispatch(2, numGroupScanThreadGroups, 1, 1);

        // Compact
        cullGrassShader.SetBuffer(3, "_GrassDataBuffer", chunk.positionsBuffer);
        cullGrassShader.SetBuffer(3, "_VoteBuffer", voteBuffer);
        cullGrassShader.SetBuffer(3, "_ScanBuffer", scanBuffer);
        cullGrassShader.SetBuffer(3, "_ArgsBuffer", chunk.argsBuffer);
        cullGrassShader.SetBuffer(3, "_CulledGrassOutputBuffer", chunk.culledPositionsBuffer);
        cullGrassShader.SetBuffer(3, "_GroupSumArray", scannedGroupSumBuffer);
        cullGrassShader.Dispatch(3, numThreadGroups, 1, 1);
    }

    void GenerateWind() {
        generateWindShader.SetTexture(0, "_WindMap", wind);
        generateWindShader.SetFloat("_Time", Time.time * windSpeed);
        generateWindShader.SetFloat("_Frequency", frequency);
        generateWindShader.SetFloat("_Amplitude", windStrength);
        generateWindShader.Dispatch(0, numWindThreadGroups, numWindThreadGroups, 1);
    }

    void Update() {
        Plane[] frustumPlanes = GeometryUtility.CalculateFrustumPlanes(Camera.main);

        Matrix4x4 P = Camera.main.projectionMatrix;
        Matrix4x4 V = Camera.main.transform.worldToLocalMatrix;
        Matrix4x4 VP = P * V;

        GenerateWind();

        for (int i = 0; i < numChunks * numChunks; ++i)
        {
            bool isVisible = IsChunkVisible(frustumPlanes, chunks[i].bounds);
            if (isVisible)
            {
                float dist = Vector3.Distance(Camera.main.transform.position, chunks[i].bounds.center);
                CullGrass(chunks[i], VP);

                // Set isDisabled to false here instead of skipping the iteration
                chunks[i].IsDisabled = false;
                Graphics.DrawMeshInstancedIndirect(grassMesh, 0, chunks[i].material, fieldBounds, chunks[i].argsBuffer);
            }
            else
            {
                // Set isDisabled to true for chunks that are not visible
                chunks[i].IsDisabled = true;
            }
        }
    }

    bool IsChunkVisible(Plane[] frustumPlanes, Bounds bounds)
    {
        Vector3[] vertices = new Vector3[8];

        // Get the corner vertices of the chunk's bounds
        vertices[0] = bounds.center + new Vector3(bounds.extents.x, bounds.extents.y, bounds.extents.z);
        vertices[1] = bounds.center + new Vector3(bounds.extents.x, bounds.extents.y, -bounds.extents.z);
        vertices[2] = bounds.center + new Vector3(bounds.extents.x, -bounds.extents.y, bounds.extents.z);
        vertices[3] = bounds.center + new Vector3(bounds.extents.x, -bounds.extents.y, -bounds.extents.z);
        vertices[4] = bounds.center + new Vector3(-bounds.extents.x, bounds.extents.y, bounds.extents.z);
        vertices[5] = bounds.center + new Vector3(-bounds.extents.x, bounds.extents.y, -bounds.extents.z);
        vertices[6] = bounds.center + new Vector3(-bounds.extents.x, -bounds.extents.y, bounds.extents.z);
        vertices[7] = bounds.center + new Vector3(-bounds.extents.x, -bounds.extents.y, -bounds.extents.z);

        // Check if any of the vertices are inside the frustum
        for (int i = 0; i < 6; i++)
        {
            bool allOutside = true;
            for (int j = 0; j < 8; j++)
            {
                if (frustumPlanes[i].GetDistanceToPoint(vertices[j]) >= 0)
                {
                    allOutside = false;
                    break;
                }
            }
            if (allOutside)
                return false; // Chunk is completely outside the frustum
        }

        return true; // Chunk is visible
    }

    void OnDisable() {
        voteBuffer.Release();
        scanBuffer.Release();
        groupSumArrayBuffer.Release();
        scannedGroupSumBuffer.Release();
        wind.Release();
        wind = null;
        scannedGroupSumBuffer = null;
        voteBuffer = null;
        scanBuffer = null;
        groupSumArrayBuffer = null;


        for (int i = 0; i < numChunks * numChunks; ++i) {
            chunks[i].FreeChunk();
        }

        chunks = null;
    }

    void OnDrawGizmos() {
        if (chunks != null) {
            for (int i = 0; i < numChunks * numChunks; ++i) {
                Gizmos.color = chunks[i].IsDisabled ? Color.red : Color.yellow;
                Gizmos.DrawWireCube(chunks[i].bounds.center, chunks[i].bounds.size);
            }
        }
    }
}
