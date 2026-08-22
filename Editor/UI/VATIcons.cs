using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * All of these are Unity's own icons, so they match the rest of the editor
     * and there is nothing to ship alongside the package.
     *
     * Looked up through EditorGUIUtility.FindTexture rather than IconContent, which is the obvious call
     * and the wrong one: IconContent logs an error for a name it cannot find instead of handing back null.
     * Icon names are not API and do come and go between versions,
     * so one going missing has to be a header without a picture on it, not an error on every repaint.
     *
     * The dark skin keeps its own copy of each icon under a "d_" name, which IconContent would have
     * handled and FindTexture does not, so that is done here.
     *
     * Everything is cached, including the failures, because these are asked for on every repaint.
     */
    /// <summary>
    /// The little pictures on the baker's section headers and buttons.
    /// </summary>
    public static class VATIcons
    {

        private static readonly Dictionary<string, Texture> BY_NAME = new Dictionary<string, Texture>();

        private static bool _wasProSkin = EditorGUIUtility.isProSkin;

        // Every cached icon is the wrong one after a theme change, and nothing else would notice,
        // so the cache watches for it itself.
        [InitializeOnLoadMethod]
        private static void WatchSkin() => EditorApplication.update += ForgetOnSkinChange;

        private static void ForgetOnSkinChange()
        {
            if (_wasProSkin == EditorGUIUtility.isProSkin) return;

            _wasProSkin = EditorGUIUtility.isProSkin;
            Forget();
        }

        /// <summary>
        /// One icon by name. Never throws and never logs.
        /// </summary>
        /// <param name="name">Unity's own icon name, without any skin prefix.</param>
        /// <returns>The icon, or null when there is no such name in this version of Unity.</returns>
        public static Texture Named(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // TryGetValue rather than a truthiness test on the value: a name that does not resolve
            // caches as null and still counts as answered, or every repaint would ask again.
            if (BY_NAME.TryGetValue(name, out Texture cached)) return cached ? cached : null;

            Texture icon = EditorGUIUtility.isProSkin ? EditorGUIUtility.FindTexture($"d_{name}") : null;
            icon = icon ? icon : EditorGUIUtility.FindTexture(name);

            BY_NAME[name] = icon;
            return icon;
        }

        /// <summary>
        /// The first of these names that exists, so one that has gone missing in some version of Unity
        /// falls back to one that has not rather than to nothing.
        /// </summary>
        /// <param name="names">Icon names to try, best first.</param>
        /// <returns>The first icon that resolved, or null when none of them did.</returns>
        public static Texture First(params string[] names)
        {
            foreach (string name in names)
            {
                Texture icon = Named(name);
                if (icon) return icon;
            }

            return null;
        }

        /*
         * Icon NAMES are not API and several of the obvious ones do not resolve through FindTexture at
         * all, which is why two headings came out bare. A type is API, and Unity keeps a thumbnail for
         * every one it knows, so asking by type is both reliable and version proof.
         */
        /// <summary>
        /// The icon Unity uses for a type, which is what a heading about that type should carry.
        /// </summary>
        /// <param name="type">Any type Unity has an icon for, such as AnimationClip or Texture2D.</param>
        /// <returns>The icon, or null when Unity has none for it.</returns>
        public static Texture ForType(System.Type type)
        {
            if (type == null) return null;

            string key = $"type:{type.FullName}";
            if (BY_NAME.TryGetValue(key, out Texture cached)) return cached ? cached : null;

            Texture icon = AssetPreview.GetMiniTypeThumbnail(type);

            BY_NAME[key] = icon;
            return icon;
        }

        /// <summary>Drops the cache, so the next lookup resolves against the current skin.</summary>
        public static void Forget()
        {
            BY_NAME.Clear();
        }

    }
}
