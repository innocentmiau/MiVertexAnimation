using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Kept in the baker rather than only on the generated clip set so that markers can be authored
     * before a bake exists, and so a re-bake knows to leave them alone.
     * The authored flag is what separates "this list is only what the source clip already had"
     * from "somebody edited this", which is the whole difference between importing and overriding.
     *
     * The start frame is recorded alongside because event times are normalized.
     * Changing Frame Step keeps every marker on the same moment, but changing Start Frame slides the
     * animation underneath them, and there is no way to correct for that after the fact - only to warn.
     */
    /// <summary>
    /// One clip's hand-authored event list inside the VAT Baker, and whether it overrides the source.
    /// </summary>
    [Serializable]
    public class VATAuthoredClipEvents
    {

        [Tooltip("Name of the source AnimationClip these events belong to.")]
        public string clipName;

        [Tooltip("True once the list was edited in the baker, which makes it win over the source clip.")]
        public bool authored;

        [Tooltip("Start Frame at the time the list was authored, used to warn when the range moved.")]
        public int authoredStartFrame;

        public List<VATClipEvent> events = new List<VATClipEvent>();

    }
}
