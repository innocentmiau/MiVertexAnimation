using System;
using UnityEngine;
using UnityEngine.Events;

namespace MiVertexAnimation
{

    /*
     * Matched by name rather than by index, because a re-bake can reorder slices and renumber events
     * while the names stay where they are. An index would quietly start firing the wrong response.
     *
     * Nothing is passed to the response. A UnityEvent can carry one argument, but only of a type chosen
     * when the class is written, and a marker carries three, so somebody would still be picking between
     * them in code. The name is the part that identifies the moment; scripts that need the parameters
     * subscribe to VATAnimator.ClipEventFired directly and get all three.
     */
    /// <summary>
    /// One named marker wired to something to do about it, without writing any code.
    /// </summary>
    [Serializable]
    public class VATEventBinding
    {

        [Tooltip("Name of the marker this answers to. Filled in from the clip set, not typed by hand.")]
        public string eventName;

        [Tooltip("What happens when that marker is reached.")]
        public UnityEvent response = new UnityEvent();

        /// <summary>
        /// Whether this binding answers to a name.
        /// </summary>
        /// <param name="name">The name the animator raised.</param>
        /// <returns>True when the names match, ignoring case.</returns>
        public bool Matches(string name) => string.Equals(eventName, name, StringComparison.OrdinalIgnoreCase);

    }
}
