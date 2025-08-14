// --- VoxelizationManager.cs ---

using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using FishNet.Object;

public class VoxelizationManager : NetworkBehaviour
{
    public static VoxelizationManager Instance { get; private set; }

    [Header("Настройки Вокселизации")]
    public int maxVoxelResolution = 64;
    [Tooltip("Максимальное общее количество вокселей")]
    public int maxTotalVoxels = 262144;
    [Tooltip("Минимальное разрешение по одной оси")]
    public int minAxisResolution = 8;
    private List<GameObject> _indexedObjects;

    [Header("Производительность")]
    [Tooltip("Сколько SDF расчетов можно запускать за кадр")]
    public int maxSdfJobsPerFrame = 2;
    [Tooltip("Сколько мешей можно генерировать за кадр")]
    public int maxMeshGenerationsPerFrame = 2;
    [Tooltip("Задержка в секундах перед обновлением меша после получения урона.")]
    [Range(0.05f, 1.0f)]
    public float remeshDelay = 0.2f;

    [Header("Обнаружение Объектов")]
    [Tooltip("Имя слоя для объектов, которые можно вокселизировать")]
    public string voxelLayerName = "Voxr";
    public ComputeShader marchingCubesShader;

    private DestructibleManager _destructibleManager;
    private readonly Dictionary<GameObject, VoxelData> managedMeshes = new Dictionary<GameObject, VoxelData>();
    private readonly Queue<VoxelData> remeshQueue = new Queue<VoxelData>();
    private readonly Dictionary<VoxelData, float> dirtyObjects = new Dictionary<VoxelData, float>();
    private readonly HashSet<VoxelData> islandCheckQueue = new HashSet<VoxelData>();

    public class VoxelData
    {
        public enum State { Queued, CalculatingSDF, GeneratingMesh, Completed, Failed }
        public State currentState = State.Queued;
        public GameObject gameObject;
        public MeshFilter meshFilter;
        public MeshCollider meshCollider;
        public Mesh originalMesh;
        public Mesh voxelMesh;
        public Bounds finalBounds;
        public Vector3Int adaptiveResolution;
        public JobHandle sdfJobHandle;
        public MeshUtility.MeshData preparedMeshData;
        public bool isMeshDataPrepared = false;
        public NativeArray<float> sdfResults;
        public NativeArray<float> originalSdf;

        public VoxelData(GameObject obj)
        {
            gameObject = obj;
            meshFilter = obj.GetComponent<MeshFilter>();
            if (meshFilter != null) originalMesh = meshFilter.sharedMesh;
            meshCollider = obj.GetComponent<MeshCollider>() ?? obj.AddComponent<MeshCollider>();
        }

        public void Cleanup()
        {
            if (isMeshDataPrepared) preparedMeshData.Dispose();
            if (sdfResults.IsCreated) sdfResults.Dispose();
            if (originalSdf.IsCreated) originalSdf.Dispose();
            if (voxelMesh != null) Destroy(voxelMesh);
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        FindMarchingCubesShader();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        InitializeWithManagers();
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        InitializeWithManagers();
    }

    private void InitializeWithManagers()
    {
        if (_destructibleManager == null)
        {
            _destructibleManager = DestructibleManager.Instance;
            if (_destructibleManager != null)
            {
                _indexedObjects = _destructibleManager.GetIndexedObjects();
                // Вокселизируем все разрушаемые объекты при старте
                foreach (var obj in _indexedObjects)
                {
                    RegisterAndVoxelizeObject(obj);
                }
            }
            else
            {
                Debug.LogError("[VoxelizationManager] DestructibleManager не найден при инициализации!");
            }
        }
    }

    private void Update()
    {
        ProcessDirtyQueue();
        ProcessVoxelizationPipeline();
        ProcessRemeshQueue();
    }

    private void OnDestroy()
    {
        var keys = managedMeshes.Keys.ToList();
        foreach (var key in keys)
        {
            if (managedMeshes.TryGetValue(key, out var data))
            {
                data.Cleanup();
            }
        }
        managedMeshes.Clear();
        MarchingCubesCore.CleanupBuffers();
        if (Instance == this) Instance = null;
    }

    public void RegisterAndVoxelizeObject(GameObject objToVoxelize)
    {
        if (objToVoxelize == null || managedMeshes.ContainsKey(objToVoxelize)) return;

        var meshFilter = objToVoxelize.GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            Debug.Log($"[VoxelizationManager] Registering '{objToVoxelize.name}' for voxelization.");
            managedMeshes.Add(objToVoxelize, new VoxelData(objToVoxelize));
        }
        else
        {
            Debug.LogWarning($"[VoxelizationManager] Attempted to register '{objToVoxelize.name}' but it has no valid MeshFilter/Mesh.");
        }
    }

    public bool IsObjectManaged(GameObject obj)
    {
        return managedMeshes.ContainsKey(obj);
    }

    private void FindMarchingCubesShader()
    {
        if (marchingCubesShader == null)
            marchingCubesShader = Resources.Load<ComputeShader>("MarchingCubesCompute");
        if (marchingCubesShader == null)
            Debug.LogError("[VoxelizationManager] MarchingCubesCompute shader not found!");
    }

    private void ProcessDirtyQueue()
    {
        if (dirtyObjects.Count == 0) return;
        var keys = dirtyObjects.Keys.ToList();
        foreach (var data in keys)
        {
            if (data == null || data.gameObject == null)
            {
                dirtyObjects.Remove(data);
                continue;
            }
            dirtyObjects[data] -= Time.deltaTime;
            if (dirtyObjects[data] <= 0)
            {
                dirtyObjects.Remove(data);
                if (!remeshQueue.Contains(data))
                {
                    remeshQueue.Enqueue(data);
                }
            }
        }
    }
    
    private void ProcessVoxelizationPipeline()
    {
        if (managedMeshes.Count == 0) return;

        int sdfJobsStarted = 0;
        int meshGensStarted = 0;
        var completedSdfJobs = new List<VoxelData>();

        var keys = managedMeshes.Keys.ToList();
        foreach (var key in keys)
        {
            if (key == null || !managedMeshes.TryGetValue(key, out var data))
            {
                if (key != null) managedMeshes.Remove(key);
                continue;
            }

            switch (data.currentState)
            {
                case VoxelData.State.Queued:
                    if (sdfJobsStarted < maxSdfJobsPerFrame)
                    {
                        if (data.originalMesh == null)
                        {
                            data.currentState = VoxelData.State.Failed;
                            continue;
                        }
                        StartSDFCalculation(data);
                        sdfJobsStarted++;
                    }
                    break;
                case VoxelData.State.CalculatingSDF:
                    if (data.sdfJobHandle.IsCompleted)
                    {
                        completedSdfJobs.Add(data);
                    }
                    break;
                case VoxelData.State.GeneratingMesh:
                    if (meshGensStarted < maxMeshGenerationsPerFrame)
                    {
                        GenerateAndApplyMesh(data);
                        meshGensStarted++;
                    }
                    break;
            }
        }

        foreach (var data in completedSdfJobs)
        {
            CompleteSDFCalculation(data);
        }
    }

    private void ProcessRemeshQueue()
    {
        int meshGensStarted = 0;
        while (remeshQueue.Count > 0 && meshGensStarted < maxMeshGenerationsPerFrame)
        {
            var data = remeshQueue.Dequeue();
            if (data != null && data.gameObject != null)
            {
                GenerateAndApplyMesh(data);
                meshGensStarted++;
            }
        }
    }

    private void StartSDFCalculation(VoxelData data)
    {
        data.currentState = VoxelData.State.CalculatingSDF;

        if (!data.isMeshDataPrepared)
        {
            data.preparedMeshData = MeshUtility.PrepareMeshData(data.originalMesh);
            data.isMeshDataPrepared = true;
        }

        Bounds originalBounds = data.originalMesh.bounds;
        Vector3 worldSize = Vector3.Scale(originalBounds.size, data.gameObject.transform.lossyScale);
        data.adaptiveResolution = CalculateAdaptiveResolution(new Bounds(Vector3.zero, worldSize));

        Vector3 voxelSize = new Vector3(
            originalBounds.size.x / (data.adaptiveResolution.x > 1 ? data.adaptiveResolution.x - 1 : 1),
            originalBounds.size.y / (data.adaptiveResolution.y > 1 ? data.adaptiveResolution.y - 1 : 1),
            originalBounds.size.z / (data.adaptiveResolution.z > 1 ? data.adaptiveResolution.z - 1 : 1)
        );

        data.finalBounds = originalBounds;
        data.finalBounds.Expand(voxelSize);

        int totalVoxels = data.adaptiveResolution.x * data.adaptiveResolution.y * data.adaptiveResolution.z;
        if (totalVoxels <= 0)
        {
            data.currentState = VoxelData.State.Failed;
            return;
        }

        data.sdfResults = new NativeArray<float>(totalVoxels, Allocator.Persistent);
        int batchSize = MeshUtility.CalculateOptimalBatchSize(totalVoxels);

        data.sdfJobHandle = MeshUtility.CalculateSDFAsyncAdaptive(
            data.preparedMeshData, data.finalBounds, data.adaptiveResolution, data.sdfResults, batchSize);
    }

    private void CompleteSDFCalculation(VoxelData data)
    {
        data.sdfJobHandle.Complete();
        if (!data.originalSdf.IsCreated)
        {
            data.originalSdf = new NativeArray<float>(data.sdfResults, Allocator.Persistent);
        }
        data.currentState = VoxelData.State.GeneratingMesh;
    }

    private void GenerateAndApplyMesh(VoxelData data)
    {
        if (data == null || data.gameObject == null) return;
        if (!data.sdfResults.IsCreated || data.sdfResults.Length == 0)
        {
            data.currentState = VoxelData.State.Failed;
            return;
        }

        var settings = new MarchingCubesCore.GenerationSettings
        {
            gridSize = data.adaptiveResolution,
            scale = new Vector3(
                data.finalBounds.size.x / (data.adaptiveResolution.x > 1 ? data.adaptiveResolution.x - 1 : 1),
                data.finalBounds.size.y / (data.adaptiveResolution.y > 1 ? data.adaptiveResolution.y - 1 : 1),
                data.finalBounds.size.z / (data.adaptiveResolution.z > 1 ? data.adaptiveResolution.z - 1 : 1)
            ),
            isoLevel = 0.0f,
            worldSpaceOffset = data.finalBounds.min
        };

        var meshData = MarchingCubesCore.GenerateMeshAdaptive(marchingCubesShader, data.sdfResults, settings);

        if (meshData.vertexCount == 0)
        {
            managedMeshes.Remove(data.gameObject);
            if (base.IsServerStarted)
            {
                ServerManager.Despawn(data.gameObject);
            }
            data.Cleanup();
            return;
        }

        if (data.voxelMesh != null) Destroy(data.voxelMesh);
        data.voxelMesh = MeshUtility.CreateUnityMesh(meshData, $"{data.originalMesh.name}_Voxelized");
        data.meshFilter.mesh = data.voxelMesh;
        UpdateMeshCollider(data);

        data.currentState = VoxelData.State.Completed;
    }

    private void UpdateMeshCollider(VoxelData data)
    {
        if (data.meshCollider == null || data.voxelMesh == null) return;
        try
        {
            var rb = data.gameObject.GetComponent<Rigidbody>();
            bool wasEnabled = data.meshCollider.enabled;
            data.meshCollider.enabled = false;
            data.meshCollider.sharedMesh = data.voxelMesh;
            if (rb != null)
            {
                data.meshCollider.convex = !rb.isKinematic;
            }
            data.meshCollider.enabled = wasEnabled;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[VoxelizationManager] Failed to update MeshCollider on {data.gameObject.name}: {e.Message}");
        }
    }

    private Vector3Int CalculateAdaptiveResolution(Bounds worldSpaceBounds)
    {
        Vector3 size = worldSpaceBounds.size;
        float maxSize = Mathf.Max(size.x, size.y, size.z);
        if (maxSize <= 0f) return new Vector3Int(minAxisResolution, minAxisResolution, minAxisResolution);

        float baseResolution = maxVoxelResolution;
        Vector3 proportions = size / maxSize;
        Vector3 targetResolution = proportions * baseResolution;

        int totalVoxels = Mathf.RoundToInt(targetResolution.x * targetResolution.y * targetResolution.z);
        if (totalVoxels > maxTotalVoxels)
        {
            float scaleFactor = Mathf.Pow((float)maxTotalVoxels / totalVoxels, 1f / 3f);
            targetResolution *= scaleFactor;
        }

        return new Vector3Int(
            Mathf.Max(minAxisResolution, Mathf.CeilToInt(targetResolution.x)),
            Mathf.Max(minAxisResolution, Mathf.CeilToInt(targetResolution.y)),
            Mathf.Max(minAxisResolution, Mathf.CeilToInt(targetResolution.z))
        );
    }

    public bool IsObjectReadyForDamage(GameObject obj)
    {
        return managedMeshes.TryGetValue(obj, out var data) && data.currentState == VoxelData.State.Completed;
    }

    public VoxelData GetVoxelDataFor(GameObject obj)
    {
        managedMeshes.TryGetValue(obj, out var data);
        return data;
    }

    public void RequestRemesh(GameObject obj)
    {
        if (managedMeshes.TryGetValue(obj, out var data))
        {
            dirtyObjects[data] = remeshDelay;
        }
    }

    public void RequestRemeshImmediate(VoxelData data)
    {
        if (data == null || data.gameObject == null) return;

        if (dirtyObjects.ContainsKey(data)) dirtyObjects.Remove(data);
        if (!remeshQueue.Contains(data)) remeshQueue.Enqueue(data);
    }

    public void RegisterDynamicVoxelObject(GameObject obj, VoxelData data)
    {
        if (obj == null || managedMeshes.ContainsKey(obj)) return;
        managedMeshes.Add(obj, data);
    }
}