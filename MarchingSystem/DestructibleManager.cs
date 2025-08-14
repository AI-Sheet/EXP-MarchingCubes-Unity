using UnityEngine;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FishNet.Connection;
using Cysharp.Threading.Tasks;

[RequireComponent(typeof(NetworkObject))]
public class DestructibleManager : NetworkBehaviour
{
    public static DestructibleManager Instance { get; private set; }
    private const string LOG_PREFIX = "[DestructibleManager]";

    // Синхронизируемые данные
    private readonly SyncList<DamageEvent> _damageHistory = new SyncList<DamageEvent>();
    private readonly SyncList<ulong> _deletedDebrisIds = new SyncList<ulong>();
    
    // Состояние сервера для текущей сессии (убрали static для изоляции сессий)
    private readonly HashSet<ulong> _serverDeletedDebris = new HashSet<ulong>();
    private bool _serverStateInitialized = false;

    // Состояние клиента
    private List<GameObject> _indexedObjects = new List<GameObject>();
    private readonly Dictionary<GameObject, ushort> _reverseIndexedObjects = new Dictionary<GameObject, ushort>();
    private readonly HashSet<ulong> _clientDeletedDebrisCache = new HashSet<ulong>();
    public bool IsReady { get; private set; } = false;
    private bool _worldStateReceived = false;

    #region Lifecycle & Network Callbacks
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        // Теперь эта проверка относится к экземпляру, а не ко всему приложению
        if (!_serverStateInitialized)
        {
            _serverDeletedDebris.Clear(); // Очищаем состояние для новой сессии
            _serverStateInitialized = true;
            Debug.Log($"{LOG_PREFIX} [Server] Initialized instance-specific server state.");
        }
    }

    public override void OnStopServer()
    {
        base.OnStopServer();
        _serverDeletedDebris.Clear();
        _serverStateInitialized = false;
        Debug.Log($"{LOG_PREFIX} [Server] Cleared server state on stop.");
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        Debug.Log($"{LOG_PREFIX} [Client] OnStartClient called. Requesting world state...");
        InitializeDestructibleObjects();
        SubscribeToNetworkEvents();
        RequestWorldStateServerRpc();
    }

    public override void OnStopClient()
    {
        base.OnStopClient();
        UnsubscribeFromNetworkEvents();
        ResetClientState();
    }
    
    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
    #endregion

    #region Client Initialization
    public void InitializeDestructibleObjects()
    {
        _indexedObjects.Clear();
        _reverseIndexedObjects.Clear();
        var destructibles = new List<GameObject>();
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var layer = LayerMask.NameToLayer(VoxelizationManager.Instance.voxelLayerName);
        foreach (var root in scene.GetRootGameObjects())
            destructibles.AddRange(root.GetComponentsInChildren<Collider>(true).Where(col => col.gameObject.layer == layer).Select(col => col.gameObject));
        
        _indexedObjects = destructibles.Distinct().ToList();
        for (ushort i = 0; i < _indexedObjects.Count; i++)
            _reverseIndexedObjects[_indexedObjects[i]] = i;

        Debug.Log($"{LOG_PREFIX} [Client] Scanned '{scene.name}' and found {_indexedObjects.Count} destructible objects.");
    }

    private void SubscribeToNetworkEvents()
    {
        _damageHistory.OnChange += OnDamageHistoryChanged;
        _deletedDebrisIds.OnChange += OnDeletedDebrisChanged;
    }

    private void UnsubscribeFromNetworkEvents()
    {
        _damageHistory.OnChange -= OnDamageHistoryChanged;
        _deletedDebrisIds.OnChange -= OnDeletedDebrisChanged;
    }

    private void ResetClientState()
    {
        _indexedObjects.Clear();
        _reverseIndexedObjects.Clear();
        _clientDeletedDebrisCache.Clear();
        IsReady = false;
        _worldStateReceived = false;
    }
    #endregion

    #region Network Event Handlers & Core Logic
    private void OnDamageHistoryChanged(SyncListOperation op, int index, DamageEvent oldItem, DamageEvent newItem, bool asServer)
    {
        if (asServer || op != SyncListOperation.Add) return;
        if (newItem.objectId >= _indexedObjects.Count) return;

        GameObject target = _indexedObjects[newItem.objectId];
        if (target != null && DamageSystem.Instance != null)
        {
            DamageSystem.Instance.QueueDamageFromServer(newItem.damage, target);
        }
    }

    private void OnDeletedDebrisChanged(SyncListOperation op, int index, ulong oldItem, ulong newItem, bool asServer)
    {
        if (asServer || op != SyncListOperation.Add) return;

        _clientDeletedDebrisCache.Add(newItem);
        if (_worldStateReceived) 
        {
            var debrisController = DebrisController.FindDebrisById(newItem);
            if (debrisController != null) debrisController.DestroyDebris();
        }
    }

    public bool IsDebrisDeleted(ulong debrisId)
    {
        return _clientDeletedDebrisCache.Contains(debrisId);
    }
    
    public bool TryGetObjectId(GameObject obj, out ushort id)
    {
        return _reverseIndexedObjects.TryGetValue(obj, out id);
    }

    public List<GameObject> GetIndexedObjects()
    {
        return _indexedObjects;
    }
    #endregion

    #region Server Logic & RPCs
    [Server]
    public void Server_AddDeletedDebris(ulong debrisId)
    {
        if (_serverDeletedDebris.Add(debrisId))
        {
            _deletedDebrisIds.Add(debrisId);
            Debug.Log($"{LOG_PREFIX} [Server] Added debris ID:{debrisId:X8} to session state. Total: {_serverDeletedDebris.Count}");
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestDamageServer(ushort objectId, Vector3 worldPos, float radius, float strength)
    {
        int seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        _damageHistory.Add(new DamageEvent(objectId, new DamageInfo { worldPosition = worldPos, radius = radius, strength = strength, debrisSeed = seed }));
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestWorldStateServerRpc(NetworkConnection conn = null)
    {
        Debug.Log($"{LOG_PREFIX} [Server] Client {conn.ClientId} requested world state.");
        
        List<ulong> deletedIdsList = new List<ulong>(_serverDeletedDebris);
        Debug.Log($"{LOG_PREFIX} [Server] Sending {deletedIdsList.Count} deleted debris IDs to client {conn.ClientId}.");
        
        TargetWorldStateSent(conn, deletedIdsList);
    }

    [TargetRpc]
    private void TargetWorldStateSent(NetworkConnection conn, List<ulong> deletedIds)
    {
        Debug.Log($"{LOG_PREFIX} [Client] Full world state package received. Applying {deletedIds.Count} deleted debris IDs.");
        
        _clientDeletedDebrisCache.Clear();
        foreach (var id in deletedIds)
            _clientDeletedDebrisCache.Add(id);
        
        _worldStateReceived = true;
        
        InitializeClientStateAsync().Forget();
    }
    
    private async UniTask InitializeClientStateAsync()
    {
        if (DamageSystem.Instance != null)
             await DamageSystem.Instance.ProcessAllQueuedDamageAsync();
       
        Debug.Log($"{LOG_PREFIX} [Client] Historical damage processed.");
        
        IsReady = true;
        Debug.Log($"{LOG_PREFIX} [Client] State synchronization complete. Manager is now ready.");
    }
    #endregion
}