using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * A bake writes one mesh per LOD level, and the number of levels can go down as easily as up.
     * Drop from three levels to two, or turn the LOD Group section off entirely, and the prefab stops
     * referring to the meshes it no longer needs - but the assets stay on disk, because nothing is
     * watching them.
     *
     * Deliberately a button rather than something the bake does on its own. A bake that deleted assets
     * would eventually delete one somebody had used somewhere this package cannot see, and the day it
     * happens is the day the tool becomes untrustworthy.
     */
    /// <summary>Finds baked meshes that nothing refers to any more.</summary>
    public static class VATMeshMaintenance
    {

        // Only the shapes the baker writes: Name_Mesh and Name_LOD0, Name_LOD1 and so on.
        private const string MESH_SUFFIX = "_Mesh";
        private const string LOD_MARKER = "_LOD";

        /// <summary>
        /// Every baked mesh asset no prefab or scene refers to.
        /// </summary>
        /// <returns>Asset paths, safe to delete, in no particular order.</returns>
        public static List<string> FindOrphans()
        {
            List<string> candidates = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets("t:Mesh"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // Only standalone .asset meshes. A mesh inside an FBX is not ours to judge.
                if (path.EndsWith(".asset", StringComparison.Ordinal) && IsBakedName(path))
                    candidates.Add(path);
            }

            if (candidates.Count == 0) return candidates;

            HashSet<string> referenced = CollectReferenced();
            List<string> orphans = new List<string>();

            foreach (string path in candidates)
                if (!referenced.Contains(path)) orphans.Add(path);

            return orphans;
        }

        /*
         * Walked from the holders inward rather than asking each mesh who refers to it, because Unity
         * only answers the first question. Prefabs and scenes are the only things that can be holding a
         * baked mesh - a material references textures, not meshes.
         */
        private static HashSet<string> CollectReferenced()
        {
            HashSet<string> referenced = new HashSet<string>();
            List<string> holders = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
                holders.Add(AssetDatabase.GUIDToAssetPath(guid));

            foreach (string guid in AssetDatabase.FindAssets("t:SceneAsset"))
                holders.Add(AssetDatabase.GUIDToAssetPath(guid));

            try
            {
                for (int i = 0; i < holders.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Vertex Animation",
                        $"Checking what still uses baked meshes  {i + 1}/{holders.Count}",
                        (float)i / Mathf.Max(1, holders.Count));

                    foreach (string dependency in AssetDatabase.GetDependencies(holders[i], true))
                        referenced.Add(dependency);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return referenced;
        }

        /// <summary>
        /// Finds unreferenced baked meshes and offers to delete them.
        /// </summary>
        public static void DeleteOrphans()
        {
            List<string> orphans = FindOrphans();

            if (orphans.Count == 0)
            {
                EditorUtility.DisplayDialog("Vertex Animation",
                    "Every baked mesh in the project is still used by a prefab or a scene.", "OK");
                return;
            }

            long bytes = 0L;
            foreach (string path in orphans)
            {
                FileInfo file = new FileInfo(path);
                if (file.Exists) bytes += file.Length;
            }

            string size = EditorUtility.FormatBytes(bytes);
            string listed = string.Join("\n", orphans.GetRange(0, Mathf.Min(orphans.Count, 12)));
            if (orphans.Count > 12) listed += $"\n... and {orphans.Count - 12} more";

            if (!EditorUtility.DisplayDialog("Delete Unused Baked Meshes",
                    $"{orphans.Count} baked mesh(es) are not referenced by any prefab or scene, " +
                    $"totalling {size}:\n\n{listed}\n\nDeleting them cannot be undone.",
                    $"Delete {orphans.Count}", "Cancel"))
                return;

            int deleted = 0;
            foreach (string path in orphans)
                if (AssetDatabase.DeleteAsset(path)) deleted++;

            AssetDatabase.Refresh();
            Debug.Log($"[VAT] Deleted {deleted} unused baked mesh(es), {size}.");
        }

        private static bool IsBakedName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);

            if (name.EndsWith(MESH_SUFFIX, StringComparison.Ordinal)) return true;

            int marker = name.LastIndexOf(LOD_MARKER, StringComparison.Ordinal);
            if (marker < 0) return false;

            string level = name.Substring(marker + LOD_MARKER.Length);
            return level.Length > 0 && int.TryParse(level, out int _);
        }

    }
}
