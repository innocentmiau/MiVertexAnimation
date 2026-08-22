using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace MiVertexAnimation
{

    /*
     * A texture built from script is readable by default, and a readable texture keeps a full copy of
     * its pixels in system memory for the lifetime of the object, on top of the copy on the GPU.
     * That is worth paying for a texture something calls GetPixels on. Nothing in this package ever
     * does - the shader reads these, and the shader reads them on the GPU - so it was paying twice
     * for every position and normal texture it ever wrote.
     *
     * Apply(false, true) would be the obvious fix and is the wrong one: it frees the pixels BEFORE the
     * asset is serialized, so the file lands on disk with no image data in it. The flag has to be
     * cleared on the saved asset instead, which is what this does.
     */
    /// <summary>Clears the readable flag on baked VAT textures, new ones and ones already written.</summary>
    public static class VATTextureMaintenance
    {

        // Only the names this baker writes, so a Texture2DArray belonging to something else is left
        // alone even if it happens to sit in the same folder.
        private static readonly string[] BAKED_SUFFIXES = { "_Positions", "_Normals", "_Pivots" };

        /// <summary>
        /// Drops the CPU-side copy of a saved texture. Safe to call on anything, including null.
        /// </summary>
        /// <param name="texture">The texture asset to mark non-readable.</param>
        /// <returns>True when the flag was set and has now been cleared.</returns>
        public static bool ClearReadable(Texture texture)
        {
            if (!texture || !texture.isReadable) return false;

            SerializedObject serialized = new SerializedObject(texture);
            SerializedProperty readable = serialized.FindProperty("m_IsReadable");

            if (readable == null) return false;

            readable.boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        /*
         * For everything baked before this existed. Textures are found by the suffixes the baker uses
         * rather than by scanning every Texture2DArray in the project, because silently halving the
         * memory of an asset some other system does read from would be a much worse bug than the one
         * this fixes.
         */
        /// <summary>Frees every baked texture in the project that still keeps a CPU copy.</summary>
        public static void FreeExisting()
        {
            List<Texture> found = new List<Texture>();
            long bytes = 0L;

            foreach (string guid in AssetDatabase.FindAssets("t:Texture2DArray"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsBakedName(path)) continue;

                Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
                if (!texture || !texture.isReadable) continue;

                found.Add(texture);
                bytes += Profiler.GetRuntimeMemorySizeLong(texture);
            }

            if (found.Count == 0)
            {
                EditorUtility.DisplayDialog("Vertex Animation",
                    "Every baked texture already has its CPU copy freed.", "OK");
                return;
            }

            // Halved because the figure counts both copies, and only the one in system memory goes.
            string saving = EditorUtility.FormatBytes(bytes / 2L);

            if (!EditorUtility.DisplayDialog("Vertex Animation",
                    $"{found.Count} baked texture(s) still keep a copy of their pixels in system memory " +
                    $"that nothing reads. Freeing it saves roughly {saving} at runtime and changes " +
                    "nothing about how they render.\n\nThe assets keep their data on disk either way.",
                    $"Free {saving}", "Cancel"))
                return;

            int cleared = 0;
            foreach (Texture texture in found)
            {
                if (!ClearReadable(texture)) continue;

                EditorUtility.SetDirty(texture);
                cleared++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[VAT] Freed the CPU copy of {cleared} baked texture(s), about {saving}.");
        }

        private static bool IsBakedName(string path)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);

            foreach (string suffix in BAKED_SUFFIXES)
                if (name.EndsWith(suffix, System.StringComparison.Ordinal)) return true;

            return false;
        }

    }
}
