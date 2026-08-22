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

        /// <summary>
        /// Starts ticking an animator, creating the driver object the first time one asks.
        /// </summary>
        /// <param name="animator">The animator to tick. Registering twice is harmless.</param>
        public static void Register(VATAnimator animator)
        {
            if (!animator) return;

            if (!_instance)
            {
                if (!Application.isPlaying) return;

                GameObject host = new GameObject("VAT Animator Driver") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(host);
                _instance = host.AddComponent<VATAnimatorDriver>();
            }

            if (!_instance._active.Contains(animator) && !_instance._pending.Contains(animator))
                _instance._pending.Add(animator);
        }

        /// <summary>
        /// Stops ticking an animator. Safe to call for one that was never registered.
        /// </summary>
        /// <param name="animator">The animator to drop.</param>
        public static void Unregister(VATAnimator animator)
        {
            if (!_instance || !animator) return;

            _instance._active.Remove(animator);
            _instance._pending.Remove(animator);
        }

        private void Update()
        {
            if (_pending.Count > 0)
            {
                _active.AddRange(_pending);
                _pending.Clear();
            }

            // Backwards so an animator that finishes can drop out mid-iteration.
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                VATAnimator animator = _active[i];
                if (!animator || !animator.Tick()) _active.RemoveAt(i);
            }
        }

    }
}
