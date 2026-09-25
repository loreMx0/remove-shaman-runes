using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DisableShamanRunesAndroid
{
    // Android port of Disable Shaman Runes.
    // Differences from the PC mod:
    //  - no [BepInProcess("Hollow Knight Silksong.exe")]: there is no such process on Android and
    //    it could stop the plugin from loading
    //  - new Harmony() + a manual raw-MethodInfo postfix instead of new Harmony("id").PatchAll()
    //  - config is read inside try/catch and falls back to the PC defaults
    //  - the private fields are looked up once at startup instead of on every Refresh call,
    //    and any field that isn't found is reported in the log
    //  - log lines that repeated on every refresh are now written once
    // Change the GUID/name below to your own.
    [BepInPlugin("com.yourname.noshamanrunesandroid", "Disable Shaman Runes Android", "1.7.0")]
    public class DisableShamanRunesPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // Same defaults as the PC mod
        private static bool disableRuneSymbol = true;
        private static bool disableZapVisuals = false;

        private static FieldInfo runeField;
        private static FieldInfo zapTintSpritesField;
        private static FieldInfo zapTintParticlesField;
        private static FieldInfo disableIfZapField;
        private static FieldInfo initialSpriteColoursField;
        private static FieldInfo initialParticleColoursField;
        private static FieldInfo runeSpawnEffectField;
        private static FieldInfo spawnOffsetField;
        private static FieldInfo spawnScaleField;
        private static FieldInfo spawnDelayField;
        private static FieldInfo spawnMultField;

        private static bool hookLogged;
        private static bool hiddenLogged;
        private static bool zapLogged;

        private void Awake()
        {
            Log = Logger;
            try
            {
                try
                {
                    disableRuneSymbol = Config.Bind("Visuals", "Disable Rune Symbol", true,
                        "Disables the purple rune symbol that appears on attacks").Value;
                    disableZapVisuals = Config.Bind("Visuals", "Disable Zap Visuals", false,
                        "Disables zap color tint (removes purple color from attacks)").Value;
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Config failed, using defaults: " + ex.Message);
                }

                ResolveFields();

                var original = AccessTools.Method(typeof(HeroShamanRuneEffect), "Refresh");
                if (original == null)
                {
                    Log.LogError("HeroShamanRuneEffect.Refresh not found");
                    return;
                }

                var postfix = AccessTools.Method(typeof(DisableShamanRunesPlugin), nameof(RefreshPostfix));

                var harmony = new Harmony();                                 // parameterless
                harmony.Patch(original, prefix: null, postfix: postfix);     // raw MethodInfo, no HarmonyMethod

                Log.LogInfo("Patched HeroShamanRuneEffect.Refresh (Disable Rune Symbol=" + disableRuneSymbol +
                            ", Disable Zap Visuals=" + disableZapVisuals + ")");
            }
            catch (Exception ex)
            {
                Log.LogError("Awake failed: " + ex);
            }
        }

        private static FieldInfo Field(string name)
        {
            FieldInfo f = AccessTools.Field(typeof(HeroShamanRuneEffect), name);
            if (f == null) Log.LogWarning("Field not found on HeroShamanRuneEffect: " + name);
            return f;
        }

        private static void ResolveFields()
        {
            runeField = Field("rune");
            zapTintSpritesField = Field("zapTintSprites");
            zapTintParticlesField = Field("zapTintParticles");
            disableIfZapField = Field("disableIfZap");
            initialSpriteColoursField = Field("initialSpriteColours");
            initialParticleColoursField = Field("initialParticleColours");
            runeSpawnEffectField = Field("runeSpawnEffect");
            spawnOffsetField = Field("spawnOffset");
            spawnScaleField = Field("spawnScale");
            spawnDelayField = Field("spawnDelay");
            spawnMultField = Field("spawnMult");
        }

        private static T Get<T>(FieldInfo field, object instance, T fallback)
        {
            if (field == null) return fallback;
            object value = field.GetValue(instance);
            return value is T ? (T)value : fallback;
        }

        private static void RefreshPostfix(HeroShamanRuneEffect __instance)
        {
            try
            {
                if (!hookLogged)
                {
                    hookLogged = true;
                    Log.LogInfo("Refresh hook firing (__instance " + (__instance == null ? "is NULL" : "ok") + ")");
                }

                if (__instance == null) return;

                GameObject rune = Get<GameObject>(runeField, __instance, null);

                // Feature 1: just hide the rune symbol
                if (disableRuneSymbol)
                {
                    if (rune != null && rune.activeSelf)
                    {
                        rune.SetActive(false);
                        if (!hiddenLogged)
                        {
                            hiddenLogged = true;
                            Log.LogInfo("Disabled rune symbol");
                        }
                    }
                    return;
                }

                // Feature 2: remove zap visuals but keep normal particles
                if (disableZapVisuals)
                {
                    RemoveZapVisuals(__instance, rune);
                }
            }
            catch (Exception ex)
            {
                Log.LogError("RefreshPostfix failed: " + ex);
            }
        }

        private static void RemoveZapVisuals(HeroShamanRuneEffect instance, GameObject rune)
        {
            var sprites = Get<List<SpriteRenderer>>(zapTintSpritesField, instance, null);
            var particles = Get<List<ParticleSystem>>(zapTintParticlesField, instance, null);
            var disableIfZap = Get<GameObject[]>(disableIfZapField, instance, null);
            var initialSpriteColours = Get<Dictionary<SpriteRenderer, Color>>(initialSpriteColoursField, instance, null);
            var initialParticleColours = Get<Dictionary<ParticleSystem, ParticleSystem.MinMaxGradient>>(initialParticleColoursField, instance, null);
            var runeSpawnEffect = Get<GameObject>(runeSpawnEffectField, instance, null);
            Vector3 spawnOffset = Get<Vector3>(spawnOffsetField, instance, Vector3.zero);
            Vector3 spawnScale = Get<Vector3>(spawnScaleField, instance, Vector3.one);
            float spawnDelay = Get<float>(spawnDelayField, instance, 0f);
            float spawnMult = Get<float>(spawnMultField, instance, 1f);

            // Hide the rune to remove zap effects
            if (rune != null && rune.activeSelf)
                rune.SetActive(false);

            // Re-enable normal particles that were disabled by zap
            if (disableIfZap != null)
            {
                foreach (GameObject obj in disableIfZap)
                {
                    if (obj != null && !obj.activeSelf)
                        obj.SetActive(true);
                }
            }

            // Respawn particles without the zap tint
            if (runeSpawnEffect != null)
            {
                Transform target = rune != null ? rune.transform : instance.transform;

                GameObject spawned = UnityEngine.Object.Instantiate(
                    runeSpawnEffect,
                    target.TransformPoint(spawnOffset),
                    Quaternion.identity);

                spawned.transform.localScale = target.TransformVector(spawnScale);

                var follow = spawned.GetComponent<FollowTransform>();
                if (follow != null) follow.Target = target;

                var followRotation = spawned.GetComponent<FollowRotation>();
                if (followRotation != null) followRotation.Target = target;

                var spawnedPs = spawned.GetComponent<ParticleSystem>();
                var originalPs = runeSpawnEffect.GetComponent<ParticleSystem>();
                if (spawnedPs != null && originalPs != null)
                {
                    var main = spawnedPs.main;
                    main.startDelay = spawnDelay;

                    var emission = spawnedPs.emission;
                    emission.rateOverTimeMultiplier = spawnMult * originalPs.emission.rateOverTimeMultiplier;

                    main.startColor = new ParticleSystem.MinMaxGradient(Color.white);

                    spawnedPs.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    spawnedPs.Play();
                }

                if (!zapLogged)
                {
                    zapLogged = true;
                    Log.LogInfo("Spawned non-zap particle effect");
                }
            }

            // Reset any remaining sprite tint
            if (sprites != null && initialSpriteColours != null)
            {
                foreach (SpriteRenderer sr in sprites)
                {
                    if (sr != null && initialSpriteColours.ContainsKey(sr))
                        sr.color = initialSpriteColours[sr];
                }
            }

            // Reset any remaining particle tint
            if (particles != null && initialParticleColours != null)
            {
                foreach (ParticleSystem ps in particles)
                {
                    if (ps != null && initialParticleColours.ContainsKey(ps))
                    {
                        var main = ps.main;
                        main.startColor = initialParticleColours[ps];
                    }
                }
            }
        }
    }
}
