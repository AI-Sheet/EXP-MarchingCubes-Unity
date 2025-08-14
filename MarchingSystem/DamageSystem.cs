using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using Unity.Mathematics;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;

public struct DamageRequest
{
    public Vector3 worldPosition;
    public float radius;
    public float strength;
    public GameObject targetObject;
    public int debrisSeed;
}

[BurstCompile(CompileSynchronously = true, FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
struct ApplyDamageJob : IJobParallelFor
{
    public NativeArray<float> sdf;
    [ReadOnly] public int3 resolution;
    [ReadOnly] public float3 boundsMin;
    [ReadOnly] public float3 boundsSize;
    [ReadOnly] public float3 damageCenterLocal;
    [ReadOnly] public float damageRadius;
    [ReadOnly] public float damageStrength;

    public void Execute(int index)
    {
        int x = index % resolution.x;
        int y = (index / resolution.x) % resolution.y;
        int z = index / (resolution.x * resolution.y);

        float3 voxelSize = boundsSize / (resolution - 1);
        float3 voxelLocalPos = boundsMin + new float3(x, y, z) * voxelSize;

        float distSq = math.distancesq(voxelLocalPos, damageCenterLocal);
        float radiusSq = damageRadius * damageRadius;

        if (distSq <= radiusSq)
        {
            float normalizedDistance = math.sqrt(distSq) / damageRadius;
            float damageValue = (1.0f - normalizedDistance) * damageStrength;
            sdf[index] = math.max(sdf[index], damageValue);
        }
    }
}

public class DamageSystem : MonoBehaviour
{
    public static DamageSystem Instance { get; private set; }

    [Header("Производительность")]
    public int maxDamageJobsPerFrame = 5;

    [Header("Настройки Обломков")]
    public bool enableDebris = true;
    public int minDebrisSize = 20;
    [Range(100f, 5000f)]
    public float debrisDensity = 700f;
    [Range(10f, 500f)]
    public float debrisEjectionForce = 50f;

    private VoxelizationManager voxelManager;
    private readonly Queue<DamageRequest> damageQueue = new Queue<DamageRequest>();
    private readonly HashSet<GameObject> _lockedObjects = new HashSet<GameObject>();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Update()
    {
        ProcessDamageQueue();
    }

    /// <summary>
    /// Асинхронно обрабатывает все запросы на урон в очереди.
    /// Завершается, когда очередь пуста и все связанные операции (создание обломков) выполнены.
    /// </summary>
    public async UniTask ProcessAllQueuedDamageAsync()
    {
        // Мы будем работать, пока в очереди есть запросы ИЛИ пока есть заблокированные объекты (т.е. идущие корутины)
        while (damageQueue.Count > 0 || _lockedObjects.Count > 0)
        {
            ProcessDamageQueue();
            // Даем один кадр на обработку, чтобы не блокировать основной поток
            await UniTask.Yield();
        }
    }

    public void QueueDamageFromServer(DamageInfo info, GameObject target)
    {
        if (target == null) return;

        if (DebrisController.IsDebris(target))
        {
            return;
        }

        damageQueue.Enqueue(new DamageRequest
        {
            worldPosition = info.worldPosition,
            radius = info.radius,
            strength = info.strength,
            targetObject = target,
            debrisSeed = info.debrisSeed
        });
    }

    private void ProcessDamageQueue()
    {
        if (voxelManager == null)
        {
            voxelManager = VoxelizationManager.Instance;
            if (voxelManager == null) return;
        }

        int jobsStartedThisFrame = 0;
        int queueCount = damageQueue.Count;

        for (int i = 0; i < queueCount; i++)
        {
            if (jobsStartedThisFrame >= maxDamageJobsPerFrame) break;
            if (!damageQueue.TryDequeue(out var request)) continue;
            if (request.targetObject == null) continue;

            if (_lockedObjects.Contains(request.targetObject))
            {
                damageQueue.Enqueue(request);
                continue;
            }

            if (voxelManager.IsObjectReadyForDamage(request.targetObject))
            {
                _lockedObjects.Add(request.targetObject);
                ApplyDamage(request);
                jobsStartedThisFrame++;
            }
            else if (!voxelManager.IsObjectManaged(request.targetObject))
            {
                voxelManager.RegisterAndVoxelizeObject(request.targetObject);
                damageQueue.Enqueue(request);
            }
            else
            {
                damageQueue.Enqueue(request);
            }
        }
    }

    private void ApplyDamage(DamageRequest request)
    {
        if (request.strength == -1f)
        {
            voxelManager.RequestRemesh(request.targetObject);
            _lockedObjects.Remove(request.targetObject);
            return;
        }

        var voxelData = voxelManager.GetVoxelDataFor(request.targetObject);
        if (voxelData == null || !voxelData.sdfResults.IsCreated)
        {
            _lockedObjects.Remove(request.targetObject);
            return;
        }

        var rb = request.targetObject.GetComponent<Rigidbody>();
        if (rb != null && rb.IsSleeping()) rb.WakeUp();

        var resolution_int3 = new int3(voxelData.adaptiveResolution.x, voxelData.adaptiveResolution.y, voxelData.adaptiveResolution.z);

        // ИСПРАВЛЕНИЕ БАГА #1: Правильное вычисление локальной позиции урона
        // Используем стандартный Unity подход - он правильно работает для всех случаев
        Vector3 damageCenterLocal = request.targetObject.transform.InverseTransformPoint(request.worldPosition);

        var damageJob = new ApplyDamageJob
        {
            sdf = voxelData.sdfResults,
            resolution = resolution_int3,
            boundsMin = voxelData.finalBounds.min,
            boundsSize = voxelData.finalBounds.size,
            damageCenterLocal = damageCenterLocal,
            damageRadius = request.radius,
            damageStrength = request.strength
        };

        JobHandle handle = damageJob.Schedule(voxelData.sdfResults.Length, 64);
        handle.Complete();

        if (enableDebris)
        {
            StartCoroutine(ProcessFragmentation(voxelData, request.debrisSeed));
        }
        else
        {
            voxelManager.RequestRemesh(request.targetObject);
            _lockedObjects.Remove(request.targetObject);
        }
    }

    private IEnumerator ProcessFragmentation(VoxelizationManager.VoxelData sourceData, int debrisSeed)
    {
        try
        {
            // Находим все отделившиеся от основного тела "острова" вокселей.
            var debrisComponents = DebrisGenerator.FindFragmentComponents(sourceData);

            // Если ничего не отделилось, просто запрашиваем обновление меша (если нужно) и выходим.
            if (debrisComponents.Count == 0)
            {
                voxelManager.RequestRemesh(sourceData.gameObject);
                yield break;
            }

            var jobHandles = new List<JobHandle>();
            var validPreparedData = new List<PreparedDebris>();
            var validComponents = new List<DebrisGenerator.VoxelComponent>();

            // --- НОВОЕ ---
            // Список для вокселей из очень маленьких "островков", которые не станут обломками.
            var voxelsToSimplyRemove = new List<int>();
            // -------------

            foreach (var component in debrisComponents)
            {
                // Если компонент достаточно большой, он становится полноценным обломком.
                if (component.voxelIndices.Count >= minDebrisSize)
                {
                    var handle = DebrisGenerator.PrepareDebrisAsync(component, sourceData, debrisDensity, out var preparedDebris);
                    if (preparedDebris.bakedMesh != null)
                    {
                        jobHandles.Add(handle);
                        validPreparedData.Add(preparedDebris);
                        validComponents.Add(component);
                    }
                }
                // --- НОВОЕ ---
                // Если компонент слишком маленький, мы не создаем из него объект,
                // а просто добавляем его воксели в список на удаление.
                else
                {
                    voxelsToSimplyRemove.AddRange(component.voxelIndices);
                }
                // -------------
            }

            // Ждем завершения всех задач по созданию мешей для больших обломков.
            if (jobHandles.Count > 0)
            {
                foreach (var handle in jobHandles)
                {
                    while (!handle.IsCompleted) yield return null;
                    handle.Complete();
                }
            }

            // --- ИЗМЕНЕННАЯ ЛОГИКА ---
            // Проверяем, есть ли у нас либо большие обломки для создания, ЛИБО маленькие островки для удаления.
            if (validPreparedData.Count > 0 || voxelsToSimplyRemove.Count > 0)
            {
                // "Вырезаем" из исходного меша воксели, которые стали большими обломками.
                foreach (var component in validComponents)
                {
                    foreach (int index in component.voxelIndices) sourceData.sdfResults[index] = 1.0f;
                }

                // "Стираем" из исходного меша воксели маленьких островков.
                foreach (int index in voxelsToSimplyRemove)
                {
                    sourceData.sdfResults[index] = 1.0f;
                }

                // Создаем игровые объекты для больших обломков.
                foreach (var preparedDebris in validPreparedData)
                {
                    DebrisGenerator.CreateDebrisFromPreparedData(preparedDebris, debrisEjectionForce, debrisSeed);
                }

                // Запрашиваем немедленное обновление исходного меша, так как мы изменили его SDF данные.
                voxelManager.RequestRemeshImmediate(sourceData);
            }
            else
            {
                // Если в итоге нечего было ни создавать, ни удалять, просто ставим меш в очередь на обычное обновление.
                voxelManager.RequestRemesh(sourceData.gameObject);
            }
            // -------------------------
        }
        finally
        {
            // В любом случае, в конце разблокируем объект для следующих повреждений.
            if (sourceData != null && sourceData.gameObject != null)
            {
                _lockedObjects.Remove(sourceData.gameObject);
            }
        }
    }
}