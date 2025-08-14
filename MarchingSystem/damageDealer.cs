using UnityEngine;
using UnityEngine.InputSystem;
using FishNet.Object;
using FishNet.Connection;

public class DamageDealer : NetworkBehaviour
{
    [Header("Параметры Повреждения")]
    [Tooltip("Радиус сферы повреждения в мировых координатах")]
    [Range(0.1f, 5.0f)]
    public float damageRadius = 0.5f;

    [Tooltip("Сила 'выедания' материала")]
    [Range(0.1f, 2.0f)]
    public float damageStrength = 0.8f;

    [Header("Визуализация")]
    [Tooltip("Эффект при попадании по разрушаемому объекту (стене и т.д.)")]
    public GameObject hitEffectPrefab;
    
    // --- НОВОЕ ПОЛЕ ---
    [Tooltip("Эффект при уничтожении обломка")]
    public GameObject debrisDestroyEffectPrefab;
    // -----------------

    [Tooltip("Как долго живет эффект (для обоих типов)")]
    public float hitEffectDuration = 2f;

    private Camera _playerCamera; 
    private int debrisLayerMask;

    private InputSystem_Actions _controls;
    private InputAction _primaryAction;
    private InputAction _secondaryAction;
    
    private PlayerController _playerController;

    void Awake()
    {
        _controls = new InputSystem_Actions();
        _playerController = GetComponent<PlayerController>();

        if (_playerController == null)
        {
            Debug.LogError($"[{gameObject.name}:{nameof(DamageDealer)}] Компонент 'PlayerController' НЕ НАЙДЕН.", this);
            enabled = false;
            return;
        }
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        if (!base.IsOwner) return;

        _playerCamera = _playerController.GetComponentInChildren<Camera>();
        if (_playerCamera == null)
        {
            Debug.LogError($"[{gameObject.name}:{nameof(DamageDealer)}] Камера не найдена.", this);
            enabled = false;
            return;
        }

        // Мы будем стрелять по всем слоям, кроме специальных (UI, вода и т.д.)
        // Логика внутри HandleDamageInput сама разберется, во что мы попали.
        debrisLayerMask = ~LayerMask.GetMask("UI", "Ignore Raycast", "Water");
        
        _primaryAction = _controls.Player.PrimaryAction;
        _primaryAction.Enable();
        _primaryAction.performed += OnPrimaryAction;
        
        _secondaryAction = _controls.Player.SecondaryAction;
        _secondaryAction.Enable();
        _secondaryAction.performed += OnSecondaryAction;
    }

    public override void OnStopClient()
    {
        base.OnStopClient();
        if (!base.IsOwner) return;

        if (_primaryAction != null) _primaryAction.performed -= OnPrimaryAction;
        if (_secondaryAction != null) _secondaryAction.performed -= OnSecondaryAction;
    }

    void OnDestroy()
    {
        if (_controls != null)
        {
            _controls.Dispose();
            _controls = null;
        }
    }

    private void OnPrimaryAction(InputAction.CallbackContext context)
    {
        HandleDamageInput();
    }

    private void OnSecondaryAction(InputAction.CallbackContext context)
    {
        HandleDebrisCleanup();
    }

    private void HandleDamageInput()
    {
        if (!IsSpawned || _playerCamera == null || DestructibleManager.Instance == null || !DestructibleManager.Instance.IsReady) return;

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = _playerCamera.ScreenPointToRay(mousePosition);

        if (Physics.Raycast(ray, out RaycastHit hit, 100f, debrisLayerMask))
        {
            // --- ИЗМЕНЕННАЯ ЛОГИКА ---
            // Мы больше не проверяем здесь на DebrisController.
            // Левый клик теперь наносит урон ВСЕМУ, включая обломки,
            // так как они тоже являются воксельными объектами.
            // Старый код, который мгновенно удалял обломки, удален.

            if (DestructibleManager.Instance.TryGetObjectId(hit.collider.gameObject, out ushort objectId))
            {
                DestructibleManager.Instance.RequestDamageServer(objectId, hit.point, damageRadius, damageStrength);
                if (hitEffectPrefab != null)
                {
                    RequestShowHitEffectServerRpc(hit.point, hit.normal);
                }
            }
        }
    }

    [ServerRpc]
    private void RequestShowHitEffectServerRpc(Vector3 hitPoint, Vector3 hitNormal)
    {
        ShowHitEffectObserversRpc(hitPoint, hitNormal);
    }

    [ObserversRpc]
    private void ShowHitEffectObserversRpc(Vector3 hitPoint, Vector3 hitNormal)
    {
        if (hitEffectPrefab == null) return;
        var effect = Instantiate(hitEffectPrefab, hitPoint, Quaternion.LookRotation(hitNormal));
        Destroy(effect, hitEffectDuration);
    }

    private void HandleDebrisCleanup()
    {
        if (!IsSpawned || _playerCamera == null) return;

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = _playerCamera.ScreenPointToRay(mousePosition);

        if (Physics.Raycast(ray, out RaycastHit hit, 100f, debrisLayerMask))
        {
            var debrisController = hit.collider.GetComponent<DebrisController>();
            if (debrisController != null)
            {
                // Правый клик по-прежнему используется для "уборки" обломков
                RequestDeleteDebrisServerRpc(debrisController.DebrisId, hit.point);
            }
        }
    }

    [ServerRpc(RequireOwnership = true)]
    private void RequestDeleteDebrisServerRpc(ulong debrisId, Vector3 hitPoint)
    {
        if (DestructibleManager.Instance != null)
        {
            DestructibleManager.Instance.Server_AddDeletedDebris(debrisId);
        }
        
        DeleteDebrisObserversRpc(debrisId, hitPoint);
    }

    [ObserversRpc]
    private void DeleteDebrisObserversRpc(ulong debrisId, Vector3 hitPoint)
    {
        var debrisController = DebrisController.FindDebrisById(debrisId);
        if (debrisController != null)
        {
            // --- НОВОЕ ---
            // Если нашли, спавним эффект в точке попадания
            if (debrisDestroyEffectPrefab != null)
            {
                var effect = Instantiate(debrisDestroyEffectPrefab, hitPoint, Quaternion.identity);
                Destroy(effect, hitEffectDuration);
            }
            // -------------
            
            // А затем уничтожаем сам обломок
            debrisController.DestroyDebris();
        }
    }
}