using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The scene ships without a baked prefab in it, because a bake is the one thing a package cannot
     * carry for you: the textures belong to whichever mesh and clips went in, and the model in Source/
     * is only the one that happens to be in the box. So the scene holds this instead. Bake something -
     * that model or your own - drop the prefab in one field, and the demo is running on your character.
     *
     * Spawning happens in play mode only. Building the grid at edit time would leave objects behind in
     * the scene to be saved by accident, and what the grid is here to show is what it costs while
     * running, which is nothing an empty scene can demonstrate.
     */
    /// <summary>Spawns a baked VAT prefab as a grid and points the section sample at a target.</summary>
    [AddComponentMenu("MiVertexAnimation/Samples/VAT Demo Rig")]
    public class VATDemoRig : MonoBehaviour
    {

        [Tooltip("Any prefab the baker produced. Bake the model in Source/, or one of your own, " +
                 "and drop the prefab here.")]
        [SerializeField] private GameObject vatPrefab;

        [Header("Crowd")]
        [Tooltip("Copies across.")]
        [SerializeField, Min(1)] private int columns = 5;

        [Tooltip("Copies deep.")]
        [SerializeField, Min(1)] private int rows = 5;

        [Tooltip("Metres between copies.")]
        [SerializeField] private float spacing = 2f;

        [Header("Sections")]
        [Tooltip("Give every copy the section sample. Does nothing unless the bake had sections in it.")]
        [SerializeField] private bool driveSections = true;

        [Tooltip("What the sections turn towards. The orbiting marker in the scene, or the player.")]
        [SerializeField] private Transform lookAtTarget;

        /// <summary>How many copies the grid is currently set to spawn.</summary>
        public int Count => columns * rows;

        /// <summary>True when there is a prefab to spawn, which is the one thing this needs.</summary>
        public bool Ready => vatPrefab;

        private void Start()
        {
            if (!vatPrefab)
            {
                Debug.LogWarning(
                    $"{name}: no VAT Prefab assigned, so the demo scene is empty. Bake the model in " +
                    "the sample's Source folder with Tools > MiVertexAnimation > Baker, then drop the " +
                    "prefab it wrote onto this component.", this);
                return;
            }

            Spawn();
        }

        /*
         * Centred on this object rather than growing away from it, so moving the rig moves the crowd
         * and the camera framing in the scene still holds at any grid size.
         */
        private void Spawn()
        {
            Vector3 corner = new Vector3((columns - 1) * spacing, 0f, (rows - 1) * spacing) * -.5f;

            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < columns; x++)
                {
                    Vector3 offset = corner + new Vector3(x * spacing, 0f, z * spacing);
                    GameObject copy = Instantiate(vatPrefab, transform.position + offset,
                        transform.rotation, transform);

                    copy.name = $"{vatPrefab.name} {(z * columns) + x}";
                    if (driveSections) AttachSample(copy);
                }
            }
        }

        /*
         * The sample needs the driver the bake put on the prefab, so a bake without sections simply
         * gets no sample rather than a component logging at every copy about a driver that was never
         * going to be there.
         */
        private void AttachSample(GameObject copy)
        {
            VATSectionDriver driver = copy.GetComponent<VATSectionDriver>();
            if (!driver) return;

            VATSectionSample sample = copy.GetComponent<VATSectionSample>();
            if (!sample) sample = copy.AddComponent<VATSectionSample>();

            sample.SetTarget(lookAtTarget);
        }

        // Draws where the crowd will stand, so the grid can be sized against the camera framing
        // without entering play mode to find out.
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(.4f, .8f, 1f, .5f);
            Gizmos.DrawWireCube(transform.position,
                new Vector3(columns * spacing, .1f, rows * spacing));
        }

    }
}
