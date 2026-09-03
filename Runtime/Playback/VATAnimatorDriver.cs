using System.Collections.Generic;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The whole point of VAT is not paying per-entity CPU every frame, so putting an Update() on every
     * VATAnimator would hand back a slice of what was just saved.
     * One driver with an explicit list keeps a crowd of settled loopers completely free.
     *
     * The host object is created on demand and never appears in a scene, so nothing has to be set up
     * by hand and nothing is left behind in a build that does not use one-shots or events.
     *
     * Ticking an animator can change who wants to be ticked, and the pass has to survive that. A one-shot reaching
     * its end raises ClipFinished and then starts its return clip from inside Tick, and starting a plain looping
     * clip with no events on it unregisters the animator, because there is nothing left to watch. So the ordinary
     * finish of PlayOnce("Attack", "Idle") took an entry out of the list the pass was walking, and the pass then
     * removed a second entry by an index that no longer meant anything, or ran off the end of the list. A listener
     * is free to do worse than that: freeze the animator, play something else, pool the entity it belongs to.
     *
     * Removal is therefore deferred while a pass is running. A slot being dropped is nulled instead of taken out,
     * so every index stays where it was for the whole pass, and the list is closed up once afterwards.
     */
    /// <summary>
    /// Ticks only the animators that have something to do, which is a one-shot in flight or events
    /// left to fire this cycle. Idle looping instances register nothing and cost nothing.
    /// </summary>
    [AddComponentMenu("")]
    public class VATAnimatorDriver : MonoBehaviour
    {

        private static VATAnimatorDriver _instance;

        private readonly List<VATAnimator> _active = new List<VATAnimator>();
        private readonly List<VATAnimator> _pending = new List<VATAnimator>();

        private bool _ticking;
        private bool _holesToClose;

        /// <summary>
        /// Starts ticking an animator, creating the driver object the first time one asks.
        /// </summary>
        /// <param name="animator">The animator to tick. Registering twice is harmless.</param>
        public static void Register(VATAnimator animator)
        {
            if (!animator) return;

            if (animator.DriverIndex >= 0 || animator.DriverPending) return;

            if (!_instance)
            {
                if (!Application.isPlaying) return;

                GameObject host = new GameObject("VAT Animator Driver") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(host);
                _instance = host.AddComponent<VATAnimatorDriver>();
            }

            animator.DriverPending = true;
            _instance._pending.Add(animator);
        }

        /// <summary>
        /// Stops ticking an animator. Safe to call for one that was never registered, and safe to call
        /// from inside a clip event or a ClipFinished handler while the driver is mid pass.
        /// </summary>
        /// <param name="animator">The animator to drop.</param>
        public static void Unregister(VATAnimator animator)
        {
            if (!_instance || !animator) return;

            if (animator.DriverPending)
            {
                _instance._pending.Remove(animator);
                animator.DriverPending = false;
            }

            int index = animator.DriverIndex;

            animator.DriverIndex = -1;

            /*
             * Checked rather than trusted, because a domain reload empties the driver while the animators that were
             * in it are still alive and still holding the index they had.
             */
            if (index < 0 || index >= _instance._active.Count || _instance._active[index] != animator) return;

            if (_instance._ticking)
            {
                /*
                 * Nulled rather than removed, so every other index still means what it meant when the pass started.
                 * This is where the exception was coming from: a one-shot ending inside Tick starts its return
                 * clip, and a return clip with no events on it unregisters from in here.
                 */
                _instance._active[index] = null;
                _instance._holesToClose = true;
                return;
            }

            _instance.RemoveActiveAt(index);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;

        private void Update()
        {
            if (_pending.Count > 0) DrainPending();

            if (_active.Count == 0) return;

            /*
             * Forwards, over a count read as it goes rather than cached, which is safe because nothing joins this
             * list during a pass: registering queues into pending and lands on the next frame. The only thing a
             * tick can do to this list is null one of its slots.
             */
            _ticking = true;

            for (int i = 0; i < _active.Count; i++)
            {
                VATAnimator animator = _active[i];

                if (!animator) continue;
                if (animator.Tick()) continue;

                animator.DriverIndex = -1;
                _active[i] = null;
                _holesToClose = true;
            }

            _ticking = false;

            if (_holesToClose) CloseHoles();
        }

        private void DrainPending()
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                VATAnimator animator = _pending[i];

                if (!animator) continue;

                animator.DriverPending = false;
                animator.DriverIndex = _active.Count;
                _active.Add(animator);
            }

            _pending.Clear();
        }

        /*
         * Swap back rather than a shift, so dropping one animator out of a crowd of them does not copy everything
         * behind it. Only ever called with no pass running, which is what makes moving the last entry into this
         * slot safe: during a pass that slot may already have been walked past.
         */
        private void RemoveActiveAt(int index)
        {
            int last = _active.Count - 1;
            VATAnimator moved = _active[last];

            _active[index] = moved;

            if (moved) moved.DriverIndex = index;

            _active.RemoveAt(last);
        }

        /*
         * One sweep after the pass rather than a removal per hole, because a crowd that all finishes its attack on
         * the same frame would otherwise shift the list once per body.
         */
        private void CloseHoles()
        {
            int write = 0;

            for (int read = 0; read < _active.Count; read++)
            {
                VATAnimator animator = _active[read];

                if (!animator) continue;

                _active[write] = animator;
                animator.DriverIndex = write;
                write++;
            }

            _active.RemoveRange(write, _active.Count - write);
            _holesToClose = false;
        }

    }
}
