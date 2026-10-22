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
	/// A Mirage body's colour tiles on the GPU, for its Visual map. Mirage bodies ship no equirect colour
	/// map; their surface is a cube of tiles, deeper levels finer, in Mirage's archive. One level of the
	/// whole body is copied into a texture atlas as it is stored - block-compressed (BC7), never decoded on
	/// the CPU - with the six level-0 tiles beside it, which stand in while the level streams in. The
	/// composite shader turns each pixel's lon/lat into a cube face and UV, finds the tile's slot in the
	/// indirection texture (one texel per tile of the level) and samples it (SCANVisualComposite's
	/// mirageColor). Mirage reads and decompresses the tiles on a worker thread; the main thread copies
	/// them in, a batch at a time, whenever a map binds the atlas.
	/// </summary>
	internal sealed class SCANmirageAtlas : IDisposable
	{
		// The equirect sources' budget (SCANtextures.TextureBudget): a whole body to level 3 at 264 px BC7 slots
		const long MaxBytes = 32L * 1048576;
		// Atlases kept, most recently bound last: a big map and a zoom map can want different levels of one body
		const int MaxAtlases = 3;
		// Tiles copied in per bind, 70 KB each
		const int UploadsPerBind = 64;
		// A one-off render (the Visual export) waits at most this long for its tiles
		const int WaitMilliseconds = 10000;

		static readonly List<SCANmirageAtlas> atlases = new List<SCANmirageAtlas>();
		static readonly HashSet<string> failedBodies = new HashSet<string>();
		// What a body's colour reader reported when first made; null for a body Mirage does not texture.
		// Maps only draw in flight and the tracking station, after Mirage has read its configs.
		static readonly Dictionary<CelestialBody, Layer> layers = new Dictionary<CelestialBody, Layer>();
		static Vector4[] basisU, basisV;
		static bool basisMeasured, basisFailed;

		class Layer
		{
			internal int maxLevel, format, tileSize, border;
		}

		struct Job
		{
			internal int face, level, tx, ty, slot;
			internal object request;
		}

		readonly CelestialBody body;
		readonly int level;
		readonly int tileSize, border, slotSize, columns, atlasSize;
		readonly TextureFormat format;
		readonly Job[] jobs;
		readonly Stopwatch clock = Stopwatch.StartNew();

		IDisposable source;
		Texture2D atlas, staging, indirection;
		Color32[] indirectionPixels;
		bool indirectionDirty;

		// Worker -> main thread. reads[i] is jobs[i]'s TileRead once landed; landedCount only grows.
		readonly object gate = new object();
		readonly object[] reads;
		int landedCount, uploadedCount, missing;
		bool workerRunning;
		volatile bool cancelled;
		Exception workerError;

		internal bool Complete { get { return uploadedCount == jobs.Length; } }

		/// <summary>
		/// Point a composite material at the body's colour tiles, for a map needing targetWidth pixels
		/// across 360 degrees, and copy in whatever has landed since the last bind. False when the body
		/// is not a Mirage body or its tiles cannot be read: the caller uses the body's other sources.
		/// loading: the level is still streaming in, so the caller should composite again. wait: block
		/// until it has all landed (a one-off render).
		/// </summary>
		internal static bool Bind(Material m, CelestialBody body, int targetWidth, bool wait, out bool loading)
		{
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

			m.SetTexture("_MirageTiles", a.atlas);
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
			IDisposable source = null;
			try
			{
				// The level is the reader's to say (its depth, tile size and format), so ask it once per body
				if (!layers.TryGetValue(body, out Layer layer))
				{
					source = SCANmirage.CreateTileSource(body, "Color", false, out int maxLevel, out int format, out int tileSize, out int border);
					layer = source == null ? null : new Layer { maxLevel = maxLevel, format = format, tileSize = tileSize, border = border };
					layers[body] = layer;
				}

				if (layer == null)
				{
					return null;
				}

				int level = levelFor(targetWidth, layer.maxLevel, layer.tileSize, layer.border, layer.format);
				for (int i = 0; i < atlases.Count; i++)
				{
					SCANmirageAtlas a = atlases[i];
					if (a.body == body && a.level == level)
					{
						source?.Dispose();
						atlases.RemoveAt(i);
						atlases.Add(a);
						return a;
					}
				}

				TextureFormat textureFormat = (TextureFormat)layer.format;
				if (!SystemInfo.SupportsTextureFormat(textureFormat))
				{
					source?.Dispose();
					failedBodies.Add(body.name);
					Log.Error($"[{body.name}] Mirage colour tiles are {textureFormat}, which this graphics device cannot sample; Visual uses the body's other sources");
					return null;
				}

				if (source == null)
				{
					source = SCANmirage.CreateTileSource(body, "Color", false, out _, out _, out _, out _);
					if (source == null)
					{
						return null;
					}
				}

				SCANmirageAtlas made = new SCANmirageAtlas(body, source, level, textureFormat, layer.tileSize, layer.border);
				source = null;   // the atlas owns it now
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
				source?.Dispose();
				failedBodies.Add(body.name);
				Log.Exception(e);
				Log.Error($"[{body.name}] Mirage colour tiles could not be read; Visual uses the body's other sources");
				return null;
			}
		}

		// The coarsest level whose texels are no larger than a map pixel - a face spans 90 degrees in 2^level
		// tiles - within the archive's depth and the memory budget.
		static int levelFor(int targetWidth, int maxLevel, int tileSize, int border, int format)
		{
			double tiles = targetWidth / 4.0 / tileSize;
			int level = tiles <= 1 ? 0 : (int)Math.Ceiling(Math.Log(tiles, 2));
			level = Math.Min(level, maxLevel);

			long slotBytes = slotBytesOf((TextureFormat)format, tileSize + 2 * border);
			while (level > 0 && slotsFor(level) * slotBytes > MaxBytes)
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

		SCANmirageAtlas(CelestialBody body, IDisposable source, int level, TextureFormat format, int tileSize, int border)
		{
			this.body = body;
			this.source = source;
			this.level = level;
			this.format = format;
			this.tileSize = tileSize;
			this.border = border;
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

			// Mirage resolves tiles on the main thread only
			for (int i = 0; i < list.Count; i++)
			{
				Job j = list[i];
				j.request = SCANmirage.PrepareTile(source, j.face, j.level, j.tx, j.ty);
				list[i] = j;
			}

			jobs = list.ToArray();
			reads = new object[jobs.Length];

			// Created unreadable: the tiles are copied in on the GPU, and a CPU copy would only double the memory
			atlas = new Texture2D(atlasSize, atlasSize, format, false, false)
			{
				name = "SCANsat Mirage colour " + body.name + " L" + level,
				filterMode = FilterMode.Bilinear,
				wrapMode = TextureWrapMode.Clamp,
			};
			atlas.Apply(false, true);

			staging = new Texture2D(slotSize, slotSize, format, false, false);

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

			Log.Message($"[{body.name}] Mirage colour: level {level}, {jobs.Length} tiles of {slotSize} px {format} ({atlasSize} px atlas, {slotsFor(level) * slotBytesOf(format, slotSize) / 1048576.0:F1} MB)");

			workerRunning = true;
			ThreadPool.QueueUserWorkItem(_ => readAll());
		}

		// Worker thread: read and decompress every tile, in job order
		void readAll()
		{
			for (int i = 0; i < jobs.Length; i++)
			{
				object read = null;
				try
				{
					if (!cancelled)
					{
						read = SCANmirage.ReadTile(jobs[i].request);
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

					reads[i] = read;
					landedCount = i + 1;
				}
			}

			// Every request is read: the reader (its open files) is done with
			lock (gate)
			{
				workerRunning = false;
				disposeSource();
			}
		}

		// Main thread: copy landed tiles into the atlas
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
				if (missing > 0)
				{
					Log.Error($"[{body.name}] Mirage colour: {missing} of {jobs.Length} tiles at level {level} could not be read; level 0 stands in for them");
				}

				Log.Message($"[{body.name}] Mirage colour: level {level} on the GPU in {clock.Elapsed.TotalSeconds:F2} s");
			}
		}

		void upload(int i)
		{
			Job j = jobs[i];
			object read = reads[i];
			reads[i] = null;

			try
			{
				byte[] data = SCANmirage.TileBytes(read, out int tileFormat);
				NativeArray<byte> raw = staging.GetRawTextureData<byte>();
				if (data == null || tileFormat != (int)format || data.Length < raw.Length)
				{
					missing++;
					return;
				}

				NativeArray<byte>.Copy(data, raw, raw.Length);
				staging.Apply(false);

				int sx = j.slot % columns, sy = j.slot / columns;
				Graphics.CopyTexture(staging, 0, 0, 0, 0, slotSize, slotSize, atlas, 0, 0, sx * slotSize, sy * slotSize);

				if (j.level == level)
				{
					int n = 1 << level;
					indirectionPixels[(j.face * n + j.ty) * n + j.tx] = new Color32((byte)sx, (byte)sy, 0, 255);
					indirectionDirty = true;
				}
			}
			finally
			{
				SCANmirage.ReleaseTile(read);
			}
		}

		void disposeSource()
		{
			source?.Dispose();
			source = null;
		}

		public void Dispose()
		{
			lock (gate)
			{
				cancelled = true;
				for (int i = uploadedCount; i < landedCount; i++)
				{
					SCANmirage.ReleaseTile(reads[i]);
					reads[i] = null;
				}

				// A running worker disposes the reader on its way out
				if (!workerRunning)
				{
					disposeSource();
				}
			}

			if (atlas != null) { UnityEngine.Object.Destroy(atlas); atlas = null; }
			if (staging != null) { UnityEngine.Object.Destroy(staging); staging = null; }
			if (indirection != null) { UnityEngine.Object.Destroy(indirection); indirection = null; }
		}
	}
}
