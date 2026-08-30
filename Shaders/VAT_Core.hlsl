#ifndef VAT_CORE_INCLUDED
#define VAT_CORE_INCLUDED

/*
 * A pass that only writes depth or an id never looks at the normal, and defining VAT_NORMALS_UNUSED
 * before including this drops every fetch and every rotation that feeds it. Each pass is its own
 * compilation unit, so the define is local to the one that sets it.
 *
 * That is two texture reads per vertex saved with frame blending on, four while a clip cross-fade is
 * running - and the depth prepass is not free work: it runs for screen space ambient occlusion and
 * for depth priming. Anything that starts USING the normal in one of those passes has to drop the
 * define, or it will be reading a constant.
 */

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// Per-clip data rides in eight float4 material properties rather than a shader array,
// because Material.SetVectorArray is NOT serialized - a .mat stores only floats, colors,
// ints and textures. An array survives the bake and is gone by the next asset reload,
// which leaves every clip at rate 0, i.e. frozen on frame 0.
//
// Each vector packs two clips: xy = (frames, rate) for the even one, zw for the odd.
// To go past 16 clips, add properties here, in VAT_Lit.shader and in VAT_ClipInfo, and
// raise MaxClips in VATBakerWindow.
#define VAT_MAX_CLIPS 16

// One per component of UV3, the channel nothing else conventionally uses.
// Must match MAX_SECTIONS in VATBakerWindow.
#define VAT_MAX_SECTIONS 4

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4  _BaseColor;
    half   _Smoothness;
    half   _Metallic;
    half   _Cutoff;
    float  _Cull;

    float  _VATTextureWidth;
    float  _VATTextureHeight;   // height of ONE array slice
    float  _VATRowsPerFrame;
    float  _VATPhaseVariation;
    float  _VATFrameBlend;
    float  _VATBlendDuration;
    float  _VATClipCount;

    float4 _VATClipData0;
    float4 _VATClipData1;
    float4 _VATClipData2;
    float4 _VATClipData3;
    float4 _VATClipData4;
    float4 _VATClipData5;
    float4 _VATClipData6;
    float4 _VATClipData7;

    // The box normalized positions are stored as a fraction of. Unused when they are raw half floats.
    float4 _VATPositionMin;
    float4 _VATPositionExtent;

    // How many sections this bake wrote, and the rows per slice of the pivot texture.
    float  _VATSectionCount;
    float  _VATPivotHeight;
CBUFFER_END

// Per-instance playback state, written by VATAnimator through a MaterialPropertyBlock.
// With instancing off these resolve to plain uniforms, so a single object still works by
// setting the material directly.
UNITY_INSTANCING_BUFFER_START(VATInstance)
    UNITY_DEFINE_INSTANCED_PROP(float, _VATClip)          // clip being played
    UNITY_DEFINE_INSTANCED_PROP(float, _VATClipStart)     // _Time.y when it started
    UNITY_DEFINE_INSTANCED_PROP(float, _VATPreviousClip)  // clip being faded out
    UNITY_DEFINE_INSTANCED_PROP(float, _VATPreviousStart)
    UNITY_DEFINE_INSTANCED_PROP(float, _VATBlendStart)    // _Time.y when the fade began
    /*
     * Per instance rather than per material, so one crowd at one material can run at a speed each -
     * a character sprinting while the one beside it walks, on the same clip and in the same batch.
     * It is still declared in the shader's Properties, so a renderer nothing writes a property block
     * for takes the material's value and behaves exactly as it did when this lived in the CBUFFER.
     *
     * Speed scales elapsed time, so changing it moves the clip somewhere else in the same instant
     * unless the start time moves with it. VATAnimator does that; a driver of your own has to.
     */
    UNITY_DEFINE_INSTANCED_PROP(float, _VATSpeed)
    /*
     * Where playback stops, as a fraction of the clip:
     *     0             loop, which is also what an instance nothing has written reads as
     *     >= 1          play through once and stop on the last baked frame
     *     0 < hold < 1  stop at that point in the clip, which is how a freeze holds the pose on screen
     */
    UNITY_DEFINE_INSTANCED_PROP(float, _VATHold)
    UNITY_DEFINE_INSTANCED_PROP(float, _VATPreviousHold)
    /*
     * Per section, and behind the keyword so a bake without sections pays nothing per instance.
     *
     * A transition is described rather than stepped: where it starts, where it ends, when it began and
     * how long it takes. The driver writes these ONCE and the GPU walks the curve, which is what keeps
     * two hundred characters turning their heads off the CPU entirely. Writing them every frame with a
     * duration of 0 is the CPU-driven path, and needs no separate code here.
     *
     * The two timing values ride in the w of the offset vectors, which an offset never uses:
     *     FromOff.w = start time      ToOff.w = duration
     */
#if defined(_VAT_SECTIONS)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionFromRot0)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionToRot0)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionFromOff0)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionToOff0)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionFromRot1)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionToRot1)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionFromOff1)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionToOff1)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionFromRot2)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionToRot2)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionFromOff2)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionToOff2)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionFromRot3)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionToRot3)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionFromOff3)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATSectionToOff3)
#endif
UNITY_INSTANCING_BUFFER_END(VATInstance)

TEXTURE2D(_BaseMap);              SAMPLER(sampler_BaseMap);
TEXTURE2D_ARRAY(_VATPositionTex); SAMPLER(sampler_VATPositionTex);
TEXTURE2D_ARRAY(_VATNormalTex);   SAMPLER(sampler_VATNormalTex);
TEXTURE2D_ARRAY(_VATPivotTex);    SAMPLER(sampler_VATPivotTex);

/*
 * Called by every pass, not only the visible one. A hole punched in the mesh has to be a hole in its
 * shadow and in the depth prepass too, or a cut-out cape casts the shadow of a solid rectangle
 * and reads as solid to screen space ambient occlusion.
 *
 * Costs one texture fetch in passes that would otherwise do none, so it is behind a shader feature
 * and a material that does not use alpha clipping never compiles it in.
 */
void VAT_AlphaClip(float2 uv)
{
#if defined(_ALPHATEST_ON)
    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a * _BaseColor.a;
    clip(alpha - _Cutoff);
#endif
}

// Playback offset derived from the object's world-space origin. Costs nothing and needs no
// per-instance data. Leave it at 0 when VATAnimator is driving things - staggered clip start
// times de-synchronise a crowd more controllably.
float VAT_PhaseOffset()
{
    // Off by default, because VATAnimator staggers a crowd itself and does it more controllably.
    // The compiler cannot fold the multiply away, so without this every vertex of every pass pays
    // for a sine whose result is then multiplied by zero. Uniform per material, so the branch is the
    // cheapest kind a GPU has.
    if (_VATPhaseVariation <= 0.0) return 0.0;

    float3 originWS = float3(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m13, UNITY_MATRIX_M._m23);
    return frac(sin(dot(originWS, float3(12.9898, 78.233, 37.719))) * 43758.5453) * _VATPhaseVariation;
}

// Returns (total frames, baked frame rate) for a clip.
float2 VAT_ClipInfo(uint clip)
{
    uint pair = clip >> 1;
    float4 packed =
        pair == 0 ? _VATClipData0 :
        pair == 1 ? _VATClipData1 :
        pair == 2 ? _VATClipData2 :
        pair == 3 ? _VATClipData3 :
        pair == 4 ? _VATClipData4 :
        pair == 5 ? _VATClipData5 :
        pair == 6 ? _VATClipData6 : _VATClipData7;

    float2 info = (clip & 1) == 0 ? packed.xy : packed.zw;
    info.x = max(info.x, 1.0);
    return info;
}

float VAT_ClipFrames(uint clip) { return VAT_ClipInfo(clip).x; }

uint VAT_ClampClip(float clip)
{
    return (uint)clamp(clip, 0.0, max(_VATClipCount - 1.0, 0.0));
}

/*
 * Position within the clip, in frames: 0 .. totalFrames.
 *
 * A hold stops the clip somewhere instead of wrapping it. A one-shot needs that so nothing restarts
 * underneath the fade out of it, and a freeze needs it so a body stays on the ground.
 *
 * Holding the end lands on the last frame exactly rather than a fraction short of it. A phase of
 * 0.999 * frames looks like the last frame and is not: floor() picks frame N-1, frac() comes out at
 * almost 1, and VAT_SampleClip wraps the frame after N-1 round to 0 - so with frame blending on, a
 * death animation asked to hold its last pose blends almost the whole way back to its first one and
 * stands up again.
 */
float VAT_Phase(float2 info, float startTime, float phaseOffset, float hold, float speed)
{
    float loopsPerSecond = info.y / info.x;
    float t = (_Time.y - startTime) * loopsPerSecond * speed + phaseOffset;

    if (hold <= 0.0) return frac(t) * info.x;

    // A held clip runs on an unwrapped t, so min() is the whole of what stops it - and once stopped
    // the phase is a constant, which is why a corpse is exactly where it fell an hour of _Time.y later.
    return min(t * info.x, hold >= 1.0 ? info.x - 1.0 : hold * info.x);
}

// Vertex N of frame F sits at x = N % width, y = floor(N / width) + rowsPerFrame * F.
// The +0.5 centres the sample; without it point filtering can pick up a neighbour.
float2 VAT_TexelUV(uint vertexID, float frame)
{
    float vid = (float)vertexID;
    float col = fmod(vid, _VATTextureWidth);
    float row = floor(vid / _VATTextureWidth) + _VATRowsPerFrame * frame;
    return float2((col + 0.5) / _VATTextureWidth,
                  (row + 0.5) / _VATTextureHeight);
}

/*
 * Three ways a normal can arrive, chosen at bake time and carried by a keyword so a texture baked
 * before any of this existed is still read exactly as it was written.
 *
 * A normal has two degrees of freedom, not three, so storing xyz spends a third of every texel
 * restating what the other channels already said. Octahedral encoding folds the sphere onto a square
 * and keeps two channels, which fits sixteen bits each into the four bytes three eight-bit channels
 * took - and lands about a hundred times closer.
 */
// Unfolds the octahedron VATBakerWindow.OctEncode folded, and must stay its exact inverse.
float3 VAT_OctDecode(float2 folded)
{
    float3 normalOS = float3(folded.x, folded.y, 1.0 - abs(folded.x) - abs(folded.y));

    float fold = saturate(-normalOS.z);
    normalOS.xy += normalOS.xy >= 0.0 ? -fold : fold;

    return normalize(normalOS);
}

float3 VAT_ReadNormal(float2 uv, uint clip)
{
    float4 raw = SAMPLE_TEXTURE2D_ARRAY_LOD(_VATNormalTex, sampler_VATNormalTex, uv, clip, 0);

#if defined(_VAT_NORMALSOCT)
    return VAT_OctDecode((raw.xy * 2.0) - 1.0);
#elif defined(_VAT_NORMALS8)
    return (raw.xyz * 2.0) - 1.0;
#else
    return raw.xyz;
#endif
}

/*
 * Takes the clip's (frames, rate) rather than looking it up again. Selecting one of eight float4s is
 * a chain of conditional moves, and it was being paid twice per clip - once to work out the phase and
 * once more to wrap the blend onto the first frame.
 */
void VAT_SampleClip(uint vertexID, uint clip, float2 info, float phase,
                    out float3 positionOS, out float3 normalOS)
{
    float frame0 = floor(phase);
    float2 uv0 = VAT_TexelUV(vertexID, frame0);

    positionOS = SAMPLE_TEXTURE2D_ARRAY_LOD(_VATPositionTex, sampler_VATPositionTex, uv0, clip, 0).xyz;

#if defined(VAT_NORMALS_UNUSED) || defined(_VAT_NONORMALS)
    // Nothing reads it: either this pass discards it, or no normal texture was baked and VAT_Sample
    // fills it in from the mesh.
    normalOS = 0;
#else
    normalOS = VAT_ReadNormal(uv0, clip);
#endif

#if defined(_VAT_FRAMEBLEND)
    // fmod wraps to frame 0 on the last frame instead of reading unbaked rows.
    float frame1 = fmod(frame0 + 1.0, info.x);
    float2 uv1 = VAT_TexelUV(vertexID, frame1);
    float weight = frac(phase);

    positionOS = lerp(positionOS,
        SAMPLE_TEXTURE2D_ARRAY_LOD(_VATPositionTex, sampler_VATPositionTex, uv1, clip, 0).xyz, weight);

#if !defined(VAT_NORMALS_UNUSED) && !defined(_VAT_NONORMALS)
    normalOS = lerp(normalOS, VAT_ReadNormal(uv1, clip), weight);
#endif
#endif
}

// Rotates v by the unit quaternion q.
float3 VAT_RotateQ(float4 q, float3 v)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

/*
 * The pivot a section turns on moves with the animation - a neck is somewhere different on every frame
 * of a walk - so it is baked per frame rather than fixed. The texture is VAT_MAX_SECTIONS texels wide
 * and one row per frame, a few kilobytes for a whole bake, and every vertex of an instance reads the
 * same texel, so it sits in cache.
 */
float3 VAT_ReadPivot(float section, float frame, uint clip)
{
    float2 uv = float2((section + 0.5) / VAT_MAX_SECTIONS,
                       (frame + 0.5) / max(_VATPivotHeight, 1.0));

    return SAMPLE_TEXTURE2D_ARRAY_LOD(_VATPivotTex, sampler_VATPivotTex, uv, clip, 0).xyz;
}

// Blended the same way the positions are, or the section would step between frames while the mesh
// under it moved smoothly.
float3 VAT_SamplePivot(float section, uint clip, float frames, float phase)
{
    float frame0 = floor(phase);
    float3 pivot = VAT_ReadPivot(section, frame0, clip);

#if defined(_VAT_FRAMEBLEND)
    float frame1 = fmod(frame0 + 1.0, frames);
    pivot = lerp(pivot, VAT_ReadPivot(section, frame1, clip), frac(phase));
#endif

    return pivot;
}

/*
 * One section: a local turn and shift about its own pivot, faded in by the vertex's weight.
 *
 * nlerp toward identity rather than slerp, because at the angles a head or a torso actually turns the
 * two agree to a fraction of a degree and this runs per vertex. An unset rotation arrives as (0,0,0,0),
 * which still normalizes to identity, so a material nothing has driven is left exactly where it was.
 */
// A quaternion nothing has written arrives as all zeros, and normalizing that is a NaN - which on a
// vertex position makes the entire mesh vanish rather than look slightly wrong.
float4 VAT_SafeQuat(float4 q)
{
    return dot(q, q) < 1e-8 ? float4(0.0, 0.0, 0.0, 1.0) : q;
}

void VAT_ApplySection(inout float3 positionOS, inout float3 normalOS,
                      float mask, float3 pivot, float4 rotation, float3 offset)
{
    float4 q = VAT_SafeQuat(lerp(float4(0.0, 0.0, 0.0, 1.0), rotation, mask));
    q = normalize(q);

    positionOS = pivot + VAT_RotateQ(q, positionOS - pivot) + offset * mask;

#if !defined(VAT_NORMALS_UNUSED)
    normalOS = VAT_RotateQ(q, normalOS);
#endif
}

/*
 * Walks one section's transition and applies the result.
 *
 * smoothstep rather than a selectable curve, because carrying an easing mode would cost another value
 * per section per instance for something a caller who really wants a different curve can do on the CPU
 * by writing the pose it wants with a duration of 0.
 *
 * nlerp, with the far quaternion flipped onto the near hemisphere first so the turn takes the short way
 * round. That flip is also what stops the two ends cancelling to zero halfway through.
 */
void VAT_ApplyTimedSection(inout float3 positionOS, inout float3 normalOS, float mask, float3 pivot,
                           float4 fromRot, float4 toRot, float4 fromOff, float4 toOff)
{
    float start    = fromOff.w;
    float duration = toOff.w;

    float t = duration > 0.0 ? saturate((_Time.y - start) / duration) : 1.0;
    t = smoothstep(0.0, 1.0, t);

    float4 a = VAT_SafeQuat(fromRot);
    float4 b = VAT_SafeQuat(toRot);
    if (dot(a, b) < 0.0) b = -b;

    VAT_ApplySection(positionOS, normalOS, mask, pivot,
        normalize(lerp(a, b, t)), lerp(fromOff.xyz, toOff.xyz, t));
}

/*
 * Priority between overlapping sections was already resolved at bake time - the weights arriving here
 * never sum past one - so this applies them in order and does no arbitration of its own.
 *
 * _VATSectionCount is a material constant, so every branch below is uniform across the whole draw and
 * a bake with one section never pays for four.
 */
void VAT_ApplySections(inout float3 positionOS, inout float3 normalOS,
                       float4 mask, uint clip, float frames, float phase)
{
#if defined(_VAT_SECTIONS)
    if (_VATSectionCount > 0.5)
        VAT_ApplyTimedSection(positionOS, normalOS, mask.x, VAT_SamplePivot(0.0, clip, frames, phase),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionFromRot0),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionToRot0),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionFromOff0),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionToOff0));

    if (_VATSectionCount > 1.5)
        VAT_ApplyTimedSection(positionOS, normalOS, mask.y, VAT_SamplePivot(1.0, clip, frames, phase),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionFromRot1),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionToRot1),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionFromOff1),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionToOff1));

    if (_VATSectionCount > 2.5)
        VAT_ApplyTimedSection(positionOS, normalOS, mask.z, VAT_SamplePivot(2.0, clip, frames, phase),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionFromRot2),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionToRot2),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionFromOff2),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionToOff2));

    if (_VATSectionCount > 3.5)
        VAT_ApplyTimedSection(positionOS, normalOS, mask.w, VAT_SamplePivot(3.0, clip, frames, phase),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionFromRot3),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionToRot3),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionFromOff3),
            UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSectionToOff3));
#endif
}

/*
 * Takes the mesh's own normal as well as the vertex id, because with Bake Normals off there is no normal
 * texture to read and the bind pose normal is the only one there is.
 * That normal is wrong wherever the animation bends the surface, which is the trade Bake Normals makes,
 * but it is a plausible wrong rather than normalize(0, 0, 0) and a mesh lit by undefined values.
 *
 * The fetches disappear with the keyword rather than being read and thrown away, so turning normals off
 * saves the bandwidth in every pass as well as half the texture memory.
 */
void VAT_Sample(uint vertexID, float3 meshNormalOS, float4 sectionMask,
                out float3 positionOS, out float3 normalOS)
{
    float phaseOffset = VAT_PhaseOffset();

    uint  clip      = VAT_ClampClip(UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATClip));
    float clipStart = UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATClipStart);
    float clipHold  = UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATHold);
    float speed     = UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATSpeed);

    float2 clipInfo = VAT_ClipInfo(clip);
    float phase = VAT_Phase(clipInfo, clipStart, phaseOffset, clipHold, speed);
    VAT_SampleClip(vertexID, clip, clipInfo, phase, positionOS, normalOS);

    float blendStart = UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATBlendStart);
    float blend = _VATBlendDuration > 0.0
        ? saturate((_Time.y - blendStart) / _VATBlendDuration)
        : 1.0;

    // The branch is uniform across an instance, so it costs nothing, and the outgoing clip's
    // texture fetches are only paid while a transition is actually running.
    if (blend < 1.0)
    {
        uint  previousClip  = VAT_ClampClip(UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATPreviousClip));
        float previousStart = UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATPreviousStart);
        float previousHold  = UNITY_ACCESS_INSTANCED_PROP(VATInstance, _VATPreviousHold);

        float2 previousInfo = VAT_ClipInfo(previousClip);
        float3 previousPosition, previousNormal;

        VAT_SampleClip(vertexID, previousClip, previousInfo,
            VAT_Phase(previousInfo, previousStart, phaseOffset, previousHold, speed),
            previousPosition, previousNormal);

        positionOS = lerp(previousPosition, positionOS, blend);

#if !defined(VAT_NORMALS_UNUSED)
        normalOS = lerp(previousNormal, normalOS, blend);
#endif
    }

    /*
     * Decoded here rather than at each fetch. The encoding is affine, so blending encoded positions and
     * decoding the result is identical to decoding both and blending - and doing it once per vertex
     * instead of up to four times is free precision.
     */
#if defined(_VAT_POSNORM)
    positionOS = _VATPositionMin.xyz + (positionOS * _VATPositionExtent.xyz);
#endif

#if defined(VAT_NORMALS_UNUSED)
    normalOS = float3(0, 0, 1);
#elif defined(_VAT_NONORMALS)
    normalOS = meshNormalOS;
#else
    normalOS = normalize(normalOS);
#endif

    // Applied to the current clip only. During a cross-fade the outgoing clip's pivot is ignored,
    // which can shift a section by a hair for the length of the fade and is not worth four more
    // texture fetches to avoid.
    VAT_ApplySections(positionOS, normalOS, sectionMask, clip, clipInfo.x, phase);
}

// For the passes that never look at the normal: depth, scene selection and picking.
void VAT_Sample(uint vertexID, float4 sectionMask, out float3 positionOS, out float3 normalOS)
{
    VAT_Sample(vertexID, float3(0, 0, 1), sectionMask, positionOS, normalOS);
}

#endif // VAT_CORE_INCLUDED
