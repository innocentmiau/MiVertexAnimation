using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * A VAT mesh keeps its bind-pose bounds, but the shader moves vertices anywhere the animation goes.
     * Without this, Unity culls the renderer against the wrong box,
     * which shows up as objects and their shadows popping out at the screen edges.
     *
     * The baker measures the real extents across every baked frame and fills these in,
     * so nothing has to be guessed or padded by hand.
     */
    /// <summary>
    /// Replaces a baked VAT renderer's culling bounds with a box covering the whole animation.
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class VATBoundsOverride : MonoBehaviour
    {

        [Tooltip("Object-space bounds covering every frame of the baked animation.")]
        public Bounds bounds = new Bounds(Vector3.zero, Vector3.one);

        private void OnEnable()
        {
            Apply();
        }

        private void OnValidate()
        {
            Apply();
        }

        /// <summary>
        /// Writes the bounds onto the renderer. Called automatically whenever they change.
        /// </summary>
        public void Apply()
        {
            Renderer target = GetComponent<Renderer>();
            if (!target) return;

            target.localBounds = bounds;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
        }

    }
}
