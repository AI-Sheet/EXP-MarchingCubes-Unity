// MeshUtility.cs - Полный код для замены

using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static class MeshUtility
{
    public struct MeshData
    {
        public NativeArray<float3> vertices;
        public NativeArray<int> triangles;

        public bool IsValid => vertices.IsCreated && triangles.IsCreated;

        public void Dispose()
        {
            if (vertices.IsCreated) vertices.Dispose();
            if (triangles.IsCreated) triangles.Dispose();
        }
    }

    public static MeshData PrepareMeshData(Mesh mesh)
    {
        var originalVertices = mesh.vertices;
        var vertices = new NativeArray<float3>(originalVertices.Length, Allocator.Persistent);

        for (int i = 0; i < originalVertices.Length; i++)
        {
            vertices[i] = originalVertices[i];
        }

        var triangles = new NativeArray<int>(mesh.triangles, Allocator.Persistent);

        return new MeshData
        {
            vertices = vertices,
            triangles = triangles,
        };
    }

    public static JobHandle CalculateSDFAsyncAdaptive(
        MeshData meshData,
        Bounds localBounds,
        Vector3Int resolution,
        NativeArray<float> sdfResults,
        int batchSize)
    {
        var sdfJob = new SDFCalculationJobAdaptive
        {
            meshVertices = meshData.vertices,
            meshTriangles = meshData.triangles,
            localBoundsMin = localBounds.min,
            resolution = new int3(resolution.x, resolution.y, resolution.z),
            voxelStep = new float3(
                localBounds.size.x / (resolution.x > 1 ? resolution.x - 1 : 1),
                localBounds.size.y / (resolution.y > 1 ? resolution.y - 1 : 1),
                localBounds.size.z / (resolution.z > 1 ? resolution.z - 1 : 1)
            ),
            sdfResults = sdfResults
        };

        int totalVoxels = resolution.x * resolution.y * resolution.z;
        return sdfJob.Schedule(totalVoxels, batchSize);
    }

    public static Mesh CreateUnityMesh(MarchingCubesCore.MeshData meshData, Color[] colors, string meshName)
    {
        Mesh mesh = new Mesh { name = meshName };

        mesh.indexFormat = meshData.vertexCount > 65535 ?
            UnityEngine.Rendering.IndexFormat.UInt32 :
            UnityEngine.Rendering.IndexFormat.UInt16;

        mesh.SetVertices(meshData.vertices, 0, meshData.vertexCount);
        mesh.SetTriangles(meshData.triangles, 0, meshData.triangles.Length, 0);

        // --- НОВОЕ: Применяем вершинные цвета, если они есть ---
        if (colors != null && colors.Length == meshData.vertexCount)
        {
            mesh.SetColors(colors);
        }

        mesh.RecalculateNormals(0f);
        mesh.RecalculateBounds();

        return mesh;
    }
    public static Mesh CreateUnityMesh(MarchingCubesCore.MeshData meshData, string meshName)
    {
        // Просто вызываем основной метод, передавая null вместо цветов.
        return CreateUnityMesh(meshData, null, meshName);
    }
    public static int CalculateOptimalBatchSize(int totalVoxels)
    {
        if (totalVoxels == 0) return 1;
        int coreCount = Mathf.Max(1, SystemInfo.processorCount);
        int baseBatchSize = Mathf.CeilToInt((float)totalVoxels / (coreCount * 8));
        const int minBatchSize = 32;
        const int maxBatchSize = 1024;
        return Mathf.Clamp(baseBatchSize, minBatchSize, maxBatchSize);
    }
}


[BurstCompile(CompileSynchronously = true, FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
public struct SDFCalculationJobAdaptive : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> meshVertices;
    [ReadOnly] public NativeArray<int> meshTriangles;
    [ReadOnly] public float3 localBoundsMin;
    [ReadOnly] public int3 resolution;
    [ReadOnly] public float3 voxelStep;
    [WriteOnly] public NativeArray<float> sdfResults;

    private struct ClosestPointResult
    {
        public float3 point;
        public float3 normal;
        public byte hasResult;
    }

    public void Execute(int index)
    {
        int x = index % resolution.x;
        int y = (index / resolution.x) % resolution.y;
        int z = index / (resolution.x * resolution.y);

        float3 localPos = localBoundsMin + new float3(x, y, z) * voxelStep;
        ClosestPointResult closest = GetClosestPointOnMesh(localPos);

        if (closest.hasResult == 0)
        {
            sdfResults[index] = float.MaxValue;
            return;
        }

        float distance = math.distance(localPos, closest.point);
        float3 direction = math.normalizesafe(localPos - closest.point);
        float dot = math.dot(direction, closest.normal);

        sdfResults[index] = distance * math.sign(dot);
    }

    private ClosestPointResult GetClosestPointOnMesh(float3 point)
    {
        float minDistanceSq = float.MaxValue;
        ClosestPointResult result = new ClosestPointResult { hasResult = 0 };

        if (meshTriangles.Length == 0) return result;

        for (int i = 0; i < meshTriangles.Length; i += 3)
        {
            float3 v1 = meshVertices[meshTriangles[i]];
            float3 v2 = meshVertices[meshTriangles[i + 1]];
            float3 v3 = meshVertices[meshTriangles[i + 2]];

            float3 pointOnTriangle = GetClosestPointOnTriangle(point, v1, v2, v3);
            float distSq = math.distancesq(point, pointOnTriangle);

            if (distSq < minDistanceSq)
            {
                minDistanceSq = distSq;
                result.point = pointOnTriangle;
                result.normal = math.normalize(math.cross(v2 - v1, v3 - v1));
                result.hasResult = 1;
            }
        }
        return result;
    }

    private float3 GetClosestPointOnTriangle(float3 p, float3 a, float3 b, float3 c)
    {
        float3 ab = b - a; float3 ac = c - a; float3 ap = p - a;
        float d1 = math.dot(ab, ap); float d2 = math.dot(ac, ap);
        if (d1 <= 0.0f && d2 <= 0.0f) return a;
        float3 bp = p - b; float d3 = math.dot(ab, bp); float d4 = math.dot(ac, bp);
        if (d3 >= 0.0f && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f) { float v = d1 / (d1 - d3); return a + v * ab; }
        float3 cp = p - c; float d5 = math.dot(ab, cp); float d6 = math.dot(ac, cp);
        if (d6 >= 0.0f && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f) { float w = d2 / (d2 - d6); return a + w * ac; }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return b + w * (c - b); }
        float denom = 1.0f / (va + vb + vc); float v_ = vb * denom; float w_ = vc * denom;
        return a + v_ * ab + w_ * ac;
    }
}