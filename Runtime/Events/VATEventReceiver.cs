using System.Collections.Generic;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The bridge between VATAnimator's C# events and the inspector. Subscribing to ClipEventFired takes
     * a script; this takes a component and a drag, so somebody who already has an enemy script can wire
     * an attack's hit frame to it without writing anything new.
     *
     * Bindings whose name is no longer in the clip set are kept rather than removed. Deleting them would
     * mean a re-bake silently throws away work somebody did in the inspector, and a name that vanished
     * is far more likely to be a clip that was temporarily left out of a bake than one gone for good.
     * The inspector marks them instead.
     */
    /// <summary>
    /// Raises UnityEvents when a baked animation reaches its markers, or when a one-shot clip ends.
    /// </summary>
    [AddComponentMenu("Mi/Vertex Animation/VAT Event Receiver")]
    [DisallowMultipleComponent]
    public class VATEventReceiver : MonoBehaviour
    {

        [SerializeField]
        [Tooltip("The animator to listen to. Picked up from this object automatically when there is one.")]
        private VATAnimator animator;

        [SerializeField]
        [Tooltip("One entry per marker name across every baked clip.")]
        private List<VATEventBinding> markers = new List<VATEventBinding>();

        [SerializeField]
        [Tooltip("Added by hand, one per clip you care about the end of. Raised when a clip played with " +
                 "PlayOnce reaches its last frame.")]
        private List<VATEventBinding> clipFinished = new List<VATEventBinding>();

        /// <summary>The animator these bindings listen to.</summary>
        public VATAnimator Animator => animator;

        private void Reset()
        {
            animator = GetComponent<VATAnimator>();
            SyncWithClipSet();
        }

        private void OnValidate()
        {
            if (!animator) animator = GetComponent<VATAnimator>();

            SyncWithClipSet();
        }

        private void OnEnable()
        {
            if (!animator) animator = GetComponent<VATAnimator>();
            if (!animator) return;

            animator.ClipEventFired += OnClipEventFired;
            animator.ClipFinished += OnClipFinished;
        }

        private void OnDisable()
        {
            if (!animator) return;

            animator.ClipEventFired -= OnClipEventFired;
            animator.ClipFinished -= OnClipFinished;
        }

        /*
         * Markers only. Every clip has an end, so filling in a row per clip would mean ten UnityEvents
         * on a ten clip rig before anybody asked for one, and almost all of them left empty.
         * Markers are different: there are only ever as many as somebody deliberately placed, and the
         * whole reason for this component is to have somewhere to wire them.
         *
         * Only ever adds. Anything already in the list keeps whatever was wired to it, including entries
         * whose name is no longer in the clip set.
         */
        /// <summary>
        /// Gives every marker name in the clip set a binding to be wired to.
        /// </summary>
        public void SyncWithClipSet()
        {
            VATClipSet set = animator ? animator.ClipSet : null;
            if (!set) return;

            for (int i = 0; i < set.Count; i++)
            {
                VATClipEvent[] events = set.EventsAt(i);
                if (events == null) continue;

                for (int e = 0; e < events.Length; e++)
                    AddMissing(markers, events[e].name);
            }
        }

        /// <summary>
        /// Adds a Clip Finished binding for one clip, doing nothing when there is already one.
        /// </summary>
        /// <param name="clipName">Name of the baked clip whose end should raise an event.</param>
        public void AddClipFinished(string clipName)
        {
            AddMissing(clipFinished, clipName);
        }

        private static void AddMissing(List<VATEventBinding> bindings, string name)
        {
            if (string.IsNullOrEmpty(name)) return;

            foreach (VATEventBinding binding in bindings)
                if (binding.Matches(name)) return;

            bindings.Add(new VATEventBinding { eventName = name });
        }

        private void OnClipEventFired(VATAnimator source, VATClipEvent clipEvent)
        {
            Raise(markers, clipEvent.name);
        }

        private void OnClipFinished(VATAnimator source, string clipName)
        {
            Raise(clipFinished, clipName);
        }

        private static void Raise(List<VATEventBinding> bindings, string name)
        {
            for (int i = 0; i < bindings.Count; i++)
                if (bindings[i].Matches(name)) bindings[i].response.Invoke();
        }

    }
}
