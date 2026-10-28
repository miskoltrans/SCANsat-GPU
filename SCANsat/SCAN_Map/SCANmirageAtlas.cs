using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SCANsat.SCAN_Reflection;
using Unity.Collections;
using UnityEngine;
using Log = KSPCommunityLib.Logging.Log;

namespace SCANsat.SCAN_Map
{
	/// <summary>
	/// A Mirage body's colour and normal tiles on the GPU, for its Visual map. Mirage bodies ship no
	/// equirect maps; their surface is a cube of tiles, deeper levels finer, in Mirage's archive. Tiles are
	/// copied into texture atlases as they are stored - block-compressed (BC7 colour, BC5 normals), never
	/// decoded on the CPU - one slot per tile, both layers in the same slots, and an indirection texture
	/// (one texel per tile of the level) names each tile's slot. A map over the whole body gets one level of
	/// all of it, with level 0 beside it to stand in while the level streams in. A window map (the zoom map,
	/// RPM) deep enough that the whole body would not fit gets a coarse whole-body atlas plus a window atlas:
	/// the tiles under its window at the level its scale needs, in a pool of slots, the least recently wanted
	/// making way as the window moves. The
	/// composite shader turns each pixel's lon/lat into a cube face and UV and samples the deepest of those
	/// that holds it (SCANVisualComposite's mirageUV). Mirage reads and decompresses the tiles on a worker
	/// thread; the main thread copies them in, a batch at a time, whenever a map binds the atlas.
	/// </summary>
	internal sealed class SCANmirageAtlas : IDisposable
	{
		// Per layer, the equirect sources' budget (SCANtextures.TextureBudget): a whole body to level 3 at
		// 264 px slots, 27 MB each for colour and normals
		const long MaxBytesPerLayer = 32L * 1048576;
		// A window map's whole-body atlas, drawn while its own tiles land and outside them: two 2 MB atlases
		const int WindowBaseLevel = 1;
		// A window atlas's slots, 10 MB per layer: a 720x480 zoom map needs 20 to 60 tiles at its level
		const int WindowSlots = 128;
		// Atlases kept, most recently bound last: a big map and a zoom map can both be open
		const int MaxAtlases = 4;
		// Past MaxAtlases, an atlas is freed only once no map has bound it for this many frames: one still
		// in use stays, however many there are, rather than be freed and read again every frame
		const int IdleFrames = 120;
		// Tiles copied in per bind, 70 KB per layer each
		const int UploadsPerBind = 64;
		// A one-off render (the Visual export) waits at most this long for its tiles
		const int WaitMilliseconds = 10000;

		static readonly List<SCANmirageAtlas> atlases = new List<SCANmirageAtlas>();
		static readonly HashSet<string> failedBodies = new HashSet<string>();
		// What a body's readers report, taken once; null for a body Mirage does not texture. Maps only draw
		// in flight and the tracking station, after Mirage has read its configs at the main menu.
		static readonly Dictionary<CelestialBody, Layers> bodyLayers = new Dictionary<CelestialBody, Layers>();
		// The same for height tiles (BindHeights); null: none the GPU can read
		static readonly Dictionary<CelestialBody, Layers> bodyHeights = new Dictionary<CelestialBody, Layers>();
		static Vector4[] basisU, basisV;
		static bool basisMeasured, basisFailed;

		/// <summary>
		/// The ground a window map shows, as lon/lat points (its pixels, sampled closer than a tile). The
		/// tiles under it are worked out once per level. owner: the map; each map has its own window atlas,
		/// so two maps cannot take each other's slots.
		/// </summary>
		internal sealed class Window
		{
			readonly List<Vector2d> points;
			readonly Dictionary<int, HashSet<int>> tiles = new Dictionary<int, HashSet<int>>();
			internal readonly object owner;

			internal Window(List<Vector2d> points, object owner)
			{
				this.points = points;
				this.owner = owner;
			}

			internal HashSet<int> TilesAt(int level)
			{
				if (!tiles.TryGetValue(level, out HashSet<int> set))
				{
					set = new HashSet<int>();
					foreach (Vector2d p in points)
					{
						set.Add(tileUnder(p.x, p.y, level));
					}

					tiles[level] = set;
				}

				return set;
			}
		}

		class Layer
		{
			internal string name;
			internal bool linear;
			internal int maxLevel, format, tileSize, border;
		}

		// The layers an atlas holds, a plane each, the first deciding the slots: Visual's colour, then its
		// normals unless the body has none the shader can read (BC5) and Visual is unshaded; or heights
		class Layers
		{
			internal Layer[] planes;
			internal bool heights;
			internal Layer primary { get { return planes[0]; } }
		}

		// One layer's reader and its GPU copy
		sealed class Plane
		{
			internal Layer layer;
			internal IDisposable source;
			internal Texture2D texture, staging;
			internal int missing;
		}

		// A tile to read into a slot: each plane's prepared request, then its read once landed
		sealed class Job
		{
			internal int tile, face, level, tx, ty, slot;
			internal object[] requests, reads;
		}

		enum SlotState : byte { Empty, Reading, Ready, Failed }

		readonly CelestialBody body;
		readonly int level;
		readonly bool window;   // tiles on request into a pool of slots; otherwise the whole body, read once
		readonly bool heights;   // height tiles, for the shader's height pass; otherwise Visual's
		readonly object owner;   // a window's map
		int boundFrame;
		readonly int tileSize, border, slotSize, columns, atlasSize;
		readonly Plane[] planes;   // colour, then normals if the body has them
		readonly Stopwatch clock = Stopwatch.StartNew();

		readonly SlotState[] slotState;
		readonly int[] slotTile;   // the tile of the level a slot holds; -1 for level 0's below a whole-body level
		readonly int[] slotWanted;   // the window update that last wanted the slot's tile
		readonly Dictionary<int, int> tileSlot = new Dictionary<int, int>();
		HashSet<int> wanted;
		int wantedStamp;
		int unplaced;   // tiles of the window with no slot yet: every slot was still reading
		int jobsQueued, jobsDone;

		Texture2D indirection;
		Color32[] indirectionPixels;
		bool indirectionDirty;
		byte[] flatNormal;   // a level BC5 slot, for a normal tile that will not read

		// Worker <-> main thread
		readonly object gate = new object();
		readonly Queue<Job> queue = new Queue<Job>();
		readonly List<Job> landed = new List<Job>();
		bool workerRunning;
		volatile bool cancelled;
		Exception workerError;

		// Nothing asked for is still to land, and every tile the window wants has a slot
		internal bool Complete { get { return jobsDone == jobsQueued && unplaced == 0; } }

		/// <summary>
		/// Point a composite material at the body's tiles, for a map needing targetWidth pixels across 360
		/// degrees, and copy in whatever has landed since the last bind. window: the ground a window map
		/// shows, null for a map of the whole body. False when the body is not a Mirage body or its tiles
		/// cannot be read: the caller uses the body's other sources. shaded: normals are bound too. loading:
		/// tiles asked for are still to land, so the caller should composite again. wait: block until they
		/// have (a one-off render).
		/// </summary>
		internal static bool Bind(Material m, CelestialBody body, int targetWidth, Window window, bool wait, out bool shaded, out bool loading)
		{
			shaded = false;
			loading = false;
			if (!SCANmirage.TilesLoaded || body == null || failedBodies.Contains(body.name) || !measureBasis())
			{
				return false;
			}

			try
			{
				if (!bodyLayers.TryGetValue(body, out Layers layers))
				{
					layers = probe(body);
					bodyLayers[body] = layers;
				}

				if (layers == null)
				{
					return false;
				}

				plan(layers, neededLevel(targetWidth, layers), window, false, out int wholeLevel, out int detailLevel, out HashSet<int> detailTiles);
				bindLayers(m, body, layers, wholeLevel, detailLevel, detailTiles, window, wait, out loading, out SCANmirageAtlas whole, out SCANmirageAtlas detail);
				shaded = whole.planes.Length > 1;
				m.SetTexture("_MirageNormals", shaded ? whole.planes[1].texture : null);
				if (detail != null)
				{
					m.SetTexture("_MirageDetailNormals", shaded ? detail.planes[1].texture : null);
				}

				return true;
			}
			catch (Exception e)
			{
				failedBodies.Add(body.name);
				Log.Exception(e);
				Log.Error($"[{body.name}] Mirage tiles could not be read; Visual uses the body's other sources");
				return false;
			}
		}

		/// <summary>
		/// The same for the body's height tiles at level, the one the CPU samples (SCANmirage.LevelForScale),
		/// into a material for the composite shader's height pass, with _MirageHeightScale and
		/// _MirageHeightOffset, the PQSMod's span (SCANmirage.TryGetHeightScale). False, with nothing read,
		/// when the GPU cannot read that level for this map - no height tiles it can sample, a level deeper
		/// than the tiles it has, or more of them than an atlas holds: the caller samples on the CPU.
		/// </summary>
		internal static bool BindHeights(Material m, CelestialBody body, int level, Window window, out bool loading)
		{
			loading = false;
			string key = body == null ? null : body.name + "/height";
			if (level < 0 || !SCANmirage.TilesLoaded || body == null || failedBodies.Contains(key) || !measureBasis()
				|| !SCANmirage.TryGetHeightScale(body, out double deformity, out double offset))
			{
				return false;
			}

			try
			{
				if (!bodyHeights.TryGetValue(body, out Layers layers))
				{
					layers = probeHeights(body);
					bodyHeights[body] = layers;
				}

				if (layers == null || level > layers.primary.maxLevel || 6 << level > SystemInfo.maxTextureSize
					|| !plan(layers, level, window, true, out int wholeLevel, out int detailLevel, out HashSet<int> detailTiles))
				{
					return false;
				}

				bindLayers(m, body, layers, wholeLevel, detailLevel, detailTiles, window, false, out loading, out _, out _);
				m.SetFloat("_MirageHeightScale", (float)deformity);
				m.SetFloat("_MirageHeightOffset", (float)offset);
				return true;
			}
			catch (Exception e)
			{
				failedBodies.Add(key);
				Log.Exception(e);
				Log.Error($"[{body.name}] Mirage height tiles could not be read; heights are sampled on the CPU");
				return false;
			}
		}

		// The levels a map needing level needed reads, before anything is made: the whole body's, and over it a
		// window's own (detailLevel, -1 for none). A window map at a level whose whole body would not fit a
		// window's slots anyway gets its own tiles, at the deepest level whose share of them does; shallower,
		// the whole body is cheaper, and shared. exact: needed itself or nothing (the height pass, which must
		// read the level the CPU samples) - false when the plan falls short of it.
		static bool plan(Layers layers, int needed, Window window, bool exact, out int wholeLevel, out int detailLevel, out HashSet<int> detailTiles)
		{
			wholeLevel = Math.Min(needed, wholeBodyLevel(layers));
			detailLevel = -1;
			detailTiles = null;
			if (window != null && needed > WindowBaseLevel && slotsFor(needed) > WindowSlots)
			{
				for (int l = needed; l > WindowBaseLevel && (!exact || l == needed); l--)
				{
					HashSet<int> tiles = window.TilesAt(l);
					if (tiles.Count <= WindowSlots)
					{
						detailLevel = l;
						detailTiles = tiles;
						wholeLevel = WindowBaseLevel;
						break;
					}
				}
			}

			return !exact || (detailLevel >= 0 ? detailLevel : wholeLevel) == needed;
		}

		// The planned atlases - the whole body's, and a window's own over them - pumped, and their first plane
		// bound with the slot geometry
		static void bindLayers(Material m, CelestialBody body, Layers layers, int wholeLevel, int detailLevel, HashSet<int> detailTiles, Window window, bool wait,
			out bool loading, out SCANmirageAtlas whole, out SCANmirageAtlas detail)
		{
			detail = null;
			if (detailLevel >= 0)
			{
				detail = find(body, layers, detailLevel, window.owner);
				detail.require(detailTiles);
			}

			whole = find(body, layers, wholeLevel, null);
			whole.pump(wait);
			if (detail != null)
			{
				detail.pump(wait);
				// Waited for: every slot has landed, so the tiles that found none can have one now
				if (wait && detail.unplaced > 0)
				{
					detail.require(detailTiles);
					detail.pump(true);
				}
			}

			loading = !whole.Complete || (detail != null && !detail.Complete);

			m.SetTexture("_MirageTiles", whole.planes[0].texture);
			m.SetTexture("_MirageIndirection", whole.indirection);
			m.SetVectorArray("_MirageU", basisU);
			m.SetVectorArray("_MirageV", basisV);
			m.SetVector("_MirageTile", new Vector4(1 << whole.level, whole.tileSize, whole.border, whole.slotSize));
			m.SetVector("_MirageAtlas", new Vector4(whole.columns, 1f / whole.atlasSize, 0, 0));

			m.SetFloat("_MirageDetail", detail != null ? 1f : 0f);
			if (detail != null)
			{
				m.SetTexture("_MirageDetailTiles", detail.planes[0].texture);
				m.SetTexture("_MirageDetailIndirection", detail.indirection);
				m.SetFloat("_MirageDetailLevel", 1 << detail.level);
				m.SetVector("_MirageDetailAtlas", new Vector4(detail.columns, 1f / detail.atlasSize, 0, 0));
			}
		}

		/// <summary>
		/// Free every atlas: GPU memory, Mirage readers, worker threads. A body whose tiles failed is tried
		/// again in the next scene, so a passing fault does not cost it its Visual for the rest of the game.
		/// </summary>
		internal static void ReleaseAll()
		{
			for (int i = atlases.Count - 1; i >= 0; i--)
			{
				atlases[i].Dispose();
			}

			atlases.Clear();
			failedBodies.Clear();
			bodyLayers.Clear();
			bodyHeights.Clear();
		}

		static bool measureBasis()
		{
			if (!basisMeasured)
			{
				basisMeasured = true;
				basisFailed = !SCANmirage.TryMeasureCubeBasis(out basisU, out basisV);
				if (basisFailed)
				{
					Log.Error("Mirage's cube mapping could not be measured; Visual maps of Mirage bodies use their other sources");
				}
			}

			return !basisFailed;
		}

		// The tile of a level under a lon/lat (degrees), as the indirection texture indexes it: the shader's
		// mirageFace, on the CPU
		static int tileUnder(double lon, double lat, int level)
		{
			double rlat = lat * Mathf.Deg2Rad, rlon = lon * Mathf.Deg2Rad;
			double x = Math.Cos(rlat) * Math.Cos(rlon), y = Math.Sin(rlat), z = Math.Cos(rlat) * Math.Sin(rlon);
			double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);

			// Mirage's tie order: X, then Y, then Z
			int axis;
			double major;
			if (ax >= ay && ax >= az) { axis = x >= 0 ? 0 : 1; major = ax; }
			else if (ay >= az) { axis = y >= 0 ? 2 : 3; major = ay; }
			else { axis = z >= 0 ? 4 : 5; major = az; }

			Vector4 bu = basisU[axis], bv = basisV[axis];
			double u = Math.Max(0, Math.Min(1, ((x * bu.x + y * bu.y + z * bu.z) / major + 1) / 2));
			double v = Math.Max(0, Math.Min(1, ((x * bv.x + y * bv.y + z * bv.z) / major + 1) / 2));

			int n = 1 << level;
			int tx = Math.Min((int)(u * n), n - 1), ty = Math.Min((int)(v * n), n - 1);
			return ((int)bu.w * n + ty) * n + tx;
		}

		// The atlas for this body and level - the whole body's, or the window of owner's map - made on first
		// use. A map's window at another level makes way for the new one.
		static SCANmirageAtlas find(CelestialBody body, Layers layers, int level, object owner)
		{
			bool window = owner != null;
			for (int i = 0; i < atlases.Count; i++)
			{
				SCANmirageAtlas a = atlases[i];
				if (a.body != body || a.heights != layers.heights || a.window != window || !ReferenceEquals(a.owner, owner))
				{
					continue;
				}

				if (a.level == level)
				{
					atlases.RemoveAt(i);
					atlases.Add(a);
					a.boundFrame = Time.frameCount;
					return a;
				}

				if (window)
				{
					a.Dispose();
					atlases.RemoveAt(i);
					break;
				}
			}

			SCANmirageAtlas made = new SCANmirageAtlas(body, layers, level, owner);
			made.boundFrame = Time.frameCount;
			atlases.Add(made);
			// Least recently bound first; the one just made is last
			for (int i = 0; atlases.Count > MaxAtlases && i < atlases.Count - 1;)
			{
				if (Time.frameCount - atlases[i].boundFrame > IdleFrames)
				{
					atlases[i].Dispose();
					atlases.RemoveAt(i);
				}
				else
				{
					i++;
				}
			}

			return made;
		}

		// What the body's readers say about its layers: depth, format, tile size. Null when it has no
		// colour layer this device can sample.
		static Layers probe(CelestialBody body)
		{
			Layer color = probe(body, "Color", false);
			if (color == null)
			{
				return null;
			}

			if (!SystemInfo.SupportsTextureFormat((TextureFormat)color.format) || (SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) == 0)
			{
				Log.Error($"[{body.name}] Mirage colour tiles are {(TextureFormat)color.format}, which this graphics device cannot sample or copy; Visual uses the body's other sources");
				return null;
			}

			// The shader reads normals the way Mirage stores them in BC5: east in red, north in green. Without
			// them, Visual is still drawn, unshaded.
			Layer normal = null;
			try
			{
				normal = probe(body, "Normal", true);
			}
			catch (Exception e)
			{
				Log.Exception(e);
			}

			if (normal != null && ((TextureFormat)normal.format != TextureFormat.BC5 || !SystemInfo.SupportsTextureFormat(TextureFormat.BC5)
				|| normal.tileSize != color.tileSize || normal.border != color.border))
			{
				Log.Warning($"[{body.name}] Mirage normal tiles are {(TextureFormat)normal.format}, {normal.tileSize} px; Visual is drawn without shading");
				normal = null;
			}

			return new Layers { planes = normal != null ? new[] { color, normal } : new[] { color } };
		}

		// Height tiles the shader can read: 16-bit fractions of the PQSMod's span, read texel by texel (the
		// height pass filters them itself, as Mirage's CPU sample does)
		static Layers probeHeights(CelestialBody body)
		{
			Layer height = probe(body, "Height", true);
			if (height == null)
			{
				return null;
			}

			if ((TextureFormat)height.format != TextureFormat.R16 || !SystemInfo.SupportsTextureFormat(TextureFormat.R16)
				|| (SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) == 0)
			{
				Log.Warning($"[{body.name}] Mirage height tiles are {(TextureFormat)height.format}, which the GPU path does not read here; heights are sampled on the CPU");
				return null;
			}

			return new Layers { planes = new[] { height }, heights = true };
		}

		// A layer's reader, and one of its level-0 tiles read to check that it is the size its layout says:
		// a tile size or border that SCANsat read wrongly from Mirage would make every tile garbage
		static Layer probe(CelestialBody body, string name, bool linear)
		{
			using (IDisposable source = SCANmirage.CreateTileSource(body, name, linear, out int maxLevel, out int format, out int tileSize, out int border))
			{
				if (source == null)
				{
					return null;
				}

				object read = SCANmirage.ReadTile(SCANmirage.PrepareTile(source, 0, 0, 0, 0));
				try
				{
					byte[] data = SCANmirage.TileBytes(read, out int tileFormat);
					long expected = slotBytesOf((TextureFormat)format, tileSize + 2 * border);
					if (data == null || tileFormat != format || data.Length != expected)
					{
						Log.Error($"[{body.name}] Mirage {name} tile 0 is {data?.Length ?? 0} bytes of {(TextureFormat)tileFormat}, not the {expected} bytes "
							+ $"of {(TextureFormat)format} at {tileSize} px + {border} px border that SCANsat reads it as");
						return null;
					}
				}
				finally
				{
					SCANmirage.ReleaseTile(read);
				}

				return new Layer { name = name, linear = linear, maxLevel = maxLevel, format = format, tileSize = tileSize, border = border };
			}
		}

		// The coarsest level whose texels are no larger than a map pixel - a face spans 90 degrees in 2^level
		// tiles - within the archive's depth
		static int neededLevel(int targetWidth, Layers layers)
		{
			double tiles = targetWidth / 4.0 / layers.primary.tileSize;
			int level = tiles <= 1 ? 0 : (int)Math.Ceiling(Math.Log(tiles, 2));
			foreach (Layer layer in layers.planes)
			{
				level = Math.Min(level, layer.maxLevel);
			}

			// The indirection has a texel per tile of the level, six faces tall
			while (level > 0 && 6 << level > SystemInfo.maxTextureSize)
			{
				level--;
			}

			return level;
		}

		// The deepest level the whole body fits the budget at
		static int wholeBodyLevel(Layers layers)
		{
			long slotBytes = slotBytesOf((TextureFormat)layers.primary.format, layers.primary.tileSize + 2 * layers.primary.border);
			int level = 0;
			while (slotsFor(level + 1) * slotBytes <= MaxBytesPerLayer)
			{
				level++;
			}

			return level;
		}

		// A whole-body atlas: level 0's six tiles, then the level's own
		static int slotsFor(int level)
		{
			return level == 0 ? 6 : 6 + 6 * (1 << (2 * level));
		}

		static long slotBytesOf(TextureFormat format, int slotSize)
		{
			switch (format)
			{
				case TextureFormat.BC4:
				case TextureFormat.DXT1:
					return (long)slotSize * slotSize / 2;
				case TextureFormat.BC5:
				case TextureFormat.BC6H:
				case TextureFormat.BC7:
				case TextureFormat.DXT5:
				case TextureFormat.R8:
				case TextureFormat.Alpha8:
					return (long)slotSize * slotSize;
				case TextureFormat.R16:
					return (long)slotSize * slotSize * 2;
				default:
					return (long)slotSize * slotSize * 4;
			}
		}

		SCANmirageAtlas(CelestialBody body, Layers layers, int level, object owner)
		{
			this.body = body;
			this.level = level;
			this.owner = owner;
			window = owner != null;
			heights = layers.heights;
			tileSize = layers.primary.tileSize;
			border = layers.primary.border;
			slotSize = tileSize + 2 * border;

			int slots = window ? WindowSlots : slotsFor(level);
			columns = (int)Math.Ceiling(Math.Sqrt(slots));
			atlasSize = columns * slotSize;
			slotState = new SlotState[slots];
			slotTile = new int[slots];
			slotWanted = new int[slots];

			List<Plane> made = new List<Plane>(2);
			try
			{
				foreach (Layer layer in layers.planes)
				{
					Plane p = new Plane { layer = layer };
					made.Add(p);
					p.source = SCANmirage.CreateTileSource(body, layer.name, layer.linear, out _, out _, out _, out _);
					if (p.source == null)
					{
						throw new InvalidOperationException("Mirage " + layer.name + " reader for " + body.name + " went away");
					}

					TextureFormat format = (TextureFormat)layer.format;
					// Created unreadable: the tiles are copied in on the GPU, and a CPU copy would only double the memory
					p.texture = new Texture2D(atlasSize, atlasSize, format, false, layer.linear)
					{
						name = "SCANsat Mirage " + layer.name + " " + body.name + (window ? " window L" : " L") + level,
						// Heights are read texel by texel and filtered in the shader
						filterMode = heights ? FilterMode.Point : FilterMode.Bilinear,
						wrapMode = TextureWrapMode.Clamp,
					};
					p.texture.Apply(false, true);
					p.staging = new Texture2D(slotSize, slotSize, format, false, layer.linear);
				}
			}
			catch
			{
				foreach (Plane p in made)
				{
					disposeSource(p);
					destroyTextures(p);
				}

				throw;
			}

			planes = made.ToArray();
			try
			{
				start(slots);
			}
			catch
			{
				Dispose();
				throw;
			}
		}

		// The indirection, then for a whole body every tile queued and the worker started
		void start(int slots)
		{
			// One texel per tile of the level, faces stacked: x = tx, y = face * n + ty. rg = the tile's slot
			// column and row, a = 255 once it has landed.
			int n = 1 << level;
			indirection = new Texture2D(n, 6 * n, TextureFormat.RGBA32, false, true)
			{
				name = "SCANsat Mirage indirection " + body.name + (window ? " window L" : " L") + level,
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp,
			};
			indirectionPixels = new Color32[n * 6 * n];
			indirection.SetPixels32(indirectionPixels);
			indirection.Apply(false);

			long bytes = 0;
			foreach (Plane p in planes)
			{
				bytes += slots * slotBytesOf((TextureFormat)p.layer.format, slotSize);
			}

			Log.Message($"[{body.name}] Mirage {(heights ? "heights" : "Visual")}: {(window ? "window" : "whole body")} at level {level}, {slots} slots of {slotSize} px, "
				+ $"{(heights ? "heights" : planes.Length > 1 ? "colour and normals" : "colour only")} ({atlasSize} px atlases, {bytes / 1048576.0:F1} MB)");

			if (window)
			{
				return;
			}

			// The whole body: level 0 first, in slots 0-5 (slot = face) - the shader's fallback for a tile still
			// to land - then the level's own. The worker starts once they are all queued: it lets the readers go
			// when it finds the queue empty.
			for (int face = 0; face < 6; face++)
			{
				int tile = level == 0 ? face : -1;
				request(face, 0, 0, 0, face, tile, false);
			}

			if (level > 0)
			{
				for (int tile = 0; tile < 6 * n * n; tile++)
				{
					request(tile / (n * n), level, tile % n, tile / n % n, 6 + tile, tile, false);
				}
			}

			startWorker();
		}

		// A window's tiles: keep the ones already in, read the rest into free slots or the slots of the least
		// recently wanted tiles not in this window. A slot still reading cannot be taken, so a tile can find
		// none for now: it draws from the whole-body atlas, and is placed again on a later bind.
		void require(HashSet<int> tiles)
		{
			bool again = ReferenceEquals(tiles, wanted);
			if (again && unplaced == 0)
			{
				return;
			}

			wanted = tiles;
			wantedStamp++;

			List<int> missing = new List<int>();
			foreach (int tile in tiles)
			{
				if (tileSlot.TryGetValue(tile, out int slot))
				{
					slotWanted[slot] = wantedStamp;
				}
				else
				{
					missing.Add(tile);
				}
			}

			int n = 1 << level, placed = 0;
			foreach (int tile in missing)
			{
				int slot = freeSlot();
				if (slot < 0)
				{
					break;
				}

				slotWanted[slot] = wantedStamp;
				request(tile / (n * n), level, tile % n, tile / n % n, slot, tile);
				placed++;
			}

			unplaced = missing.Count - placed;
			if (again && placed == 0)
			{
				return;
			}

			SCANUtil.SCANlog("[{0}] Mirage {5} window at level {1}: {2} tiles, {3} to read{4}", body.name, level, tiles.Count, placed,
				placed < missing.Count ? string.Format(", {0} without a slot", missing.Count - placed) : "", heights ? "heights" : "Visual");
		}

		// An empty slot, else the least recently wanted landed one this window does not want
		int freeSlot()
		{
			int best = -1;
			for (int s = 0; s < slotState.Length; s++)
			{
				if (slotState[s] == SlotState.Empty)
				{
					return s;
				}

				if (slotState[s] != SlotState.Reading && slotWanted[s] != wantedStamp && (best < 0 || slotWanted[s] < slotWanted[best]))
				{
					best = s;
				}
			}

			if (best >= 0)
			{
				// Evict: the indirection forgets the tile, so the shader falls back until the slot is refilled
				int old = slotTile[best];
				tileSlot.Remove(old);
				indirectionPixels[old] = default(Color32);
				indirectionDirty = true;
				slotState[best] = SlotState.Empty;
			}

			return best;
		}

		// Queue one tile for the worker, and start it unless told not to yet. Mirage resolves tiles on the
		// main thread only.
		void request(int face, int tileLevel, int tx, int ty, int slot, int tile, bool start = true)
		{
			Job job = new Job { tile = tile, face = face, level = tileLevel, tx = tx, ty = ty, slot = slot, requests = new object[planes.Length], reads = new object[planes.Length] };
			for (int i = 0; i < planes.Length; i++)
			{
				job.requests[i] = SCANmirage.PrepareTile(planes[i].source, face, tileLevel, tx, ty);
			}

			slotState[slot] = SlotState.Reading;
			slotTile[slot] = tile;
			if (tile >= 0)
			{
				tileSlot[tile] = slot;
			}

			jobsQueued++;
			lock (gate)
			{
				queue.Enqueue(job);
			}

			if (start)
			{
				startWorker();
			}
		}

		void startWorker()
		{
			lock (gate)
			{
				if (!workerRunning && queue.Count > 0)
				{
					workerRunning = true;
					ThreadPool.QueueUserWorkItem(_ => readAll());
				}
			}
		}

		// Worker thread: read and decompress queued tiles until the queue is empty
		void readAll()
		{
			while (true)
			{
				Job job;
				lock (gate)
				{
					if (cancelled || queue.Count == 0)
					{
						workerRunning = false;
						// A whole-body atlas reads its tiles once: its readers (their open files) are done with.
						// A window keeps them for the next window.
						if (cancelled || !window)
						{
							foreach (Plane p in planes)
							{
								disposeSource(p);
							}
						}

						return;
					}

					job = queue.Dequeue();
				}

				for (int i = 0; i < planes.Length; i++)
				{
					try
					{
						job.reads[i] = SCANmirage.ReadTile(job.requests[i]);
					}
					catch (Exception e)
					{
						lock (gate)
						{
							if (workerError == null)
							{
								workerError = e;
							}
						}
					}
				}

				lock (gate)
				{
					if (cancelled)
					{
						releaseReads(job);
					}
					else
					{
						landed.Add(job);
					}
				}
			}
		}

		// Main thread: copy landed tiles into the atlases
		void pump(bool wait)
		{
			Stopwatch waited = Stopwatch.StartNew();
			bool wasComplete = Complete;
			while (jobsDone < jobsQueued)
			{
				List<Job> batch = new List<Job>();
				lock (gate)
				{
					int take = wait ? landed.Count : Math.Min(landed.Count, UploadsPerBind);
					batch.AddRange(landed.GetRange(0, take));
					landed.RemoveRange(0, take);
				}

				foreach (Job job in batch)
				{
					upload(job);
					jobsDone++;
				}

				if (!wait || jobsDone == jobsQueued || waited.ElapsedMilliseconds > WaitMilliseconds)
				{
					break;
				}

				Thread.Sleep(1);
			}

			if (indirectionDirty)
			{
				indirection.SetPixels32(indirectionPixels);
				indirection.Apply(false);
				indirectionDirty = false;
			}

			if (workerError != null)
			{
				Log.Exception(workerError);
				workerError = null;
			}

			if (Complete && !wasComplete)
			{
				foreach (Plane p in planes)
				{
					if (p.missing > 0)
					{
						Log.Error($"[{body.name}] Mirage {p.layer.name}: {p.missing} tiles at level {level} could not be read; "
							+ (p == planes[0] ? "a coarser level stands in for them" : "they are drawn unshaded"));
						p.missing = 0;
					}
				}

				if (!window)
				{
					Log.Message($"[{body.name}] Mirage {(heights ? "heights" : "Visual")}: whole body at level {level} on the GPU in {clock.Elapsed.TotalSeconds:F2} s");
				}
			}
		}

		// A job's tile of every layer into its slot. The colour tile decides whether the slot counts as
		// landed; a normal tile that will not read is left level, so it cannot shade its colour wrongly.
		void upload(Job job)
		{
			int sx = job.slot % columns, sy = job.slot / columns;
			bool landedColor = false;

			try
			{
				for (int i = 0; i < planes.Length; i++)
				{
					Plane p = planes[i];
					byte[] data = SCANmirage.TileBytes(job.reads[i], out int tileFormat);
					NativeArray<byte> raw = p.staging.GetRawTextureData<byte>();
					if (data == null || tileFormat != p.layer.format || data.Length != raw.Length)
					{
						p.missing++;
						if (i == 0)
						{
							continue;
						}

						data = flatBC5(raw.Length);
					}

					NativeArray<byte>.Copy(data, raw, raw.Length);
					p.staging.Apply(false);
					Graphics.CopyTexture(p.staging, 0, 0, 0, 0, slotSize, slotSize, p.texture, 0, 0, sx * slotSize, sy * slotSize);
					landedColor |= i == 0;
				}
			}
			finally
			{
				releaseReads(job);
			}

			slotState[job.slot] = landedColor ? SlotState.Ready : SlotState.Failed;
			if (landedColor && job.tile >= 0)
			{
				indirectionPixels[job.tile] = new Color32((byte)sx, (byte)sy, 0, 255);
				indirectionDirty = true;
			}
		}

		static void releaseReads(Job job)
		{
			for (int i = 0; i < job.reads.Length; i++)
			{
				SCANmirage.ReleaseTile(job.reads[i]);
				job.reads[i] = null;
			}
		}

		// A BC5 slot of level normals: every block's two channels at Mirage's flat value, 128
		byte[] flatBC5(int length)
		{
			if (flatNormal == null || flatNormal.Length != length)
			{
				flatNormal = new byte[length];
				for (int b = 0; b < length; b += 8)
				{
					flatNormal[b] = 128;
					flatNormal[b + 1] = 128;
				}
			}

			return flatNormal;
		}

		static void disposeSource(Plane p)
		{
			p.source?.Dispose();
			p.source = null;
		}

		static void destroyTextures(Plane p)
		{
			if (p.texture != null) { UnityEngine.Object.Destroy(p.texture); p.texture = null; }
			if (p.staging != null) { UnityEngine.Object.Destroy(p.staging); p.staging = null; }
		}

		public void Dispose()
		{
			lock (gate)
			{
				cancelled = true;
				queue.Clear();
				foreach (Job job in landed)
				{
					releaseReads(job);
				}

				landed.Clear();

				// A running worker disposes the readers on its way out
				if (!workerRunning)
				{
					foreach (Plane p in planes)
					{
						disposeSource(p);
					}
				}
			}

			foreach (Plane p in planes)
			{
				destroyTextures(p);
			}

			if (indirection != null) { UnityEngine.Object.Destroy(indirection); indirection = null; }
		}
	}
}
