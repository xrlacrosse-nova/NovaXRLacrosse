using UnityEngine;

public class ProceduralRunAnimation : MonoBehaviour
{
    [Header("Straight Run")]
    public float runSpeed = 3f;
    public float runDuration = 3f;

    private float _runTimer = 0f;
    private bool _running = true;

    void Update()
    {
        if (!_running) return;

        _runTimer += Time.deltaTime;
        transform.position += transform.forward * runSpeed * Time.deltaTime;

        if (_runTimer >= runDuration)
        {
            _running = false;
            Debug.Log("[ProceduralRunAnimation] Run complete.");
        }
    }
}