using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using Log = KSPCommunityLib.Logging.Log;

namespace SCANsat.SCAN_Reflection
{
	/// <summary>
	/// Heights for bodies whose terrain comes from Mirage, the virtual-texture terrain Sol ships with.
	/// Mirage displaces the PQS from its own tile archive (PQSMod_MirageTerrain) but declares no PQS height
	/// bounds, so the body reads as flat, and its direction lookups drop to a coarse level during on-rails
	/// warp. This samples the archive directly, at the level the caller asks for, through a height layer of
	/// SCANsat's own: Mirage's per-body layer is the cache its terrain is built from, and a whole-globe map
	/// sweep through it would evict the tiles around the vessel. Heights match Mirage's PQSMod exactly;
	/// other PQSMods on the body (Earth's coast smoothing, a few metres) are not included.
	/// Everything here runs on the main thread, as Mirage's own layer does.
	/// </summary>
	static class SCANmirage
	{
		// A layer holds up to 192 decoded tiles (about 53 MB); keep only the bodies in use.
		const int MaxLayers = 3;
		// The default height range is read from this level's tiles: one per cube face.
		const int RangeLevel = 0;
		const int DefaultTileSize = 256;

		static Type x_PQSMod_MirageTerrain_Type = null;
		static FieldInfo x_PQSMod_MirageTerrain_deformity = null;
		static FieldInfo x_PQSMod_MirageTerrain_offset = null;

		static MethodInfo x_MirageBodyRegistry_TryGet = null;
		static PropertyInfo x_MirageBodyConfig_VirtualTexture = null;
		static PropertyInfo x_VirtualTextureConfig_Archive = null;
		static PropertyInfo x_VirtualTextureConfig_StreamingMaxLevel = null;
		static FieldInfo x_VirtualTextureConfig_tileSize = null;
		static MethodInfo x_BodyArchive_CreateCpuHeightLayer = null;

		static MethodInfo x_HeightTileLayer_Sample = null;
		static PropertyInfo x_HeightTileLayer_MaxLevel = null;
		static MethodInfo x_HeightTileLayer_TryLoadNativeWalkUp = null;

		internal static bool MirageLoaded = false;

		// HeightTileLayer.Sample: the height fraction [0, 1] at a raw face UV, or NaN when nothing resolves
		delegate double SampleHeight(int face, double rawU, double rawV, int level);

		class MirageBody
		{
			internal string name;
			internal double deformity, offset;
			internal object layer;   // SCANsat's own HeightTileLayer; null until first sampled or once evicted
			internal SampleHeight sample;
			internal int maxLevel;
			internal int tileSize = DefaultTileSize;
			internal int lastAttempt = -1;
			internal bool broken;
		}

		// A null value marks a body already looked at that is not a Mirage body.
		static readonly Dictionary<CelestialBody, MirageBody> bodies = new Dictionary<CelestialBody, MirageBody>();
		// Bodies holding a layer, most recently sampled last
		static readonly List<MirageBody> layerOrder = new List<MirageBody>();

		internal static void Initialize(AssemblyLoader.LoadedAssembly mirageAssembly)
		{
			try
			{
				Assembly assembly = mirageAssembly.assembly;
				BindingFlags instance = BindingFlags.Instance | BindingFlags.Public;

				x_PQSMod_MirageTerrain_Type = assembly.GetType("Mirage.KopernicusMods.PQSMod_MirageTerrain");
				x_PQSMod_MirageTerrain_deformity = x_PQSMod_MirageTerrain_Type.GetField("deformity", instance);
				x_PQSMod_MirageTerrain_offset = x_PQSMod_MirageTerrain_Type.GetField("offset", instance);

				Type registry_Type = assembly.GetType("Mirage.Configuration.MirageBodyRegistry");
				x_MirageBodyRegistry_TryGet = registry_Type.GetMethod("TryGet", BindingFlags.Static | BindingFlags.Public);
				x_MirageBodyConfig_VirtualTexture = assembly.GetType("Mirage.Configuration.MirageBodyConfig").GetProperty("VirtualTexture", instance);

				Type virtualTexture_Type = assembly.GetType("Mirage.VirtualTexture.VirtualTextureConfig");
				x_VirtualTextureConfig_Archive = virtualTexture_Type.GetProperty("Archive", instance);
				x_VirtualTextureConfig_StreamingMaxLevel = virtualTexture_Type.GetProperty("StreamingMaxLevel", instance);
				x_VirtualTextureConfig_tileSize = virtualTexture_Type.GetField("tileSize", instance);
				x_BodyArchive_CreateCpuHeightLayer = assembly.GetType("Mirage.VirtualTexture.BodyArchive").GetMethod("CreateCpuHeightLayer", instance);

				Type layer_Type = assembly.GetType("Mirage.VirtualTexture.HeightTileLayer");
				x_HeightTileLayer_Sample = layer_Type.GetMethod("Sample", instance, null, new Type[] { typeof(int), typeof(double), typeof(double), typeof(int) }, null);
				x_HeightTileLayer_MaxLevel = layer_Type.GetProperty("MaxLevel", instance);
				x_HeightTileLayer_TryLoadNativeWalkUp = layer_Type.GetMethod("TryLoadNativeWalkUp", instance);
			}
			catch (Exception e)
			{
				Log.Exception(e);
			}

			// tileSize and TryLoadNativeWalkUp are optional: without them, the default tile size and the
			// PQSMod's declared span stand in.
			if (x_PQSMod_MirageTerrain_deformity == null || x_PQSMod_MirageTerrain_offset == null || x_MirageBodyRegistry_TryGet == null
				|| x_MirageBodyConfig_VirtualTexture == null || x_VirtualTextureConfig_Archive == null || x_VirtualTextureConfig_StreamingMaxLevel == null
				|| x_BodyArchive_CreateCpuHeightLayer == null || x_HeightTileLayer_Sample == null || x_HeightTileLayer_MaxLevel == null)
			{
				Log.Error("Failed to initialize Mirage reflection methods; Mirage bodies fall back to PQS altimetry");
			}
			else
			{
				MirageLoaded = true;
			}
		}

		internal static bool IsMirageBody(CelestialBody body)
		{
			return find(body) != null;
		}

		/// <summary>
		/// Terrain height (m above the PQS radius) on a Mirage body. level is the archive level to sample;
		/// negative or beyond the archive's deepest means the deepest. False on any other body, or while
		/// the archive cannot be read, so the caller falls back to PQS.
		/// </summary>
		internal static bool TryGetElevation(CelestialBody body, double lon, double lat, int level, out double elevation)
		{
			elevation = 0;
			MirageBody mb = find(body);
			if (mb == null || !ensureLayer(body, mb))
			{
				return false;
			}

			// The same direction as SCANUtil.getElevation hands to PQS, and the same face coordinates
			// PQSMod_MirageTerrain derives from it.
			double rlon = Mathf.Deg2Rad * lon;
			double rlat = Mathf.Deg2Rad * lat;
			Vector3d rad = new Vector3d(Math.Cos(rlat) * Math.Cos(rlon), Math.Sin(rlat), Math.Cos(rlat) * Math.Sin(rlon));
			PQSMod_GnomonicTest.GetGnomonicMapCoords(rad, out PQS.QuadPlane plane, out double u, out double v);

			double h = mb.sample((int)plane, u, v, level < 0 || level > mb.maxLevel ? mb.maxLevel : level);
			if (double.IsNaN(h))
			{
				return false;
			}

			elevation = mb.offset + mb.deformity * h;
			return true;
		}

		/// <summary>
		/// The coarsest level whose texels are no larger than a map pixel at this many pixels per degree:
		/// a cube face spans 90 degrees in 2^level tiles. Coarser levels are Mirage's own averages, so a
		/// pixel gets the mean height of the ground it covers, cheaply. -1 (the deepest) off Mirage.
		/// </summary>
		internal static int LevelForScale(CelestialBody body, double pixelsPerDegree)
		{
			MirageBody mb = find(body);
			if (mb == null || !ensureLayer(body, mb))
			{
				return -1;
			}

			double tiles = 90 * pixelsPerDegree / mb.tileSize;
			int level = tiles <= 1 ? 0 : (int)Math.Ceiling(Math.Log(tiles, 2));
			return Math.Min(level, mb.maxLevel);
		}

		/// <summary>The span the PQSMod can produce: offset to offset + deformity.</summary>
		internal static bool TryGetDeclaredRange(CelestialBody body, out double min, out double max)
		{
			min = max = 0;
			MirageBody mb = find(body);
			if (mb == null)
			{
				return false;
			}

			min = Math.Min(mb.offset, mb.offset + mb.deformity);
			max = Math.Max(mb.offset, mb.offset + mb.deformity);
			return true;
		}

		/// <summary>
		/// The range the archive's terrain actually covers, read from the six tiles of its coarsest level.
		/// Coarse texels average peaks and floors away, so this is narrower than the true range.
		/// </summary>
		internal static bool TryMeasureRange(CelestialBody body, out double min, out double max)
		{
			min = max = 0;
			MirageBody mb = find(body);
			if (mb == null || x_HeightTileLayer_TryLoadNativeWalkUp == null || !ensureLayer(body, mb))
			{
				return false;
			}

			double lo = double.MaxValue, hi = double.MinValue;
			object[] args = new object[8];

			try
			{
				for (int face = 0; face < 6; face++)
				{
					// At level 0 a face is one tile, so any UV on it names that tile.
					args[0] = face;
					args[1] = 0.5f;
					args[2] = 0.5f;
					args[3] = RangeLevel;

					if (!(bool)x_HeightTileLayer_TryLoadNativeWalkUp.Invoke(mb.layer, args))
					{
						continue;
					}

					NativeArray<float> tile = (NativeArray<float>)args[4];
					try
					{
						for (int i = 0; i < tile.Length; i++)
						{
							float f = tile[i];
							if (float.IsNaN(f))
							{
								continue;
							}

							lo = Math.Min(lo, f);
							hi = Math.Max(hi, f);
						}
					}
					finally
					{
						tile.Dispose();
					}
				}
			}
			catch (Exception e)
			{
				Log.Exception(e);
				return false;
			}

			if (lo > hi)
			{
				return false;
			}

			double a = mb.offset + mb.deformity * lo, b = mb.offset + mb.deformity * hi;
			min = Math.Min(a, b);
			max = Math.Max(a, b);
			return true;
		}

		static MirageBody find(CelestialBody body)
		{
			if (!MirageLoaded || body == null)
			{
				return null;
			}

			if (bodies.TryGetValue(body, out MirageBody mb))
			{
				return mb;
			}

			if (body.pqsController != null)
			{
				PQSMod[] mods = body.pqsController.GetComponentsInChildren<PQSMod>(true);
				for (int i = 0; i < mods.Length; i++)
				{
					PQSMod mod = mods[i];
					if (mod != null && mod.modEnabled && x_PQSMod_MirageTerrain_Type.IsInstanceOfType(mod))
					{
						mb = new MirageBody
						{
							name = body.name,
							deformity = (double)x_PQSMod_MirageTerrain_deformity.GetValue(mod),
							offset = (double)x_PQSMod_MirageTerrain_offset.GetValue(mod),
						};
						break;
					}
				}
			}

			bodies[body] = mb;
			return mb;
		}

		static bool ensureLayer(CelestialBody body, MirageBody mb)
		{
			if (mb.sample != null)
			{
				if (layerOrder[layerOrder.Count - 1] != mb)
				{
					layerOrder.Remove(mb);
					layerOrder.Add(mb);
				}

				return true;
			}

			// Mirage fills its registry at the main menu; until then, retry once a frame rather than per sample.
			if (mb.broken || mb.lastAttempt == Time.frameCount)
			{
				return false;
			}

			mb.lastAttempt = Time.frameCount;

			try
			{
				object[] args = { mb.name, null };
				if (!(bool)x_MirageBodyRegistry_TryGet.Invoke(null, args) || args[1] == null)
				{
					return false;
				}

				object virtualTexture = x_MirageBodyConfig_VirtualTexture.GetValue(args[1], null);
				if (virtualTexture == null)
				{
					mb.broken = true;
					return false;
				}

				object archive = x_VirtualTextureConfig_Archive.GetValue(virtualTexture, null);
				int streamingMaxLevel = (int)x_VirtualTextureConfig_StreamingMaxLevel.GetValue(virtualTexture, null);

				// The depth Mirage builds its own layer with
				object layer = x_BodyArchive_CreateCpuHeightLayer.Invoke(archive, new object[] { streamingMaxLevel });
				if (layer == null)
				{
					Log.Error($"Mirage archive for {mb.name} has no height layer; using PQS altimetry");
					mb.broken = true;
					return false;
				}

				mb.layer = layer;
				mb.sample = (SampleHeight)Delegate.CreateDelegate(typeof(SampleHeight), layer, x_HeightTileLayer_Sample);
				mb.maxLevel = (int)x_HeightTileLayer_MaxLevel.GetValue(layer, null);
				if (x_VirtualTextureConfig_tileSize != null)
				{
					mb.tileSize = Math.Max(1, (int)x_VirtualTextureConfig_tileSize.GetValue(virtualTexture));
				}
			}
			catch (Exception e)
			{
				Log.Exception(e);
				mb.broken = true;
				return false;
			}

			layerOrder.Add(mb);
			while (layerOrder.Count > MaxLayers)
			{
				release(layerOrder[0]);
			}

			Log.Message($"Mirage height layer for {mb.name}: levels 0-{mb.maxLevel}, {mb.tileSize} px tiles");
			return true;
		}

		static void release(MirageBody mb)
		{
			layerOrder.Remove(mb);
			(mb.layer as IDisposable)?.Dispose();
			mb.layer = null;
			mb.sample = null;
		}
	}
}
