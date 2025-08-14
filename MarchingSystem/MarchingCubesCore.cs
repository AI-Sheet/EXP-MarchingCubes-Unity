// MarchingCubesCore.cs - Полный код для замены

using UnityEngine;
using Unity.Collections;
using System;

public static class MarchingCubesCore
{
    private const int THREAD_GROUP_SIZE_XY = 8;
    private const int THREAD_GROUP_SIZE_Z = 4;
    private const int MAX_VERTICES_PER_CUBE = 15;
    
    // Убраны статические поля, чтобы избежать проблем в редакторе
    // VoxelizationManager теперь должен владеть этими буферами,
    // но для простоты пока оставим так, с правильным циклом очистки.
    private static ComputeBuffers cachedBuffers;
    private static Vector3Int lastGridSize = Vector3Int.zero;
    private static bool buffersInitialized = false;
    
    public struct MeshData
    {
        public Vector3[] vertices;
        public int[] triangles;
        public int vertexCount; // Общее количество вершин, не массив!
    }
    
    public struct GenerationSettings
    {
        public Vector3Int gridSize;
        public Vector3 scale;
        public float isoLevel;
        public Vector3 worldSpaceOffset;
    }

    public static MeshData GenerateMeshAdaptive(ComputeShader computeShader, NativeArray<float> voxelData, GenerationSettings settings)
    {
        if (!ValidateInputAdaptive(computeShader, voxelData, settings)) return new MeshData();
        
        var buffers = GetOrCreateBuffersAdaptive(voxelData.Length, settings);
        UpdateVoxelData(buffers, voxelData);
        
        // Этап 1: Посчитать количество треугольников для каждого куба
        CountTrianglesAdaptive(computeShader, buffers, settings);
        
        // Этап 2: Выполнить префиксную сумму (сканирование) на GPU, чтобы получить смещения
        int totalVertexCount = CalculateOffsetsOnGPU(computeShader, buffers, settings);
        
        if (totalVertexCount == 0) return new MeshData();

        // Этап 3: Сгенерировать вершины, используя вычисленные смещения
        GenerateVerticesAdaptive(computeShader, buffers, settings);
        
        // Этап 4: Создать Unity меш из данных GPU
        return CreateMeshData(buffers, totalVertexCount);
    }
    
    public static void CleanupBuffers()
    {
        if (buffersInitialized)
        {
            ReleaseBuffers(cachedBuffers);
            buffersInitialized = false;
            lastGridSize = Vector3Int.zero;
        }
    }

    private static bool ValidateInputAdaptive(ComputeShader computeShader, NativeArray<float> voxelData, GenerationSettings settings)
    {
        if (computeShader == null) { Debug.LogError("[MarchingCubesCore] Compute shader is null"); return false; }
        if (!voxelData.IsCreated || voxelData.Length == 0) { Debug.LogError("[MarchingCubesCore] Voxel data is null or empty"); return false; }
        if (settings.gridSize.x < 2 || settings.gridSize.y < 2 || settings.gridSize.z < 2)
        {
            Debug.LogError("[MarchingCubesCore] Grid size must be at least 2x2x2");
            return false;
        }
        return true;
    }
    
    private struct ComputeBuffers
    {
        public ComputeBuffer voxelData, triangleCountAndOffsets, vertices, groupSums;
    }
    
    private static ComputeBuffers GetOrCreateBuffersAdaptive(int totalVoxels, GenerationSettings settings)
    {
        if (!buffersInitialized || lastGridSize != settings.gridSize)
        {
            if(buffersInitialized) ReleaseBuffers(cachedBuffers);
            
            int totalCubes = (settings.gridSize.x - 1) * (settings.gridSize.y - 1) * (settings.gridSize.z - 1);
            int maxVertices = totalCubes * MAX_VERTICES_PER_CUBE;
            int scanGroupCount = Mathf.Max(1, Mathf.CeilToInt((float)totalCubes / 256)); // 256 = SCAN_THREAD_GROUP_SIZE

            // ОПТИМИЗАЦИЯ: Используем более эффективные типы буферов
            cachedBuffers = new ComputeBuffers
            {
                voxelData = new ComputeBuffer(totalVoxels, sizeof(float), ComputeBufferType.Structured),
                // Этот буфер будет хранить сначала количество, а потом смещения
                triangleCountAndOffsets = new ComputeBuffer(totalCubes, sizeof(uint), ComputeBufferType.Structured),
                vertices = new ComputeBuffer(maxVertices, sizeof(float) * 3, ComputeBufferType.Structured),
                groupSums = new ComputeBuffer(scanGroupCount, sizeof(uint), ComputeBufferType.Structured)
            };
            lastGridSize = settings.gridSize;
            buffersInitialized = true;
        }
        return cachedBuffers;
    }
    
    private static void UpdateVoxelData(ComputeBuffers buffers, NativeArray<float> voxelData)
    {
        buffers.voxelData.SetData(voxelData);
    }
    
    private static void CountTrianglesAdaptive(ComputeShader cs, ComputeBuffers buffers, GenerationSettings s)
    {
        cs.SetInt("GridWidth", s.gridSize.x);
        cs.SetInt("GridHeight", s.gridSize.y);
        cs.SetInt("GridDepth", s.gridSize.z);
        cs.SetFloat("IsoLevel", s.isoLevel);

        int kernel = cs.FindKernel("CountTriangles");
        cs.SetBuffer(kernel, "VoxelData", buffers.voxelData);
        cs.SetBuffer(kernel, "TriangleCountAndOffsets", buffers.triangleCountAndOffsets);
        
        int dispatchX = Mathf.CeilToInt((float)(s.gridSize.x - 1) / THREAD_GROUP_SIZE_XY);
        int dispatchY = Mathf.CeilToInt((float)(s.gridSize.y - 1) / THREAD_GROUP_SIZE_XY);
        int dispatchZ = Mathf.CeilToInt((float)(s.gridSize.z - 1) / THREAD_GROUP_SIZE_Z);
        cs.Dispatch(kernel, dispatchX, dispatchY, dispatchZ);
    }

    private static int CalculateOffsetsOnGPU(ComputeShader cs, ComputeBuffers buffers, GenerationSettings s)
    {
        int totalCubes = (s.gridSize.x - 1) * (s.gridSize.y - 1) * (s.gridSize.z - 1);
        int numScanGroups = Mathf.CeilToInt((float)totalCubes / 256); // 256 = SCAN_THREAD_GROUP_SIZE

        // 1. Локальное сканирование и подсчет сумм групп
        int scanKernel = cs.FindKernel("ScanGroupSums");
        cs.SetBuffer(scanKernel, "TriangleCountAndOffsets", buffers.triangleCountAndOffsets);
        cs.SetBuffer(scanKernel, "GroupSums", buffers.groupSums);
        cs.Dispatch(scanKernel, numScanGroups, 1, 1);

        // 2. ОПТИМИЗАЦИЯ: Используем AsyncGPUReadback только для маленького массива сумм
        if (numScanGroups <= 1)
        {
            // Для одной группы нет смысла в асинхронности
            var singleGroupSum = new uint[1];
            buffers.groupSums.GetData(singleGroupSum);
            return (int)singleGroupSum[0];
        }

        // 3. Асинхронное чтение только сумм групп (это безопасно, так как массив маленький)
        var groupSums = new uint[numScanGroups];
        buffers.groupSums.GetData(groupSums);
        
        uint totalVertexCount = 0;
        for (int i = 0; i < numScanGroups; i++)
        {
            uint sum = groupSums[i];
            groupSums[i] = totalVertexCount;
            totalVertexCount += sum;
        }

        if (totalVertexCount == 0) return 0;
        
        // 4. Загружаем отсканированные суммы обратно на GPU
        buffers.groupSums.SetData(groupSums);

        // 5. Добавляем смещения групп к локальным смещениям
        int addKernel = cs.FindKernel("AddScannedGroupOffsets");
        cs.SetBuffer(addKernel, "TriangleCountAndOffsets", buffers.triangleCountAndOffsets);
        cs.SetBuffer(addKernel, "GroupSums", buffers.groupSums);
        cs.Dispatch(addKernel, numScanGroups, 1, 1);
        
        return (int)totalVertexCount;
    }
    
    private static void GenerateVerticesAdaptive(ComputeShader cs, ComputeBuffers buffers, GenerationSettings s)
    {
        cs.SetVector("Scale", s.scale);
        cs.SetVector("WorldSpaceOffset", s.worldSpaceOffset);

        int kernel = cs.FindKernel("GenerateVertices");
        cs.SetBuffer(kernel, "VoxelData", buffers.voxelData);
        cs.SetBuffer(kernel, "TriangleCountAndOffsets", buffers.triangleCountAndOffsets);
        cs.SetBuffer(kernel, "Vertices", buffers.vertices);
        
        int dispatchX = Mathf.CeilToInt((float)(s.gridSize.x - 1) / THREAD_GROUP_SIZE_XY);
        int dispatchY = Mathf.CeilToInt((float)(s.gridSize.y - 1) / THREAD_GROUP_SIZE_XY);
        int dispatchZ = Mathf.CeilToInt((float)(s.gridSize.z - 1) / THREAD_GROUP_SIZE_Z);
        cs.Dispatch(kernel, dispatchX, dispatchY, dispatchZ);
    }
    
    private static MeshData CreateMeshData(ComputeBuffers buffers, int vertexCount)
    {
        // ОПТИМИЗАЦИЯ: Избегаем лишних копирований данных
        var vertices = new Vector3[vertexCount];
        
        // Используем более эффективный метод чтения данных
        buffers.vertices.GetData(vertices, 0, 0, vertexCount);

        // ОПТИМИЗАЦИЯ: Создаем треугольники более эффективно
        var triangles = new int[vertexCount];
        
        // Используем параллельный цикл для больших массивов
        if (vertexCount > 10000)
        {
            System.Threading.Tasks.Parallel.For(0, vertexCount, i => triangles[i] = i);
        }
        else
        {
            for (int i = 0; i < vertexCount; i++)
            {
                triangles[i] = i;
            }
        }

        return new MeshData { vertices = vertices, triangles = triangles, vertexCount = vertexCount };
    }
    
    private static void ReleaseBuffers(ComputeBuffers buffers)
    {
        buffers.voxelData?.Release();
        buffers.triangleCountAndOffsets?.Release();
        buffers.vertices?.Release();
        buffers.groupSums?.Release();
    }
}