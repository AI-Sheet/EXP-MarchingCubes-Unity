// --- Файл: CaveSystemController.cs (С АБСОЛЮТНО НАДЕЖНОЙ ЗАГРУЗКОЙ) ---

using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using Unity.Burst;
using Unity.Mathematics;
using OpenSimplex2V;
using Unity.Collections.LowLevel.Unsafe;

public class CaveSystemController : NetworkBehaviour
{
    // ... (поля остаются те же) ...
    #region Data Structures and Fields
    [System.Serializable]
    public struct ChunkData { public Vector3Int chunkCoord; public float[] sdfData; public ChunkData(Vector3Int coord, float[] data) { chunkCoord = coord; sdfData = data; } public void Serialize(BinaryWriter writer) { writer.Write(chunkCoord.x); writer.Write(chunkCoord.y); writer.Write(chunkCoord.z); writer.Write(sdfData.Length); foreach (float val in sdfData) { writer.Write(val); } } public static ChunkData Deserialize(BinaryReader reader) { var coord = new Vector3Int(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()); int length = reader.ReadInt32(); var data = new float[length]; for (int i = 0; i < length; i++) { data[i] = reader.ReadSingle(); } return new ChunkData(coord, data); } }
    private class PlayerChunkInfo { public readonly NetworkConnection connection; public HashSet<Vector3Int> loadedClientChunks = new HashSet<Vector3Int>(); public PlayerChunkInfo(NetworkConnection conn) { connection = conn; } }
    
    [Header("Настройки мира")]
    [SerializeField] private int worldDepthInChunks = 5;
    [SerializeField] private Vector3Int chunkVoxelResolution = new Vector3Int(32, 32, 32);
    [SerializeField] private float voxelSize = 0.7f;
    [SerializeField] private bool useParentTransformAsOrigin = true;
    [SerializeField] private Vector3 additionalWorldOffset = Vector3.zero;

    [Header("Настройки динамической загрузки")]
    [SerializeField] private Vector3Int surfaceLoadRadius = new Vector3Int(5, 1, 5);
    [Tooltip("Максимальная дистанция 'заливки' от игрока под землей")]
    [SerializeField] private int undergroundLoadDistance = 4;
    [SerializeField] private float playerCheckInterval = 0.5f;
    [SerializeField] private int clientCacheSize = 200;

    [Header("Настройки генерации")]
    public long worldSeed = 12345;
    [SerializeField] private float caveShapeFrequency = 0.05f;
    [Range(0.0f, 1.0f)][SerializeField] private float tunnelThickness = 0.3f;
    [Range(0.5f, 3.0f)][SerializeField] private float verticalityFactor = 1.5f;
    [Range(1, 10)][SerializeField] private int patchThickness = 2;
    [Header("Настройки рельефа и входов")]
    [SerializeField] private float surfaceNoiseFrequency = 0.008f;
    [SerializeField] private float surfaceNoiseAmplitude = 20f;
    [Range(1f, 50f)][SerializeField] private float surfaceIntegrityDepth = 10f;
    
    [Header("Процедурный цвет (градиент)")]
    public Material caveMaterial;
    [ColorUsage(true, true)] public Color surfaceColor = Color.gray;
    [ColorUsage(true, true)] public Color deepColor = new Color(0.2f, 0.15f, 0.1f);
    public float gradientTopY = 20f;
    public float gradientBottomY = -100f;

    [Header("Компоненты и персистентность")]
    public ComputeShader marchingCubesShader;
    [SerializeField] private bool enablePersistence = true;
    private string _chunkSavePath;

    private readonly Dictionary<Vector3Int, ChunkData> _serverChunkCache = new Dictionary<Vector3Int, ChunkData>();
    private readonly Dictionary<int, PlayerChunkInfo> _trackedPlayers = new Dictionary<int, PlayerChunkInfo>();
    private CaveGenParams _caveGenParams;
    private System.Action<NetworkConnection, RemoteConnectionStateArgs> _onRemoteConnectionStateLambda;
    private readonly Dictionary<Vector3Int, GameObject> _clientLoadedChunks = new Dictionary<Vector3Int, GameObject>();
    private readonly Dictionary<Vector3Int, GameObject> _clientChunkCache = new Dictionary<Vector3Int, GameObject>();
    private readonly List<Vector3Int> _clientCacheQueue = new List<Vector3Int>();
    #endregion

    #region Initialization & Connection Events
    private void Awake() { _onRemoteConnectionStateLambda = ServerManager_OnRemoteConnectionState; LookupTable.Gradients.Prepare(); }
    public override void OnStartServer() { base.OnStartServer(); _caveGenParams = new CaveGenParams { seed = this.worldSeed, caveShapeFrequency = this.caveShapeFrequency, tunnelThickness = this.tunnelThickness, verticalityFactor = this.verticalityFactor, surfaceNoiseFrequency = this.surfaceNoiseFrequency, surfaceNoiseAmplitude = this.surfaceNoiseAmplitude, surfaceIntegrityDepth = this.surfaceIntegrityDepth }; Server_InitializePersistence(); InstanceFinder.ServerManager.OnRemoteConnectionState += _onRemoteConnectionStateLambda; StartCoroutine(Server_UpdatePlayerChunksLoop()); }
    public override void OnStopClient() { base.OnStopClient(); foreach (var chunk in _clientLoadedChunks.Values) { if (chunk) Destroy(chunk); } foreach (var chunk in _clientChunkCache.Values) { if (chunk) Destroy(chunk); } _clientLoadedChunks.Clear(); _clientChunkCache.Clear(); _clientCacheQueue.Clear(); MarchingCubesCore.CleanupBuffers(); }
    private void OnDestroy() { LookupTable.Gradients.Dispose(); if (InstanceFinder.ServerManager != null) { InstanceFinder.ServerManager.OnRemoteConnectionState -= _onRemoteConnectionStateLambda; } }
    private void ServerManager_OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args) { if (args.ConnectionState == RemoteConnectionState.Started) { _trackedPlayers.Add(conn.ClientId, new PlayerChunkInfo(conn)); } else if (args.ConnectionState == RemoteConnectionState.Stopped) { _trackedPlayers.Remove(conn.ClientId); } }
    #endregion
    
    #region Server: Player Chunk Update Loop
    private IEnumerator Server_UpdatePlayerChunksLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(playerCheckInterval);
            if (!base.IsServerStarted || _trackedPlayers.Count == 0) continue;
            foreach (var playerInfo in _trackedPlayers.Values.ToList())
            {
                if (!playerInfo.connection.IsActive || playerInfo.connection.FirstObject == null) continue;
                Server_UpdateChunksForPlayer(playerInfo);
            }
        }
    }

    [Server]
    private unsafe void Server_UpdateChunksForPlayer(PlayerChunkInfo playerInfo)
    {
        Transform playerTransform = playerInfo.connection.FirstObject.transform;
        Vector3 playerPos = playerTransform.position;
        Vector3Int playerChunkCoord = WorldToChunkCoords(playerPos);
        
        Vector3 calculatedWorldOffset = useParentTransformAsOrigin ? transform.position + additionalWorldOffset : additionalWorldOffset;
        float sdfAtPlayer = CaveGenerator.CalculateSDFAtPoint(
            playerPos.x - calculatedWorldOffset.x, playerPos.y - calculatedWorldOffset.y, playerPos.z - calculatedWorldOffset.z, 
            in _caveGenParams, LookupTable.Gradients.GetPointer()
        );
        bool isUnderground = sdfAtPlayer > -0.5f; 
        
        HashSet<Vector3Int> requiredChunks = new HashSet<Vector3Int>();

        // =========================================================================
        // === НАДЕЖНОЕ РЕШЕНИЕ: ЗАГРУЖАЕМ ЧАНКИ ДЛЯ ПОВЕРХНОСТИ ВСЕГДА ===
        // =========================================================================
        // Мы всегда загружаем "ковер" чанков вокруг игрока на поверхности.
        // Это гарантирует, что земля под ногами и вокруг него будет всегда,
        // предотвращая падения сквозь мир.
        for (int x = -surfaceLoadRadius.x; x <= surfaceLoadRadius.x; x++)
        for (int y = -surfaceLoadRadius.y; y <= surfaceLoadRadius.y; y++)
        for (int z = -surfaceLoadRadius.z; z <= surfaceLoadRadius.z; z++)
        {
            requiredChunks.Add(playerChunkCoord + new Vector3Int(x, y, z));
        }

        // =========================================================================
        // === ...И ДОБАВЛЯЕМ К НИМ ПОДЗЕМНЫЕ, ЕСЛИ НУЖНО ===
        // =========================================================================
        // Если игрок под землей, мы ДОПОЛНИТЕЛЬНО запускаем заливку, чтобы
        // прогрузить видимые ему ветки пещер. Результаты добавляются к уже
        // существующему набору 'requiredChunks'.
        if (isUnderground)
        {
            Queue<Vector3Int> chunksToVisit = new Queue<Vector3Int>();
            HashSet<Vector3Int> visitedChunks = new HashSet<Vector3Int>(requiredChunks); // Начинаем с уже посещенных

            chunksToVisit.Enqueue(playerChunkCoord);
            visitedChunks.Add(playerChunkCoord);

            while (chunksToVisit.Count > 0)
            {
                Vector3Int currentChunk = chunksToVisit.Dequeue();
                requiredChunks.Add(currentChunk); // Добавляем в общий набор

                if (Vector3Int.Distance(playerChunkCoord, currentChunk) > undergroundLoadDistance) continue;
                if (Server_IsChunkTrivial(currentChunk)) continue;

                // Добавляем соседей в очередь на проверку
                TryEnqueueNeighbor(currentChunk + Vector3Int.up);
                TryEnqueueNeighbor(currentChunk + Vector3Int.down);
                TryEnqueueNeighbor(currentChunk + Vector3Int.right);
                TryEnqueueNeighbor(currentChunk + Vector3Int.left);
                TryEnqueueNeighbor(currentChunk + Vector3Int.forward);
                TryEnqueueNeighbor(currentChunk + Vector3Int.back);

                void TryEnqueueNeighbor(Vector3Int neighbor)
                {
                    if (!visitedChunks.Contains(neighbor))
                    {
                        visitedChunks.Add(neighbor);
                        chunksToVisit.Enqueue(neighbor);
                    }
                }
            }
        }
        
        var chunksToLoad = requiredChunks.Where(c => c.y <= 0 && c.y > -worldDepthInChunks).Except(playerInfo.loadedClientChunks).ToList();
        var chunksToUnload = playerInfo.loadedClientChunks.Except(requiredChunks).ToList();
        
        foreach (var chunkCoord in chunksToLoad) { Server_LoadAndSendChunk(chunkCoord, playerInfo.connection); }
        foreach (var chunkCoord in chunksToUnload) { TargetUnloadChunkRpc(playerInfo.connection, chunkCoord); }
        
        playerInfo.loadedClientChunks = requiredChunks;
    }
    #endregion
    
    // ... (Остальная часть файла без изменений) ...
    #region Server: Chunk Generation & Management
    [Server] private unsafe void Server_LoadAndSendChunk(Vector3Int chunkCoord, NetworkConnection conn) { if (!_serverChunkCache.TryGetValue(chunkCoord, out ChunkData data)) { if (!enablePersistence || !Server_LoadChunkFromDisk(chunkCoord, out data)) { data = Server_GenerateChunkData(chunkCoord); } _serverChunkCache[chunkCoord] = data; } if (!IsChunkTrivial(data.sdfData)) { TargetBuildChunkRpc(conn, data); if (enablePersistence) { Server_SaveChunkToDisk(data); } } }
    [Server] private unsafe ChunkData Server_GenerateChunkData(Vector3Int chunkCoord) { int totalVoxels = chunkVoxelResolution.x * chunkVoxelResolution.y * chunkVoxelResolution.z; var sdfData = new NativeArray<float>(totalVoxels, Allocator.TempJob); Vector3 calculatedWorldOffset = useParentTransformAsOrigin ? transform.position + additionalWorldOffset : additionalWorldOffset; var sdfJob = new GenerateChunkSDFJob { sdfResults = sdfData, chunkCoord = chunkCoord, chunkResolution = this.chunkVoxelResolution, voxelSize = this.voxelSize, caveParams = _caveGenParams, worldOffset = calculatedWorldOffset, gradientPtr = LookupTable.Gradients.GetPointer() }; sdfJob.Schedule(totalVoxels, 128).Complete(); PatchChunkBorders(ref sdfData, chunkCoord); var chunkData = new ChunkData(chunkCoord, sdfData.ToArray()); sdfData.Dispose(); return chunkData; }
    [Server] private bool Server_IsChunkTrivial(Vector3Int chunkCoord) { if (!_serverChunkCache.TryGetValue(chunkCoord, out ChunkData data)) { if (enablePersistence && Server_LoadChunkFromDisk(chunkCoord, out data)) { _serverChunkCache[chunkCoord] = data; } else { data = Server_GenerateChunkData(chunkCoord); _serverChunkCache[chunkCoord] = data; } } return IsChunkTrivial(data.sdfData); }
    private bool IsChunkTrivial(float[] sdfData) { if (sdfData == null || sdfData.Length == 0) return true; bool allSolid = true, allAir = true; for (int i = 0; i < sdfData.Length; i++) { if (sdfData[i] > 0.0f) allSolid = false; if (sdfData[i] < 0.0f) allAir = false; if (!allSolid && !allAir) return false; } return allSolid || allAir; }
    #endregion
    #region Client: RPCs and Chunk Caching
    [TargetRpc] private void TargetBuildChunkRpc(NetworkConnection conn, ChunkData data) { if (_clientLoadedChunks.ContainsKey(data.chunkCoord)) return; if (_clientChunkCache.TryGetValue(data.chunkCoord, out GameObject cachedChunk)) { cachedChunk.SetActive(true); _clientChunkCache.Remove(data.chunkCoord); _clientCacheQueue.Remove(data.chunkCoord); _clientLoadedChunks[data.chunkCoord] = cachedChunk; } else { Client_BuildChunk(data); } }
    [TargetRpc] private void TargetUnloadChunkRpc(NetworkConnection conn, Vector3Int chunkCoord) { if (_clientLoadedChunks.TryGetValue(chunkCoord, out GameObject chunkObject)) { chunkObject.SetActive(false); _clientLoadedChunks.Remove(chunkCoord); _clientChunkCache[chunkCoord] = chunkObject; _clientCacheQueue.Add(chunkCoord); Client_TrimCache(); } }
    private void Client_BuildChunk(ChunkData data) { FindShaderIfNeeded(); if (marchingCubesShader == null) { Debug.LogError("[CaveSystem Client] Marching Cubes Shader не найден."); return; } var sdfData = new NativeArray<float>(data.sdfData, Allocator.Persistent); GameObject chunkGO = CreateMeshObject(sdfData, data.chunkCoord, $"CaveChunk_{data.chunkCoord.x}_{data.chunkCoord.y}_{data.chunkCoord.z}", caveMaterial); if (chunkGO != null) { _clientLoadedChunks[data.chunkCoord] = chunkGO; } }
    private void Client_TrimCache() { if (clientCacheSize <= 0 || _clientCacheQueue.Count <= clientCacheSize) return; int itemsToRemove = _clientCacheQueue.Count - clientCacheSize; for (int i = 0; i < itemsToRemove; i++) { Vector3Int coordToRemove = _clientCacheQueue[0]; _clientCacheQueue.RemoveAt(0); if (_clientChunkCache.TryGetValue(coordToRemove, out GameObject objectToDestroy)) { _clientChunkCache.Remove(coordToRemove); Destroy(objectToDestroy); } } }
    #endregion
    #region Persistence (Disk I/O)
    [Server] private void Server_InitializePersistence() { if (!enablePersistence) return; _chunkSavePath = Path.Combine(Application.persistentDataPath, "chunks"); if (!Directory.Exists(_chunkSavePath)) { Directory.CreateDirectory(_chunkSavePath); } else { Server_LoadAllChunksFromDisk(); } }
    [Server] private void Server_SaveChunkToDisk(ChunkData data) { string filePath = Path.Combine(_chunkSavePath, $"chunk_{data.chunkCoord.x}_{data.chunkCoord.y}_{data.chunkCoord.z}.chunk"); try { using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write); using var writer = new BinaryWriter(stream); data.Serialize(writer); } catch (System.Exception e) { Debug.LogError($"[CaveSystem Server] Не удалось сохранить чанк {data.chunkCoord}: {e.Message}"); } }
    [Server] private bool Server_LoadChunkFromDisk(Vector3Int chunkCoord, out ChunkData data) { string filePath = Path.Combine(_chunkSavePath, $"chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}.chunk"); if (File.Exists(filePath)) { try { using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read); using var reader = new BinaryReader(stream); data = ChunkData.Deserialize(reader); return true; } catch (System.Exception e) { Debug.LogError($"[CaveSystem Server] Не удалось загрузить чанк {chunkCoord}: {e.Message}"); } } data = default; return false; }
    [Server] private void Server_LoadAllChunksFromDisk() { var files = Directory.GetFiles(_chunkSavePath, "*.chunk"); foreach (var file in files) { string fileName = Path.GetFileNameWithoutExtension(file); string[] parts = fileName.Split('_'); if (parts.Length == 4 && int.TryParse(parts[1], out int x) && int.TryParse(parts[2], out int y) && int.TryParse(parts[3], out int z)) { if (Server_LoadChunkFromDisk(new Vector3Int(x, y, z), out ChunkData data)) { _serverChunkCache[data.chunkCoord] = data; } } } }
    #endregion
    #region Helpers & Utilities
    private Vector3Int WorldToChunkCoords(Vector3 worldPos) { Vector3 chunkWorldSize = new Vector3((chunkVoxelResolution.x - 1) * voxelSize, (chunkVoxelResolution.y - 1) * voxelSize, (chunkVoxelResolution.z - 1) * voxelSize); Vector3 calculatedWorldOffset = useParentTransformAsOrigin ? transform.position + additionalWorldOffset : additionalWorldOffset; Vector3 relativePos = worldPos - calculatedWorldOffset; return new Vector3Int(Mathf.FloorToInt(relativePos.x / chunkWorldSize.x), Mathf.FloorToInt(relativePos.y / chunkWorldSize.y), Mathf.FloorToInt(relativePos.z / chunkWorldSize.z)); }
    private GameObject CreateMeshObject(NativeArray<float> sdfData, Vector3Int chunkCoord, string name, Material material) { var settings = new MarchingCubesCore.GenerationSettings { gridSize = chunkVoxelResolution, scale = new Vector3(voxelSize, voxelSize, voxelSize), isoLevel = 0.0f, worldSpaceOffset = Vector3.zero }; var meshData = MarchingCubesCore.GenerateMeshAdaptive(marchingCubesShader, sdfData, settings); sdfData.Dispose(); if (meshData.vertexCount < 3) return null; GameObject go = new GameObject(name); go.transform.SetParent(this.transform, false); go.transform.localPosition = Vector3.Scale(chunkCoord, new Vector3((chunkVoxelResolution.x - 1) * voxelSize, (chunkVoxelResolution.y - 1) * voxelSize, (chunkVoxelResolution.z - 1) * voxelSize)); go.layer = LayerMask.NameToLayer("Default"); Matrix4x4 localToWorldMatrix = go.transform.localToWorldMatrix; var colors = new Color[meshData.vertexCount]; for (int i = 0; i < meshData.vertexCount; i++) { Vector3 worldPos = localToWorldMatrix.MultiplyPoint3x4(meshData.vertices[i]); float t = Mathf.InverseLerp(gradientBottomY, gradientTopY, worldPos.y); colors[i] = Color.Lerp(deepColor, surfaceColor, t); } Mesh mesh = MeshUtility.CreateUnityMesh(meshData, colors, go.name); if (mesh == null || mesh.vertexCount == 0) { Destroy(go); return null; } go.AddComponent<MeshFilter>().mesh = mesh; go.AddComponent<MeshRenderer>().material = material; go.AddComponent<MeshCollider>().sharedMesh = mesh; return go; }
    private void FindShaderIfNeeded() { if (marchingCubesShader == null) { marchingCubesShader = Resources.Load<ComputeShader>("MarchingCubesCompute"); } }
    private void PatchChunkBorders(ref NativeArray<float> sdfData, Vector3Int chunkCoord) { int resX = chunkVoxelResolution.x, resY = chunkVoxelResolution.y, resZ = chunkVoxelResolution.z; if (chunkCoord.y == -worldDepthInChunks + 1) { for (int y = 0; y < patchThickness; y++) for (int x = 0; x < resX; x++) for (int z = 0; z < resZ; z++) { int index = x + y * resX + z * resX * resY; if (sdfData[index] > -1.0f) sdfData[index] = -1.0f; } } }
    #endregion
}
// Structs остаются без изменений
[BurstCompile] public struct GenerateChunkSDFJob : IJobParallelFor { [WriteOnly] public NativeArray<float> sdfResults; [ReadOnly] public Vector3Int chunkCoord; [ReadOnly] public Vector3Int chunkResolution; [ReadOnly] public float voxelSize; [ReadOnly] public CaveGenParams caveParams; [ReadOnly] public Vector3 worldOffset; [NativeDisableUnsafePtrRestriction] [ReadOnly] public unsafe float* gradientPtr; public unsafe void Execute(int index) { int x = index % chunkResolution.x; int y = (index / chunkResolution.x) % chunkResolution.y; int z = index / (chunkResolution.x * chunkResolution.y); float3 worldPos = new float3((chunkCoord.x * (chunkResolution.x - 1) + x) * voxelSize + worldOffset.x, (chunkCoord.y * (chunkResolution.y - 1) + y) * voxelSize + worldOffset.y, (chunkCoord.z * (chunkResolution.z - 1) + z) * voxelSize + worldOffset.z); sdfResults[index] = CaveGenerator.CalculateSDFAtPoint(worldPos.x, worldPos.y, worldPos.z, in caveParams, gradientPtr); } }
[BurstCompile] public struct CaveGenParams { public long seed; public float caveShapeFrequency; public float tunnelThickness; public float verticalityFactor; public float surfaceNoiseFrequency; public float surfaceNoiseAmplitude; public float surfaceIntegrityDepth; }