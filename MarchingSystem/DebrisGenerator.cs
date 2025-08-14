using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using System.Collections.Generic;
using System.Linq;

public struct PreparedDebris
{
    public VoxelizationManager.VoxelData sourceData;
    public Mesh bakedMesh;
    public NativeArray<float> sdf;
    public Vector3Int resolution;
    public Bounds localBounds;
    public float mass;
}

public static class DebrisGenerator
{
    public class VoxelComponent
    {
        public List<int> voxelIndices = new List<int>();
        public BoundsInt bounds = new BoundsInt();
    }

    [BurstCompile]
    private struct BakeMeshJob : IJob
    {
        public int meshID;
        public bool isConvex;
        public void Execute() => Physics.BakeMesh(meshID, isConvex);
    }
    
    public static List<VoxelComponent> FindFragmentComponents(VoxelizationManager.VoxelData sourceObjectData)
    {
        var components = FindConnectedComponents(sourceObjectData.sdfResults, sourceObjectData.adaptiveResolution);
        if (components.Count <= 1) return new List<VoxelComponent>();

        var sortedComponents = components.OrderByDescending(c => c.voxelIndices.Count).ToList();
        sortedComponents.RemoveAt(0); // Самый большой компонент остается, остальные - кандидаты в обломки
        return sortedComponents;
    }

    public static JobHandle PrepareDebrisAsync(VoxelComponent component, VoxelizationManager.VoxelData sourceData, float density, out PreparedDebris preparedDebris)
    {
        // ... (код подготовки SDF и меша остается без изменений) ...
        int padding = 2;
        Vector3Int debrisMin = component.bounds.min - Vector3Int.one * padding;
        Vector3Int debrisMax = component.bounds.max + Vector3Int.one * padding;
        Vector3Int debrisResolution = (debrisMax - debrisMin) + Vector3Int.one;

        int totalDebrisVoxels = debrisResolution.x * debrisResolution.y * debrisResolution.z;
        var debrisSDF = new NativeArray<float>(totalDebrisVoxels, Allocator.Persistent);
        for (int i = 0; i < totalDebrisVoxels; i++) debrisSDF[i] = 1.0f;

        for (int i = 0; i < component.voxelIndices.Count; i++)
        {
            int sourceIndex = component.voxelIndices[i];
            Vector3Int sourceCoord = IndexTo3D(sourceIndex, sourceData.adaptiveResolution.x, sourceData.adaptiveResolution.y);
            Vector3Int debrisCoord = sourceCoord - debrisMin;
            int debrisIndex = To1DIndex(debrisCoord, debrisResolution.x, debrisResolution.y);
            if (debrisIndex >= 0 && debrisIndex < totalDebrisVoxels)
                debrisSDF[debrisIndex] = sourceData.sdfResults[sourceIndex];
        }

        var voxelSize = new Vector3(
            sourceData.finalBounds.size.x / (sourceData.adaptiveResolution.x - 1),
            sourceData.finalBounds.size.y / (sourceData.adaptiveResolution.y - 1),
            sourceData.finalBounds.size.z / (sourceData.adaptiveResolution.z - 1)
        );
        Vector3 debrisLocalOffset = sourceData.finalBounds.min + Vector3.Scale(debrisMin, voxelSize);

        var settings = new MarchingCubesCore.GenerationSettings { gridSize = debrisResolution, scale = voxelSize, isoLevel = 0.0f, worldSpaceOffset = debrisLocalOffset };
        var meshData = MarchingCubesCore.GenerateMeshAdaptive(VoxelizationManager.Instance.marchingCubesShader, debrisSDF, settings);

        if (meshData.vertexCount == 0)
        {
            debrisSDF.Dispose();
            preparedDebris = default;
            return default;
        }

        Mesh mesh = MeshUtility.CreateUnityMesh(meshData, $"Debris_{sourceData.gameObject.name}");
        float totalDebrisVolume = component.voxelIndices.Count * (voxelSize.x * voxelSize.y * voxelSize.z);
        float mass = Mathf.Max(0.1f, totalDebrisVolume * density);

        preparedDebris = new PreparedDebris { sourceData = sourceData, bakedMesh = mesh, sdf = debrisSDF, resolution = settings.gridSize, localBounds = mesh.bounds, mass = mass };
        return new BakeMeshJob { meshID = mesh.GetInstanceID(), isConvex = true }.Schedule();
    }

    public static void CreateDebrisFromPreparedData(PreparedDebris data, float ejectionForce, int seed)
    {
        GameObject sourceGO = data.sourceData.gameObject;
        ulong debrisId = GenerateDebrisId(sourceGO, data, seed);
        
        // Главная проверка, предотвращающая создание "зомби" обломков.
        if (DestructibleManager.Instance != null && DestructibleManager.Instance.IsDebrisDeleted(debrisId))
        {
            Debug.Log($"[DebrisGenerator] Пропуск создания обломка ID:{debrisId:X8}, так как он уже удален.");
            data.sdf.Dispose();
            Object.Destroy(data.bakedMesh);
            return;
        }
        
        GameObject debrisGO = new GameObject($"Debris_{sourceGO.name}_{debrisId:X8}");
        debrisGO.layer = sourceGO.layer;
        debrisGO.tag = "Debris";

        debrisGO.transform.SetPositionAndRotation(sourceGO.transform.position, sourceGO.transform.rotation);
        debrisGO.transform.localScale = sourceGO.transform.localScale;

        var meshFilter = debrisGO.AddComponent<MeshFilter>();
        meshFilter.mesh = data.bakedMesh;

        var debrisRenderer = debrisGO.AddComponent<MeshRenderer>();
        var originalRenderer = sourceGO.GetComponent<Renderer>();
        if (originalRenderer != null)
        {
            debrisRenderer.sharedMaterial = originalRenderer.sharedMaterial;
        }

        var collider = debrisGO.AddComponent<MeshCollider>();
        collider.sharedMesh = data.bakedMesh;
        collider.convex = true;

        var rb = debrisGO.AddComponent<Rigidbody>();
        rb.mass = data.mass;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.interpolation = RigidbodyInterpolation.None;
        rb.sleepThreshold = 0.1f;
        
        var debrisController = debrisGO.AddComponent<DebrisController>();
        debrisController.Initialize(debrisId);

        var randomState = new System.Random(seed);
        Vector3 randomDirection = new Vector3((float)(randomState.NextDouble() * 2.0 - 1.0), (float)(randomState.NextDouble() * 2.0 - 1.0), (float)(randomState.NextDouble() * 2.0 - 1.0)).normalized;
        rb.AddForce(randomDirection * ejectionForce, ForceMode.Impulse);

        var debrisVoxelData = new VoxelizationManager.VoxelData(debrisGO) { currentState = VoxelizationManager.VoxelData.State.Completed, sdfResults = data.sdf, adaptiveResolution = data.resolution, finalBounds = data.localBounds, voxelMesh = data.bakedMesh, isMeshDataPrepared = false };
        VoxelizationManager.Instance.RegisterDynamicVoxelObject(debrisGO, debrisVoxelData);
    }

    private static ulong GenerateDebrisId(GameObject sourceObject, PreparedDebris data, int seed)
    {
        // ... (код генерации ID остается без изменений) ...
        ushort sourceId = 0;
        if (DestructibleManager.Instance != null)
            DestructibleManager.Instance.TryGetObjectId(sourceObject, out sourceId);
        
        Vector3 center = data.localBounds.center;
        int positionHash = (center.x + center.y + center.z).GetHashCode();
        
        ulong id = ((ulong)sourceId << 48) | ((ulong)(positionHash & 0xFFFF) << 32) | (uint)seed;
        return id;
    }

    public static List<VoxelComponent> FindConnectedComponents(NativeArray<float> sdf, Vector3Int resolution)
    {
        // ... (код поиска компонентов остается без изменений) ...
        var components = new List<VoxelComponent>();
        int totalVoxels = resolution.x * resolution.y * resolution.z;
        var visited = new NativeArray<bool>(totalVoxels, Allocator.Temp);
        for (int i = 0; i < totalVoxels; i++)
        {
            if (sdf[i] < 0 && !visited[i])
            {
                var newComponent = new VoxelComponent();
                var q = new Queue<int>();
                q.Enqueue(i);
                visited[i] = true;
                Vector3Int min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
                Vector3Int max = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
                while (q.Count > 0)
                {
                    int currentIndex = q.Dequeue();
                    newComponent.voxelIndices.Add(currentIndex);
                    Vector3Int currentCoord = IndexTo3D(currentIndex, resolution.x, resolution.y);
                    min = Vector3Int.Min(min, currentCoord);
                    max = Vector3Int.Max(max, currentCoord);
                    for (int j = 0; j < 6; j++)
                    {
                        Vector3Int neighborCoord = currentCoord + GetDirection(j);
                        if (IsInside(neighborCoord, resolution))
                        {
                            int neighborIndex = To1DIndex(neighborCoord, resolution.x, resolution.y);
                            if (sdf[neighborIndex] < 0 && !visited[neighborIndex])
                            {
                                visited[neighborIndex] = true;
                                q.Enqueue(neighborIndex);
                            }
                        }
                    }
                }
                newComponent.bounds.SetMinMax(min, max);
                components.Add(newComponent);
            }
        }
        visited.Dispose();
        return components;
    }

    #region Helpers
    private static int To1DIndex(Vector3Int p, int width, int height) => p.z * width * height + p.y * width + p.x;
    private static Vector3Int IndexTo3D(int i, int width, int height) { int z = i / (width * height); int y = (i - (z * width * height)) / width; int x = i - (z * width * height) - (y * width); return new Vector3Int(x, y, z); }
    private static bool IsInside(Vector3Int coord, Vector3Int resolution) => coord.x >= 0 && coord.x < resolution.x && coord.y >= 0 && coord.y < resolution.y && coord.z >= 0 && coord.z < resolution.z;
    private static Vector3Int GetDirection(int i) { switch (i) { case 0: return Vector3Int.right; case 1: return Vector3Int.left; case 2: return Vector3Int.up; case 3: return Vector3Int.down; case 4: return new Vector3Int(0, 0, 1); case 5: return new Vector3Int(0, 0, -1); default: return Vector3Int.zero; } }
    #endregion
}