using System;
using System.Collections;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>A few beeps (and a game notice, if it shows) so a long diagnostic can be followed from inside the headset.</summary>
    internal partial class VrManager
    {
        private AudioClip _beepClip;
        private AudioSource _beepSource;

        private void PlayCue(int beeps, string notice)
        {
            try
            {
                Interface.CAvisos.Nuevo(notice, 6);
            }
            catch
            {
                // the notice is only a bonus
            }
            StartCoroutine(BeepRoutine(beeps));
        }

        private IEnumerator BeepRoutine(int beeps)
        {
            if (_beepClip == null)
            {
                const int rate = 44100;
                var samples = new float[rate / 6];
                for (var i = 0; i < samples.Length; i++)
                {
                    var fade = Mathf.Min(1f, Mathf.Min(i, samples.Length - i) / 800f);
                    samples[i] = Mathf.Sin(2f * Mathf.PI * 880f * i / rate) * 0.5f * fade;
                }
                _beepClip = AudioClip.Create("NuclearesVR_beep", samples.Length, 1, rate, false);
                _beepClip.SetData(samples, 0);
            }
            if (_beepSource == null)
            {
                _beepSource = gameObject.AddComponent<AudioSource>();
                _beepSource.spatialBlend = 0f;
                _beepSource.playOnAwake = false;
                _beepSource.ignoreListenerPause = true;
            }
            for (var i = 0; i < beeps; i++)
            {
                _beepSource.PlayOneShot(_beepClip, 1f);
                yield return new WaitForSecondsRealtime(0.4f);
            }
        }
    }
}
