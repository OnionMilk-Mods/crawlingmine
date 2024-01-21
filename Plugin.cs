using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using OnionMilk_crawlingmine;
using UnityEngine;

namespace OnionMilk_crawlingmine
{
	[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
	public class Plugin : BaseUnityPlugin
	{
		private static Plugin instance;
		private readonly Harmony _harmony = new Harmony("OnionMilk.CrawlingMine");

		public static ConfigEntry<bool> cfgEnabled;

		public static ConfigEntry<float> cfgJumpMineSpawnChance;
		
		public static ConfigEntry<bool> cfgMineSound;
		public static ConfigEntry<bool> cfgCrawlingMineSound;

		public static ConfigEntry<float> cfgJumpIntervalMin;
		public static ConfigEntry<float> cfgJumpIntervalMax;
		public static ConfigEntry<float> cfgJumpRangeMin;
		public static ConfigEntry<float> cfgJumpRangeMax;
		
		public static void Log(string msg)
		{
			instance.Logger.LogInfo($"[{PluginInfo.PLUGIN_GUID}] {msg}");
		}
		private void Awake()
		{
			instance = this;

			cfgJumpRangeMax = Config.Bind(
				"Settings",
				"minRangeInterval",
				1f,
				"Minimal jump range of mine (minval: 0.5)"
			);
			cfgJumpRangeMin = Config.Bind(
				"Settings",
				"maxRangeInterval",
				3f,
				"Maximal jump range of mine (minval: 0.5)"
			);
			cfgJumpIntervalMax = Config.Bind(
				"Settings",
				"minJumpInterval",
				4f,
				"Minimal of interval after which mine would change it's position (minval: 0.5)"
			);
			cfgJumpIntervalMin = Config.Bind(
				"Settings",
				"maxJumpInterval",
				10f,
				"Maximal of interval after which mine would change it's position (minval: 0.5)"
			);
			cfgJumpMineSpawnChance = Config.Bind(
				"Settings",
				"chance",
				0.1f,
				"Chance of transforming regular mine into jumping one (0.0-1.0)"
			);


			cfgMineSound = Config.Bind(
				"Settings",
				"mineBeepSound",
				true,
				"let's you disable beeping sound"
			);
			cfgCrawlingMineSound = Config.Bind(
				"Settings",
				"crawlingMineBeepSound",
				true,
				"let's you disable beeping sound of crawling mine ;)"
			);

			cfgEnabled = Config.Bind(
				"General",
				"enabled",
				true,
				"Is plugin enabled?"
			);

			if(!cfgEnabled.Value)
				return;
			

			Log($"Mod loaded and set up!");

			_harmony.PatchAll();
		}

		public static string GetPath
		{
			get
			{
				if(getPath == null)
				{
					var cd = Assembly.GetExecutingAssembly().CodeBase;
					UriBuilder uri = new UriBuilder(cd);
					string path = Uri.UnescapeDataString(uri.Path);
					getPath = Path.GetDirectoryName(path);
				}
				return getPath;
			}
		}
		private static string getPath = null;
	}
}

namespace HealthMetrics.Patches
{
	[HarmonyPatch(typeof(EnemyAI))]
	internal class EnemyAIPatches
	{
		[HarmonyPatch("ChooseClosestNodeToPosition")]
		[HarmonyPrefix]
		private static void ChooseClosestNodeToPosition(ref EnemyAI __instance, Vector3 pos, bool avoidLineOfSight = false, int offset = 0)
		{
			if(__instance.allAINodes.Any(n => n == null || n.Equals(null))) {
				__instance.allAINodes = __instance
					.allAINodes
					.Where(n => n != null && !n.Equals(null))
					.ToArray();
			}
		}
	}

	[HarmonyPatch(typeof(StartOfRound))]
	internal class StartOfRoundPatches
	{
		[HarmonyPatch("SceneManager_OnUnloadComplete")]
		[HarmonyPostfix]
		private static void SceneManager_OnUnloadComplete(ref StartOfRound __isntance, ulong clientId, string sceneName)
		{
			LandminePatches.jumpTimer = new();
		}
	}

	[HarmonyPatch(typeof(Landmine))]
	internal class LandminePatches
	{
		public static Dictionary<Landmine, float> jumpTimer = null;

		private static System.Random rnd = null;

		[HarmonyPatch("Start")]
		[HarmonyPostfix]
		private static void Start(ref Landmine __instance)
		{
			if(jumpTimer == null)
				jumpTimer = new();
			if(rnd == null)
				rnd = new System.Random(StartOfRound.Instance.randomMapSeed + 2137);

			if(rnd.NextDouble() < Plugin.cfgJumpMineSpawnChance.Value)
			{
				var node = __instance.transform.parent.GetComponentInChildren<ScanNodeProperties>();
				node.headerText = "Crawling Mine";
				node.subText = "It crawls around!";

				if(__instance.IsServer)
				{
					jumpTimer.Add(__instance, Time.time + GetInterval());

					Plugin.Log("Planted!");
				}
				
				if(!Plugin.cfgCrawlingMineSound.Value)
				{
					DisableAudio(__instance);
				}
			}

			if(!Plugin.cfgMineSound.Value)
			{
				DisableAudio(__instance);
			}

		}

		private static void DisableAudio(Landmine __instance)
		{
			__instance.mineAudio.enabled = false;
			__instance.mineAudio.volume = 0f;
			__instance.mineFarAudio.enabled = false;
			__instance.mineFarAudio.volume = 0f;
		}

		private static float GetInterval()
		{
			return Mathf.Max(0.5f, UnityEngine.Random.Range(
				Plugin.cfgJumpIntervalMin.Value, Plugin.cfgJumpIntervalMax.Value
			));
		}
		private static float GetRange()
		{
			return Mathf.Max(0.5f, UnityEngine.Random.Range(
				Plugin.cfgJumpRangeMin.Value, Plugin.cfgJumpRangeMax.Value
			));
		}

		[HarmonyPatch("Update")]
		[HarmonyPrefix]
		private static void SetOffMineAnimation(ref Landmine __instance)
		{
			if(crawlCoutine == null)
				return;

			__instance.StopCoroutine(crawlCoutine);
		}

		private static Coroutine crawlCoutine;

		[HarmonyPatch("Update")]
		[HarmonyPostfix]
		private static void Update(ref Landmine __instance)
		{
			if(__instance.IsServer
			&& jumpTimer.TryGetValue(__instance, out float nextJump)
			&& nextJump < Time.time
			)
			{
				List<Vector3> possible = new();
				IEnumerable<Vector3> hits;
				float section = (360f * Mathf.Deg2Rad) / 8f;
				float range = GetRange();
				for(int i = 0; i < 8; ++i)
				{
					float angle = section * i;
					hits = Raycast(__instance.transform.position + new Vector3(
						Mathf.Cos(angle), 0f, Mathf.Sin(angle)
					) * range);
					possible.AddRange(hits);
				}

				if(possible.Count > 0)
				{
					var newpos = possible[UnityEngine.Random.Range(0, possible.Count)];
					crawlCoutine = __instance.StartCoroutine(CrawlRoutine(__instance, newpos));
					//Plugin.Log($"Jumped! {__instance.transform.position} -> {newpos}");
					jumpTimer[__instance] = Time.time + GetInterval();
				}
				else
				{
					//Plugin.Log("Found 0 possible jump positions, transforming into static...");
					jumpTimer.Remove(__instance);
				}
			}
		}

		private static IEnumerator CrawlRoutine(Landmine mine, Vector3 target)
		{
			if(mine.hasExploded)
				yield break;

			float time = 0.5f;
			Transform mineTransform = mine.transform;
			Vector3 orgPos = mineTransform.position;
			while(time > 0f)
			{
				if(mine.hasExploded)
					yield break;

				float t = 1f - (time / 0.5f);
				var p = Vector3.Lerp(orgPos, target, t);
				mineTransform.position = p;

				time -= Time.deltaTime;
				yield return null;
			}
			mineTransform.position = target;
		}

		private static IEnumerable<Vector3> Raycast(Vector3 pos)
		{
			var hits = Physics.RaycastAll(pos + Vector3.up, Vector3.down, 2.5f);
			if(hits.Length > 0)
				return hits.Select(h => h.point);
			return System.Array.Empty<Vector3>();
		}
	}
}
