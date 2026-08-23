using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * LOOK_AT is the one sample mode that cannot be shown by a still scene: it exists to demonstrate
     * following something that moves, which is the case a timed transition cannot describe in advance.
     * A target that just sits there makes it look identical to every other mode.
     */
    /// <summary>Walks a transform in a circle, to give the section sample something to follow.</summary>
    [AddComponentMenu("MiVertexAnimation/Samples/VAT Demo Orbit")]
    public class VATDemoOrbit : MonoBehaviour
    {

        [Tooltip("Metres from the starting position.")]
        [SerializeField] private float radius = 6f;

        [Tooltip("Laps per second.")]
        [SerializeField] private float speed = .15f;

        [Tooltip("How far it rises and falls over one lap.")]
        [SerializeField] private float bob = 1f;

        private Vector3 _centre;

        private void OnEnable() => _centre = transform.position;

        private void Update()
        {
            float phase = Time.time * speed * Mathf.PI * 2f;

            transform.position = _centre + new Vector3(
                Mathf.Cos(phase) * radius,
                Mathf.Sin(phase * 2f) * bob,
                Mathf.Sin(phase) * radius);
        }

        /*
         * Always drawn, not only when selected. This object is what the crowd is looking at, and an
         * empty transform with no renderer makes the whole demo read as characters turning at nothing.
         * A gizmo rather than a mesh so the sample carries no material of its own - which means the
         * Scene view, not the Game view, is where this demo is worth watching.
         */
        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, .8f, .3f, 1f);
            Gizmos.DrawSphere(transform.position, .25f);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, .8f, .3f, .5f);
            Gizmos.DrawWireSphere(Application.isPlaying ? _centre : transform.position, radius);
        }

    }
}
