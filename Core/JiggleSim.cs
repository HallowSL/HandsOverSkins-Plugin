using System.Collections.Generic;
using UnityEngine;

namespace HandsOverSkins.Core;

/// <summary>
/// Simple spring bones (Verlet tip per bone, length-constrained), shared by the plugin and the SDK preview.
/// Each jiggle bone's tip lags behind, springs back toward its rest direction and settles. Fixed 60 Hz steps,
/// so it behaves the same at any frame rate; a bone that jumps more than 0.5 m in one frame resets (teleports).
/// Only bones that don't follow a game bone should be added. Add them parent-first.
/// </summary>
internal sealed class JiggleSim
{
    private const float Step = 1f / 60f;
    private const int MaxSteps = 4;

    private sealed class Bone
    {
        public Transform T;
        public Quaternion RestLocal;
        public Vector3 Tip;          // child offset in the bone's local space (rest)
        public float S, D, G;
        public Vector3 Cur, Prev, LastPos;
        public bool Init;
    }

    private readonly List<Bone> _bones = new List<Bone>();
    private float _acc;

    public int Count => _bones.Count;

    /// <param name="tipLocal">Rest local position of the bone's child (or a guess for leaf bones).</param>
    /// <param name="stiffness">0..1</param><param name="damping">0..1</param><param name="gravity">0..1</param>
    public void Add(Transform t, Quaternion restLocal, Vector3 tipLocal, float stiffness, float damping, float gravity)
    {
        if (t == null || tipLocal.sqrMagnitude < 1e-12f) return;
        _bones.Add(new Bone
        {
            T = t, RestLocal = restLocal, Tip = tipLocal,
            S = Mathf.Lerp(0.01f, 0.5f, Mathf.Clamp01(stiffness)),
            D = Mathf.Lerp(0.02f, 0.6f, Mathf.Clamp01(damping)),
            G = Mathf.Clamp01(gravity) * 9.81f * Step * Step,
        });
    }

    public void Reset() { foreach (var b in _bones) b.Init = false; _acc = 0f; }

    /// <summary>Call once per frame after the body has been posed.</summary>
    public void Update(float dt)
    {
        if (_bones.Count == 0) return;
        _acc += Mathf.Clamp(dt, 0f, 0.1f);
        int n = 0;
        while (_acc >= Step && n < MaxSteps) { Tick(true); _acc -= Step; n++; }
        if (n == MaxSteps) _acc = 0f;
        if (n == 0) Tick(false);
    }

    private void Tick(bool integrate)
    {
        foreach (var b in _bones)
        {
            var t = b.T;
            if (t == null) continue;
            var p = t.parent;
            Quaternion restRot = (p != null ? p.rotation : Quaternion.identity) * b.RestLocal;
            Vector3 pos = t.position;
            Vector3 restOff = restRot * Vector3.Scale(b.Tip, t.lossyScale);
            float len = restOff.magnitude;
            if (len < 1e-6f) continue;
            Vector3 restTip = pos + restOff;

            if (!b.Init || (pos - b.LastPos).sqrMagnitude > 0.25f) { b.Cur = b.Prev = restTip; b.Init = true; }
            b.LastPos = pos;

            if (integrate)
            {
                Vector3 vel = (b.Cur - b.Prev) * (1f - b.D);
                Vector3 next = b.Cur + vel + (restTip - b.Cur) * b.S + Vector3.down * b.G;
                Vector3 dir = next - pos;
                next = dir.sqrMagnitude > 1e-12f ? pos + dir.normalized * len : restTip;
                b.Prev = b.Cur;
                b.Cur = next;
            }
            else
            {
                Vector3 dir = b.Cur - pos;
                b.Cur = dir.sqrMagnitude > 1e-12f ? pos + dir.normalized * len : restTip;
            }
            t.rotation = Quaternion.FromToRotation(restOff, b.Cur - pos) * restRot;
        }
    }
}
