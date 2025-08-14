using UnityEngine;
using System.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(Rigidbody))]
public class DebrisController : MonoBehaviour
{
    [Header("Настройки оптимизации")]
    [Tooltip("Как часто (в секундах) проверять, заснул ли объект.")]
    [SerializeField] private float sleepCheckInterval = 1.0f;
    [Tooltip("Порог скорости, ниже которого объект считается неподвижным.")]
    [SerializeField] private float sleepThreshold = 0.05f;
    [Tooltip("Сколько секунд объект должен быть неподвижным, чтобы заснуть.")]
    [SerializeField] private float sleepTime = 2.0f;
    
    public ulong DebrisId { get; private set; }
    public bool IsOptimized => _isOptimized;
    
    private Rigidbody _rigidbody;
    private float _sleepTimer;
    private bool _isOptimized = false;
    private Coroutine _sleepCheckCoroutine;

    private static readonly Dictionary<ulong, DebrisController> AllDebris = new Dictionary<ulong, DebrisController>();

    public void Initialize(ulong debrisId)
    {
        DebrisId = debrisId;
        _rigidbody = GetComponent<Rigidbody>();

        if (DebrisId != 0 && !AllDebris.ContainsKey(DebrisId))
        {
            AllDebris.Add(DebrisId, this);
        }
        
        _sleepCheckCoroutine = StartCoroutine(CheckSleepRoutine());
    }

    private void OnDestroy()
    {
        if (_sleepCheckCoroutine != null)
        {
            StopCoroutine(_sleepCheckCoroutine);
        }
        if (DebrisId != 0 && AllDebris.ContainsKey(DebrisId))
        {
            AllDebris.Remove(DebrisId);
        }
    }

    private IEnumerator CheckSleepRoutine()
    {
        var waitInterval = new WaitForSeconds(sleepCheckInterval);
        
        while (!_isOptimized)
        {
            yield return waitInterval;
            
            if (_rigidbody == null) yield break;
            
            if (_rigidbody.IsSleeping() || 
               (_rigidbody.linearVelocity.magnitude < sleepThreshold && 
                _rigidbody.angularVelocity.magnitude < sleepThreshold))
            {
                _sleepTimer += sleepCheckInterval;
                
                if (_sleepTimer >= sleepTime)
                {
                    OptimizeDebris();
                    yield break; // Выходим из корутины после оптимизации
                }
            }
            else
            {
                _sleepTimer = 0f;
            }
        }
    }

    private void OptimizeDebris()
    {
        if (_isOptimized || _rigidbody == null) return;
        
        Debug.Log($"[DebrisController] Оптимизация обломка ID:{DebrisId:X8}");
        
        // 1. Делаем объект статичным для физики
        _rigidbody.isKinematic = true;
        
        // --- НОВАЯ, УПРОЩЕННАЯ И КОРРЕКТНАЯ ЛОГИКА ---
        if (TryGetComponent<MeshCollider>(out var meshCollider))
        {
            // Получаем меш из коллайдера
            Mesh sourceMesh = meshCollider.sharedMesh;
            if (sourceMesh != null)
            {
                // Уничтожаем дорогой MeshCollider
                Destroy(meshCollider);

                // Добавляем дешевый BoxCollider
                var boxCollider = gameObject.AddComponent<BoxCollider>();

                // Используем локальные bounds самого меша. Они уже в правильной системе координат.
                boxCollider.center = sourceMesh.bounds.center;
                boxCollider.size = sourceMesh.bounds.size;
            }
        }
        // ------------------------------------------
        
        _isOptimized = true;
    }

    public static bool IsDebris(GameObject obj)
    {
        return obj != null && obj.CompareTag("Debris");
    }

    public static DebrisController FindDebrisById(ulong debrisId)
    {
        AllDebris.TryGetValue(debrisId, out var debris);
        return debris;
    }

    public void DestroyDebris()
    {
        Destroy(gameObject);
    }
}