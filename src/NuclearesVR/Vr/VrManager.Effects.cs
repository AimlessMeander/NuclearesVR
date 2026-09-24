using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Some of the game's visuals aren't drawn by lights or meshes on their own
    /// but by components sitting on the *camera*: the Volumetric Lights asset
    /// (torch beam, rotating alarm-light beams) composites through a
    /// post-process image effect on the player camera, and Unity's halos and
    /// lens flares only render on cameras that have a FlareLayer. Our eye
    /// cameras are fresh camera objects, and Camera.CopyFrom copies Camera
    /// settings only, not sibling components - so those lights worked on the
    /// monitor but not in the headset. This copies the relevant components
    /// across and keeps their enabled state in sync.
    ///
    /// Only serialized fields are copied (public, or [SerializeField]): the
    /// rest is runtime state - cached render textures, command buffers, a
    /// render-pass object holding a reference back to its owner - which must
    /// not be shared between two live instances.
    /// </summary>
    internal partial class VrManager
    {
        private readonly Dictionary<Camera, List<KeyValuePair<Behaviour, Behaviour>>> _effectPairs =
            new Dictionary<Camera, List<KeyValuePair<Behaviour, Behaviour>>>();

        private static bool IsCopiedEffect(Component component)
        {
            return component is VolumetricLights.VolumetricLightsPostProcessBase ||
                   component.GetType().Name == "FlareLayer";
        }

        private static bool IsSerializedField(FieldInfo field)
        {
            if (field.IsStatic || field.IsInitOnly || field.IsLiteral || field.IsNotSerialized)
            {
                return false;
            }
            return field.IsPublic || field.GetCustomAttributes(typeof(SerializeField), true).Length > 0;
        }

        private static void CopySerializedFields(Component source, Component destination)
        {
            for (var type = source.GetType(); type != null && type != typeof(MonoBehaviour) && type != typeof(Behaviour); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!IsSerializedField(field))
                    {
                        continue;
                    }
                    try
                    {
                        field.SetValue(destination, field.GetValue(source));
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogWarning($"Couldn't copy {type.Name}.{field.Name}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Must be called while the eye camera's GameObject is still inactive,
        /// so the copied fields are in place before the components' own Awake
        /// and OnEnable run (they read their settings there).
        /// </summary>
        private void CopyCameraEffects(Camera main, Camera eye)
        {
            var pairs = new List<KeyValuePair<Behaviour, Behaviour>>();
            var names = new List<string>();
            foreach (var component in main.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }
                names.Add(component.GetType().Name);
                if (!IsCopiedEffect(component) || !(component is Behaviour sourceBehaviour))
                {
                    continue;
                }

                var copy = eye.gameObject.AddComponent(component.GetType()) as Behaviour;
                if (copy == null)
                {
                    continue;
                }
                CopySerializedFields(component, copy);
                copy.enabled = sourceBehaviour.enabled;
                pairs.Add(new KeyValuePair<Behaviour, Behaviour>(sourceBehaviour, copy));
            }

            _effectPairs[eye] = pairs;
            Plugin.Logger.LogInfo($"Components on '{main.name}': {string.Join(", ", names.ToArray())}. " +
                                   $"Copied {pairs.Count} camera effect(s) to '{eye.name}'.");
        }

        private void SyncEffectsEnabled(Camera eye)
        {
            if (!_effectPairs.TryGetValue(eye, out var pairs))
            {
                return;
            }
            foreach (var pair in pairs)
            {
                if (pair.Key != null && pair.Value != null && pair.Value.enabled != pair.Key.enabled)
                {
                    pair.Value.enabled = pair.Key.enabled;
                }
            }
        }
    }
}
