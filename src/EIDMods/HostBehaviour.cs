using System;
using UnityEngine;

namespace EIDMods;

/// <summary>
/// The only type injected into IL2CPP. It just forwards Unity messages to the FeatureHost,
/// so the reloadable features never need injected types of their own.
/// </summary>
public class HostBehaviour : MonoBehaviour
{
    public HostBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update() => FeatureHost.Update();
    private void LateUpdate() => FeatureHost.LateUpdate();
    private void OnGUI() => FeatureHost.OnGUI();
}
