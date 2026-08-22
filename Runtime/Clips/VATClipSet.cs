using System;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The shader can only address clips by index, and a material cannot store strings,
     * so without this the mapping from "slice 3" back to "Attack" lives nowhere at all.
     * The baker writes one of these next to the material and points VATAnimator at it,
     * which is what lets the inspector and gameplay code use clip names instead of numbers.
     */
    /// <summary>
    /// The record of which baked clip sits in which slice of a VAT texture array.
    /// </summary>
    public class VATClipSet : ScriptableObject
    {

        [Tooltip("Slice order. The index here is the value the shader's Clip Index expects.")]
        public VATClipEntry[] clips = new VATClipEntry[0];

        [Tooltip("Mesh sections baked alongside the clips. Empty when the bake had none.")]
        public VATSection[] sections = new VATSection[0];

        /// <summary>How many clips were baked into the texture array.</summary>
        public int Count => clips?.Length ?? 0;

        /// <summary>
        /// The clip name for a slice, safe to call with anything.
        /// </summary>
        /// <param name="index">Slice index.</param>
        /// <returns>The baked clip's name, or a readable stand-in when there is no entry.</returns>
        public string NameAt(int index) => index >= 0 && index < Count ? clips[index].name : $"slice {index}";

        /// <summary>
        /// Finds the slice holding a named clip, ignoring case.
        /// </summary>
        /// <param name="clipName">Name of the source AnimationClip that was baked.</param>
        /// <returns>The slice index, or -1 when no clip of that name was baked.</returns>
        public int IndexOf(string clipName)
        {
            for (int i = 0; i < Count; i++)
                if (string.Equals(clips[i].name, clipName, StringComparison.OrdinalIgnoreCase)) return i;

            return -1;
        }

        /// <summary>
        /// The markers baked into one clip.
        /// </summary>
        /// <param name="index">Slice index.</param>
        /// <returns>That clip's events, or null when the index is out of range.</returns>
        public VATClipEvent[] EventsAt(int index) => index >= 0 && index < Count ? clips[index].events : null;

        /// <summary>
        /// How long one cycle of a clip takes, which is what turns elapsed time into a normalized position.
        /// </summary>
        /// <param name="index">Slice index.</param>
        /// <returns>The clip's length in seconds, or 0 when the index is out of range.</returns>
        public float LengthAt(int index) => index >= 0 && index < Count ? clips[index].length : 0f;

        /// <summary>How many mesh sections this bake wrote.</summary>
        public int SectionCount => sections?.Length ?? 0;

        /// <summary>
        /// Finds a baked section by name, ignoring case.
        /// </summary>
        /// <param name="sectionName">Name given to the section in the baker.</param>
        /// <returns>The section, or null when this bake has no section of that name.</returns>
        public VATSection Section(string sectionName)
        {
            for (int i = 0; i < SectionCount; i++)
                if (string.Equals(sections[i].name, sectionName, StringComparison.OrdinalIgnoreCase))
                    return sections[i];

            return null;
        }

        /// <summary>
        /// Every section name, ready for a dropdown.
        /// </summary>
        /// <returns>One label per section, in bake order.</returns>
        public string[] SectionNames()
        {
            string[] names = new string[SectionCount];
            for (int i = 0; i < SectionCount; i++)
                names[i] = sections[i].name;

            return names;
        }

        /// <summary>
        /// Every clip name, numbered by slice, ready for a dropdown.
        /// </summary>
        /// <returns>One label per slice, in slice order.</returns>
        public string[] Names()
        {
            string[] names = new string[Count];
            for (int i = 0; i < Count; i++)
                names[i] = $"{i}  {clips[i].name}";

            return names;
        }

    }
}
