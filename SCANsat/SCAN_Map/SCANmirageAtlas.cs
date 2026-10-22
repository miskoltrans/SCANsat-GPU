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
	/// equirect maps; their surface is a cube of tiles, deeper levels finer, in Mirage's archive. One level
	/// of the whole body is copied into texture atlases as it is stored - block-compressed (BC7 colour, BC5
	/// normals), never decoded on the CPU - with the six level-0 tiles beside it, which stand in while the
	/// level streams in. Both layers share one slot layout. The composite shader turns each pixel's lon/lat
	/// into a cube face and UV, finds the tile's slot in the indirection texture (one texel per tile of the
	/// level) and samples it (SCANVisualComposite's mirageUV). Mirage reads and decompresses the tiles on a
	/// worker thread; the main thread copies them in, a batch at a time, whenever a map binds the atlas.
	/// </summary>
	internal sealed class SCANmirageAtlas : IDisposable
	{
		// Per layer, the equirect sources' budget (SCANtextures.TextureBudget): a whole body to level 3 at
		// 264 px slots, 27 MB each for colour and normals
		const long MaxBytesPerLayer = 32L * 1048576;
		// Atlases kept, most recently bound last: a big map and a zoom map can want different levels of one body
		const int MaxAtlases = 3;
		// Tiles copied in per bind, 70 KB per layer each
		const int UploadsPerBind = 64;
		// A one-off render (the Visual export) waits at most this long for its tiles
		const int WaitMilliseconds = 10000;

		static readonly List<SCANmirageAtlas> atlases = new List<SCANmirageAtlas>();
		static readonly HashSet<string> failedBodies = new HashSet<string>();
		// What a body's readers report, taken once; null for a body Mirage does not texture. Maps only draw
		// in flight and the tracking station, after Mirage has read its configs at the main menu.
		static readonly Dictionary<CelestialBody, Layers> bodyLayers = new Dictionary<CelestialBody, Layers>();
		static Vector4[] basisU, basisV;
		static bool basisMeasured, basisFailed;

		class Layer
		{
			internal string name;
			internal bool linear;
			internal int maxLevel, format, tileSize, border;
		}

		// normal is null when the body has no normals the shader can read (BC5), and Visual is unshaded
		class Layers
		{
			internal Layer color, normal;
		}

		// One layer's reader, its prepared requests and landed reads (by job), and its GPU copy
		sealed class Plane
		{
			internal Layer layer;
			internal IDisposable source;
			internal object[] requests, reads;
			internal Texture2D texture, staging;
			internal int missing;
		}

		struct Job
		{
			internal int face, level, tx, ty, slot;
		}

		readonly CelestialBody body;
		readonly int level;
		readonly int tileSize, border, slotSize, columns, atlasSize;
		readonly Job[] jobs;
		readonly Plane[] planes;   // colour, then normals if the body has them
		readonly Stopwatch clock = Stopwatch.StartNew();

		Texture2D indirection;
		Color32[] indirectionPixels;
		bool indirectionDirty;
		byte[] flatNormal;   // a level BC5 slot, for a normal tile that will not read

		// Worker -> main thread. A plane's reads[i] is job i's TileRead once landed; landedCount only grows.
		readonly object gate = new object();
		int landedCount, uploadedCount;
		bool workerRunning;
		volatile bool cancelled;
		Exception workerError;

		internal bool Complete { get { return uploadedCount == jobs.Length; } }

		/// <summary>
		/// Point a composite material at the body's tiles, for a map needing targetWidth pixels across 360
		/// degrees, and copy in whatever has landed since the last bind. False when the body is not a Mirage
		/// body or its tiles cannot be read: the caller uses the body's other sources. shaded: normals are
		/// bound too. loading: the level is still streaming in, so the caller should composite again. wait:
		/// block until it has all landed (a one-off render).
		/// </summary>
		internal static bool Bind(Material m, CelestialBody body, int targetWidth, bool wait, out bool shaded, out bool loading)
		{
			shaded = false;
			loading = false;
			if (!SCANmirage.TilesLoaded || body == null || failedBodies.Contains(body.name) || !measureBasis())
			{
				return false;
			}

			SCANmirageAtlas a = find(body, targetWidth);
			if (a == null)
			{
				return false;
			}

			a.pump(wait);
			loading = !a.Complete;
			shaded = a.planes.Length > 1;

			m.SetTexture("_MirageTiles", a.planes[0].texture);
			m.SetTexture("_MirageNormals", shaded ? a.planes[1].texture : null);
			m.SetTexture("_MirageIndirection", a.indirection);
			m.SetVectorArray("_MirageU", basisU);
			m.SetVectorArray("_MirageV", basisV);
			m.SetVector("_MirageTile", new Vector4(1 << a.level, a.tileSize, a.border, a.slotSize));
			m.SetVector("_MirageAtlas", new Vector4(a.columns, 1f / a.atlasSize, 0, 0));
			return true;
		}

		/// <summary>Free every atlas: GPU memory, Mirage readers, worker threads.</summary>
		internal static void ReleaseAll()
		{
			for (int i = atlases.Count - 1; i >= 0; i--)
			{
				atlases[i].Dispose();
			}

			atlases.Clear();
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

		// The atlas for this body at the level a map this wide needs, made on first use
		static SCANmirageAtlas find(CelestialBody body, int targetWidth)
		{
			try
			{
				if (!bodyLayers.TryGetValue(body, out Layers layers))
				{
					layers = probe(body);
					bodyLayers[body] = layers;
				}

				if (layers == null)
				{
					return null;
				}

				int level = levelFor(targetWidth, layers);
				for (int i = 0; i < atlases.Count; i++)
				{
					SCANmirageAtlas a = atlases[i];
					if (a.body == body && a.level == level)
					{
						atlases.RemoveAt(i);
						atlases.Add(a);
						return a;
					}
				}

				SCANmirageAtlas made = new SCANmirageAtlas(body, layers, level);
				atlases.Add(made);
				while (atlases.Count > MaxAtlases)
				{
					atlases[0].Dispose();
					atlases.RemoveAt(0);
				}

				return made;
			}
			catch (Exception e)
			{
				failedBodies.Add(body.name);
				Log.Exception(e);
				Log.Error($"[{body.name}] Mirage tiles could not be read; Visual uses the body's other sources");
				return null;
			}
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

			if (!SystemInfo.SupportsTextureFormat((TextureFormat)color.format))
			{
				Log.Error($"[{body.name}] Mirage colour tiles are {(TextureFormat)color.format}, which this graphics device cannot sample; Visual uses the body's other sources");
				return null;
			}

			// The shader reads normals the way Mirage stores them in BC5: east in red, north in green
			Layer normal = probe(body, "Normal", true);
			if (normal != null && ((TextureFormat)normal.format != TextureFormat.BC5 || !SystemInfo.SupportsTextureFormat(TextureFormat.BC5)
				|| normal.tileSize != color.tileSize || normal.border != color.border))
			{
				Log.Warning($"[{body.name}] Mirage normal tiles are {(TextureFormat)normal.format}, {normal.tileSize} px; Visual is drawn without shading");
				normal = null;
			}

			return new Layers { color = color, normal = normal };
		}

		static Layer probe(CelestialBody body, string name, bool linear)
		{
			using (IDisposable source = SCANmirage.CreateTileSource(body, name, linear, out int maxLevel, out int format, out int tileSize, out int border))
			{
				return source == null ? null : new Layer { name = name, linear = linear, maxLevel = maxLevel, format = format, tileSize = tileSize, border = border };
			}
		}

		// The coarsest level whose texels are no larger than a map pixel - a face spans 90 degrees in 2^level
		// tiles - within the archive's depth and the memory budget.
		static int levelFor(int targetWidth, Layers layers)
		{
			Layer color = layers.color;
			double tiles = targetWidth / 4.0 / color.tileSize;
			int level = tiles <= 1 ? 0 : (int)Math.Ceiling(Math.Log(tiles, 2));
			level = Math.Min(level, color.maxLevel);
			if (layers.normal != null)
			{
				level = Math.Min(level, layers.normal.maxLevel);
			}

			long slotBytes = slotBytesOf((TextureFormat)color.format, color.tileSize + 2 * color.border);
			while (level > 0 && slotsFor(level) * slotBytes > MaxBytesPerLayer)
			{
				level--;
			}

			return level;
		}

		// Level 0's six tiles, then the level's own
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
				default:
					return (long)slotSize * slotSize * 4;
			}
		}

		SCANmirageAtlas(CelestialBody body, Layers layers, int level)
		{
			this.body = body;
			this.level = level;
			tileSize = layers.color.tileSize;
			border = layers.color.border;
			slotSize = tileSize + 2 * border;

			int slots = slotsFor(level);
			columns = (int)Math.Ceiling(Math.Sqrt(slots));
			atlasSize = columns * slotSize;

			// Level 0 first, in slots 0-5 (slot = face): the shader's fallback for a tile still to land
			List<Job> list = new List<Job>(slots);
			for (int face = 0; face < 6; face++)
			{
				list.Add(new Job { face = face, level = 0, slot = face });
			}

			int n = 1 << level;
			if (level > 0)
			{
				for (int face = 0; face < 6; face++)
				{
					for (int ty = 0; ty < n; ty++)
					{
						for (int tx = 0; tx < n; tx++)
						{
							list.Add(new Job { face = face, level = level, tx = tx, ty = ty, slot = list.Count });
						}
					}
				}
			}

			jobs = list.ToArray();

			List<Plane> made = new List<Plane>(2);
			try
			{
				foreach (Layer layer in new Layer[] { layers.color, layers.normal })
				{
					if (layer == null)
					{
						continue;
					}

					Plane p = new Plane { layer = layer };
					made.Add(p);
					p.source = SCANmirage.CreateTileSource(body, layer.name, layer.linear, out _, out _, out _, out _);
					if (p.source == null)
					{
						throw new InvalidOperationException("Mirage " + layer.name + " reader for " + body.name + " went away");
					}

					// Mirage resolves tiles on the main thread only
					p.requests = new object[jobs.Length];
					p.reads = new object[jobs.Length];
					for (int i = 0; i < jobs.Length; i++)
					{
						Job j = jobs[i];
						p.requests[i] = SCANmirage.PrepareTile(p.source, j.face, j.level, j.tx, j.ty);
					}

					TextureFormat format = (TextureFormat)layer.format;
					// Created unreadable: the tiles are copied in on the GPU, and a CPU copy would only double the memory
					p.texture = new Texture2D(atlasSize, atlasSize, format, false, layer.linear)
					{
						name = "SCANsat Mirage " + layer.name + " " + body.name + " L" + level,
						filterMode = FilterMode.Bilinear,
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
					disposePlane(p);
				}

				throw;
			}

			planes = made.ToArray();

			// One texel per tile of the level, faces stacked: x = tx, y = face * n + ty. rg = the tile's slot
			// column and row, a = 255 once it has landed.
			indirection = new Texture2D(n, 6 * n, TextureFormat.RGBA32, false, true)
			{
				name = "SCANsat Mirage indirection " + body.name + " L" + level,
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

			Log.Message($"[{body.name}] Mirage Visual: level {level}, {jobs.Length} tiles of {slotSize} px, {(planes.Length > 1 ? "colour and normals" : "colour only")} ({atlasSize} px atlases, {bytes / 1048576.0:F1} MB)");

			workerRunning = true;
			ThreadPool.QueueUserWorkItem(_ => readAll());
		}

		// Worker thread: read and decompress every tile, in job order
		void readAll()
		{
			for (int i = 0; i < jobs.Length; i++)
			{
				foreach (Plane p in planes)
				{
					object read = null;
					try
					{
						if (!cancelled)
						{
							read = SCANmirage.ReadTile(p.requests[i]);
						}
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

					lock (gate)
					{
						if (cancelled)
						{
							SCANmirage.ReleaseTile(read);
							read = null;
						}

						p.reads[i] = read;
					}
				}

				lock (gate)
				{
					landedCount = i + 1;
				}
			}

			// Every request is read: the readers (their open files) are done with
			lock (gate)
			{
				workerRunning = false;
				foreach (Plane p in planes)
				{
					disposeSource(p);
				}
			}
		}

		// Main thread: copy landed tiles into the atlases
		void pump(bool wait)
		{
			if (Complete)
			{
				return;
			}

			Stopwatch waited = Stopwatch.StartNew();
			while (true)
			{
				int landed;
				lock (gate)
				{
					landed = landedCount;
				}

				int limit = wait ? landed : Math.Min(landed, uploadedCount + UploadsPerBind);
				while (uploadedCount < limit)
				{
					upload(uploadedCount);
					uploadedCount++;
				}

				if (!wait || Complete || waited.ElapsedMilliseconds > WaitMilliseconds)
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

			if (Complete)
			{
				foreach (Plane p in planes)
				{
					if (p.missing > 0)
					{
						Log.Error($"[{body.name}] Mirage {p.layer.name}: {p.missing} of {jobs.Length} tiles at level {level} could not be read; "
							+ (p == planes[0] ? "level 0 stands in for them" : "they are drawn unshaded"));
					}
				}

				Log.Message($"[{body.name}] Mirage Visual: level {level} on the GPU in {clock.Elapsed.TotalSeconds:F2} s");
			}
		}

		// Job i's tile of every layer into its slot. The colour tile decides whether the slot counts as
		// landed; a normal tile that will not read is left level, so it cannot shade its colour wrongly.
		void upload(int i)
		{
			Job j = jobs[i];
			int sx = j.slot % columns, sy = j.slot / columns;
			bool landed = false;

			foreach (Plane p in planes)
			{
				object read = p.reads[i];
				p.reads[i] = null;

				try
				{
					byte[] data = SCANmirage.TileBytes(read, out int tileFormat);
					NativeArray<byte> raw = p.staging.GetRawTextureData<byte>();
					if (data == null || tileFormat != p.layer.format || data.Length < raw.Length)
					{
						p.missing++;
						if (p == planes[0])
						{
							continue;
						}

						data = flatBC5(raw.Length);
					}

					NativeArray<byte>.Copy(data, raw, raw.Length);
					p.staging.Apply(false);
					Graphics.CopyTexture(p.staging, 0, 0, 0, 0, slotSize, slotSize, p.texture, 0, 0, sx * slotSize, sy * slotSize);
					landed |= p == planes[0];
				}
				finally
				{
					SCANmirage.ReleaseTile(read);
				}
			}

			if (landed && j.level == level)
			{
				int n = 1 << level;
				indirectionPixels[(j.face * n + j.ty) * n + j.tx] = new Color32((byte)sx, (byte)sy, 0, 255);
				indirectionDirty = true;
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

		static void disposePlane(Plane p)
		{
			disposeSource(p);
			if (p.texture != null) { UnityEngine.Object.Destroy(p.texture); p.texture = null; }
			if (p.staging != null) { UnityEngine.Object.Destroy(p.staging); p.staging = null; }
		}

		public void Dispose()
		{
			lock (gate)
			{
				cancelled = true;
				foreach (Plane p in planes)
				{
					// To the end, not landedCount: the job being read can hold one layer's tile already
					for (int i = uploadedCount; i < jobs.Length; i++)
					{
						SCANmirage.ReleaseTile(p.reads[i]);
						p.reads[i] = null;
					}

					// A running worker disposes the readers on its way out
					if (!workerRunning)
					{
						disposeSource(p);
					}

					if (p.texture != null) { UnityEngine.Object.Destroy(p.texture); p.texture = null; }
					if (p.staging != null) { UnityEngine.Object.Destroy(p.staging); p.staging = null; }
				}
			}

			if (indirection != null) { UnityEngine.Object.Destroy(indirection); indirection = null; }
		}
	}
}
