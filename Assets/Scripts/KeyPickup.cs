using UnityEngine;

// Ключ от выходной двери: лежит на месте гибели последнего врага. Игрок подбирает его, коснувшись
// (триггер). Слегка пульсирует, чтобы его было видно.
public class KeyPickup : MonoBehaviour
{
    public float pulseSpeed = 4f;
    public float pulseAmount = 0.12f;

    private Vector3 baseScale;

    void Awake()
    {
        baseScale = transform.localScale;
    }

    void Update()
    {
        transform.localScale = baseScale * (1f + Mathf.Sin(Time.unscaledTime * pulseSpeed) * pulseAmount);
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.GetComponentInParent<PlayerHealth>() == null) return;
        if (LevelManager.Instance != null) LevelManager.Instance.PickUpKey();
        Destroy(gameObject);
    }
}
