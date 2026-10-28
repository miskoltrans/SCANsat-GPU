#region license
/* 
 * [Scientific Committee on Advanced Navigation]
 * 			S.C.A.N. Satellite
 *
 * SCANtextures - support structure for loading and displaying textures.
 * 
 * Copyright (c)2013 damny;
 * Copyright (c)2014 David Grandy <david.grandy@gmail.com>;
 * Copyright (c)2014 technogeeky <technogeeky@gmail.com>;
 * Copyright (c)2026 Azrail09 
 * Copyright (c)2026 Miskoltrans <your email here>; see LICENSE.txt for licensing details.
 */
#endregion

using Contracts;
using FinePrint.Contracts;
using FinePrint.Contracts.Parameters;
using KSPTextureLoader;
using SCANsat.SCAN_Data;
using SCANsat.SCAN_Map;
using SCANsat.SCAN_Platform.Extensions.ConfigNodes;
using SCANsat.SCAN_Reflection;
using SCANsat.SCAN_Toolbar;
using SCANsat.SCAN_UI.UI_Framework;
using SCANsat.SCAN_Unity;
using SCANsat.Unity.Interfaces;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using Log = KSPCommunityLib.Logging.Log;
using palette = SCANsat.SCAN_UI.UI_Framework.SCANcolorUtil;


namespace SCANsat
{
	/// <summary>
	/// Per-body Visual-map texture sources. Reads the body's SCANSAT_BODY_TEXTURES node (if any) into
	/// resolved absolute paths and loads the colour/normal maps to the GPU one mip level at a time -
	/// the smallest mip that still covers the widest map asking for it - through KSPTextureLoader's
	/// owned-texture API. Nothing is decompressed on the CPU and nothing is kept in RAM.
	/// </summary>
	public class SCANtextures
	{
		public CelestialBody body;

		/* Resolved absolute paths from a SCANSAT_BODY_TEXTURES node; null when not declared */
		public string heightMapPath = null;
		public string normalMapPath = null;
		public string colorMapPath = null;

		/* GPU textures loaded from the cfg, one mip each */
		private SCANddsHeader colorHeader;
		private SCANddsHeader normalHeader;
		private Texture colorTex;    // the loader's Texture2D, or a RenderTexture holding a downscale of a mip-less file
		private Texture normalTex;
		private int colorMip = -1;
		private int normalMip = -1;
		private bool colorFailed;    // header or loader rejected the file: logged once, not retried
		private bool normalFailed;

		private static HashSet<string> configuredBodies;
		private static readonly HashSet<string> failedColorBodies = new HashSet<string>();
		// Declared paths found missing: a body's paths are resolved again each time one of its Visual maps opens, so
		// the warning is given once a session (Sol's Mirage build ships no maps for 22 bodies, by design)
		private static readonly HashSet<string> missingPaths = new HashSet<string>();

		public bool HasConfig
		{
			get { return colorMapPath != null || normalMapPath != null || heightMapPath != null; }
		}

		public Texture ColorTexture
		{
			get { return colorTex; }
		}

		public Texture NormalTexture
		{
			get { return normalTex; }
		}

		/// <summary>The declared colorMap will not load: the cfg cannot serve this body's Visual map.</summary>
		public bool ColorFailed
		{
			get { return colorFailed; }
		}

		/// <summary>Which normal-map channel carries Y for the shader: 1 = green (DXT5nm, BC5), 0 = blue.</summary>
		public int NormalYChannel
		{
			get { return normalHeader == null ? 0 : NormalYChannelFor((TextureFormat)(int)normalHeader.Format); }
		}

		public static int NormalYChannelFor(TextureFormat format)
		{
			switch (format)
			{
				case TextureFormat.DXT5:   // DXT5nm: X in alpha, Y in green
				case TextureFormat.BC5:    // X in red, Y in green
					return 1;
				default:
					return 0;              // uncompressed RGB normals: KSP keeps Y in blue
			}
		}

		/// <summary>True when a SCANSAT_BODY_TEXTURES node declares a colorMap for the body and the file is there (cached from GameDatabase).</summary>
		/// Whether the file actually loads is another matter, so the OnDemand paths ask UseConfigFor, not
		/// this. A declared file the install does not have is no source at all: Sol's Mirage build ships
		/// no colour map for the bodies Mirage draws, and those need their ScaledSpace textures paged in
		/// like any other body.
		public static bool HasConfigFor(CelestialBody b)
		{
			if (b == null)
			{
				return false;
			}

			if (configuredBodies == null)
			{
				configuredBodies = new HashSet<string>();

				ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("SCANSAT_BODY_TEXTURES");
				string gameDataFolder = Directory.GetParent(KSPUtil.ApplicationRootPath).FullName + "/GameData/";

				for (int i = 0; i < nodes.Length; i++)
				{
					string name = nodes[i].GetValue("name");
					string colorMap = nodes[i].GetValue("colorMap");

					if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(colorMap) && File.Exists(gameDataFolder + colorMap))
					{
						configuredBodies.Add(name);
					}
				}
			}

			return configuredBodies.Contains(b.name);
		}

		/// <summary>
		/// True while the cfg really is this body's Visual source: a node declares a colorMap and that
		/// file has not failed to load. Only then can Kopernicus OnDemand be left alone - once the
		/// declared colour map is unusable the body needs its own ScaledSpace textures like any other.
		/// Static, so it still answers after the body's SCANtextures handler has been released.
		/// </summary>
		public static bool UseConfigFor(CelestialBody b)
		{
			return HasConfigFor(b) && !failedColorBodies.Contains(b.name);
		}

		/// <summary>Records a failed colorMap; true the first time, so the caller logs once per session.</summary>
		public static bool MarkColorFailed(CelestialBody b)
		{
			return b != null && failedColorBodies.Add(b.name);
		}

		public SCANtextures(CelestialBody b)
		{
			body = b;

			ConfigNode[] visualOverrides = GameDatabase.Instance.GetConfigNodes("SCANSAT_BODY_TEXTURES");

			for (int i = 0; i < visualOverrides.Length; i++)
			{
				ConfigNode node = visualOverrides[i];
				if (node.HasValue("name"))
				{
					string bodyName = node.GetValue("name");
					if (bodyName == b.name)
					{
						GetTexturePathsFromConfig(node);
						break;
					}
				}
			}

			SCANUtil.SCANlog(TextureState());
		}

		public void GetTexturePathsFromConfig(ConfigNode node)
		{
			string bodyName = node.GetValue("name");
			if (bodyName == body.name)
			{
				// Allow cfg definitions for custom planet packs: all paths relative to GameData.
				string gameDataFolder = Directory.GetParent(KSPUtil.ApplicationRootPath).FullName + "/GameData/";

				heightMapPath = resolve(gameDataFolder, node.GetValue("heightMap"));
				normalMapPath = resolve(gameDataFolder, node.GetValue("normalMap"));
				colorMapPath = resolve(gameDataFolder, node.GetValue("colorMap"));
			}
		}

		private static string resolve(string baseFolder, string cfgPath)
		{
			if (string.IsNullOrEmpty(cfgPath))
			{
				return null;
			}

			string path = (baseFolder + cfgPath).Replace("\\", "/");
			if (File.Exists(path))
			{
				return path;
			}
			if (missingPaths.Add(path))
			{
				SCANUtil.SCANlog($"[WARNING] SCANSAT_BODY_TEXTURES path \"GameData/{cfgPath}\" does not exist.");
			}
			return null;
		}

		/// <summary>
		/// Load, or upgrade to, the mip that covers targetWidth pixels across 360 degrees of longitude.
		/// Only ever grows: a zoom map that zoomed in keeps the larger mip until the body is released.
		/// Blocks on the load - mip 3 of a 16K DXT5 map is 2 MiB, the base mip of one is 128 MiB.
		/// </summary>
		public bool EnsureLoaded(int targetWidth)
		{
			// A RenderTexture downscale loses its contents on a graphics device reset; rebuild it then.
			if (colorTex is RenderTexture crt && !crt.IsCreated())
			{
				destroyTexture(colorTex);
				colorTex = null;
				colorMip = -1;
			}

			if (normalTex is RenderTexture nrt && !nrt.IsCreated())
			{
				destroyTexture(normalTex);
				normalTex = null;
				normalMip = -1;
			}

			if (colorMapPath != null && !colorFailed)
			{
				loadMip(colorMapPath, "colorMap", false, targetWidth, ref colorHeader, ref colorTex, ref colorMip, ref colorFailed);
			}

			if (normalMapPath != null && !normalFailed)
			{
				loadMip(normalMapPath, "normalMap", true, targetWidth, ref normalHeader, ref normalTex, ref normalMip, ref normalFailed);
			}

			return colorTex != null;
		}

		/// <summary>Ceiling on a single resident source texture, compressed or not.</summary>
		private const long TextureBudget = 32L * 1048576;

		/// <summary>
		/// Size of the GPU downscale kept in place of the base level of a file with no mip chain: about
		/// two texels per map pixel, as the mip pick, never wider than the source and never over the same
		/// 32 MiB budget. False when the file has a mip chain, or when the downscale would not be clearly
		/// smaller than the base level and so is not worth the blit.
		/// </summary>
		private static bool downscaleSize(SCANddsHeader header, int targetWidth, out int w, out int h)
		{
			w = 0;
			h = 0;

			if (header.MipCount != 1)
			{
				return false;
			}

			w = Mathf.Min(header.Width, Mathf.NextPowerOfTwo(Mathf.Max(1, targetWidth) * 2));
			h = Mathf.Max(1, (int)((long)w * header.Height / header.Width));

			// ARGB32, so the budget bites sooner than on a compressed mip: 4096x2048 is exactly 32 MiB.
			while (w > 1 && (long)w * h * 4 > TextureBudget)
			{
				w >>= 1;
				h = Mathf.Max(1, (int)((long)w * header.Height / header.Width));
			}

			return (long)w * h * 4 * 2 < header.MipBytes(0);
		}

		private void loadMip(string path, string role, bool linear, int targetWidth, ref SCANddsHeader header, ref Texture tex, ref int loadedMip, ref bool failed)
		{
			if (header == null && !SCANddsHeader.TryRead(path, out header, out string error))
			{
				failed = true;
				Log.Error($"[{body.name}] {role} {path}: {error}");
				return;
			}

			// Aim for about two texels per map pixel - bilinear at 1:1 looks soft next to the old full-res
			// CPU sampling - unless that mip costs more than 32 MiB, then settle for 1:1 (the zoom map at
			// high magnification lands here, where the 1:1 mip is already the base level or close to it).
			int mip = header.MipForWidth(targetWidth * 2);

			if (header.MipBytes(mip) > TextureBudget && header.MipForWidth(targetWidth) > mip)
			{
				mip = header.MipForWidth(targetWidth);
			}

			// The width this call would end up holding: the mip's own, or - for a file with no mip
			// chain, where a downscale replaces the base level - the downscale's.
			bool downscale = downscaleSize(header, targetWidth, out int dsWidth, out int dsHeight);
			int wantWidth = downscale ? dsWidth : header.MipWidth(mip);

			// Only ever grow, and never shrink. The mip compare alone is not enough: a file with one
			// level always picks mip 0, so once loaded every later call - however much wider the map
			// asking - matched "mip >= loadedMip" and returned, and the GPU downscale stayed at whatever
			// the first caller wanted (small map at 360 px -> 1024x512, and then the big map at 1440 and
			// a 4096 export both composited from that).
			if (tex != null && mip >= loadedMip && wantWidth <= tex.width)
			{
				return;   // already holding this mip or a larger one, at this width or wider
			}

			// The mip chain is the file's own claim; nothing guarantees the bytes behind it were
			// written. Check the range against the file's length before asking the loader to read it.
			if (!header.TryMipRange(mip, out long mipOffset, out long mipBytes, out string rangeError))
			{
				failed = true;
				Log.Error($"[{body.name}] {role} {path}: {rangeError}");
				return;
			}

			float start = Time.realtimeSinceStartup;

			try
			{
				Texture2DConfig config = header.ConfigForMip(mip, linear);
				TextureLoadTask<Texture2D> task = TextureLoader.LoadOwnedTexture2D(config, path, mipOffset, mipBytes);
				Texture2D loaded = task.GetTexture();

				if (loaded == null)
				{
					throw new Exception("loader returned no texture");
				}

				loaded.name = $"SCANsat {body.name} {role} mip{mip}";
				loaded.wrapModeU = TextureWrapMode.Repeat;   // longitude wraps at the seam
				loaded.wrapModeV = TextureWrapMode.Clamp;
				loaded.filterMode = FilterMode.Bilinear;

				Texture result = loaded;
				string note = "";

				// No mip chain to choose from, so the only level is the full-size one (RSS ships most of
				// its PluginData maps that way: 16K, one level, 128 MiB). When a GPU downscale that covers
				// the request is clearly smaller, keep that instead and release the base level - the
				// transient cost is the same, the resident cost drops from 128 MiB to 2.
				if (downscale)
				{
					long rtBytes = (long)dsWidth * dsHeight * 4;

					RenderTexture rt = new RenderTexture(dsWidth, dsHeight, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
					rt.name = $"SCANsat {body.name} {role} downscale";
					rt.wrapModeU = TextureWrapMode.Repeat;
					rt.wrapModeV = TextureWrapMode.Clamp;
					rt.filterMode = FilterMode.Bilinear;
					rt.useMipMap = false;
					rt.Create();
					Graphics.Blit(loaded, rt);
					UnityEngine.Object.Destroy(loaded);
					result = rt;
					note = $"; no mip chain, kept a {dsWidth}x{dsHeight} GPU downscale ({rtBytes / 1048576f:F1} MiB) and released the base level";
				}

				if (tex != null)
				{
					destroyTexture(tex);
				}

				tex = result;
				loadedMip = mip;

				SCANUtil.SCANlog("[{0}] {1}: mip {2}/{3} ({4}x{5} {6}, {7:F1} MiB at offset {8}) for target width {9}px in {10:F0} ms{11}",
					body.name, role, mip, header.MipCount, config.Width, config.Height, header.FormatName,
					mipBytes / 1048576f, mipOffset, targetWidth, (Time.realtimeSinceStartup - start) * 1000f, note);
			}
			catch (Exception e)
			{
				failed = true;
				Log.Error($"[{body.name}] {role} {path}: mip {mip} failed to load");
				Log.Exception(e);
			}
		}

		private static void destroyTexture(Texture t)
		{
			if (t is RenderTexture rt)
			{
				rt.Release();
			}

			UnityEngine.Object.Destroy(t);
		}

		public void Release()
		{
			if (colorTex != null)
			{
				destroyTexture(colorTex);
				colorTex = null;
			}

			if (normalTex != null)
			{
				destroyTexture(normalTex);
				normalTex = null;
			}

			colorMip = -1;
			normalMip = -1;
		}

		public string TextureState()
		{
			return $"{body.name} Visual textures: colorMap={(colorMapPath ?? "none")}, normalMap={(normalMapPath ?? "none")}, heightMap={(heightMapPath ?? "none")}";
		}
	}

	public class SCANMirageTextures
	{
		public CelestialBody body;

		public string baseMirageFolder = null;
		public uint maxLOD = 0;
		public SCANMirageTextures(CelestialBody b)
		{
			body = b;
			
			ConfigNode[] mirageConfigurations = GameDatabase.Instance.GetConfigNodes("SCANSAT_BODY_MIRAGE_CONFIG");

			for (int i = 0; i < mirageConfigurations.Length; i++)
			{
				ConfigNode node = mirageConfigurations[i];
				if (node.HasValue("name"))
				{
					string bodyName = node.GetValue("name");
					if (bodyName == b.name)
					{
						GetMirageFromConfig(node);
						break;
					}
				}
			}
		}

		public void GetMirageFromConfig(ConfigNode node)
		{
			string bodyName = node.GetValue("name");
			if (bodyName == body.name)
			{
				// Allow cfg definitions for custom planet packs: all paths relative to GameData.
				string gameDataFolder = Directory.GetParent(KSPUtil.ApplicationRootPath).FullName + "/GameData/";

				string mirageFolder = node.GetValue("mirageTextures");
				if (mirageFolder == null)
				{
					return;
				}

				baseMirageFolder = (gameDataFolder + mirageFolder).Replace("\\", "/");
				string[] lod_folders = Directory.GetDirectories(baseMirageFolder);
				bool lodsValid = verifyLOD(lod_folders.Length);
				if (!lodsValid)
				{
					SCANUtil.SCANlog($"[WARNING] SCANSAT_BODY_MIRAGE_CONFIG of {body.name} does not contain valid LOD folders.");
					baseMirageFolder = null;
					return;
				}
			}
		}

		private static string resolve(string baseFolder, string cfgPath)
		{
			if (string.IsNullOrEmpty(cfgPath))
			{
				return null;
			}

			string path = (baseFolder + cfgPath).Replace("\\", "/");
			if (File.Exists(path))
			{
				return path;
			}
			SCANUtil.SCANlog($"[WARNING] SCANSAT_BODY_TEXTURES path \"GameData/{cfgPath}\" does not exist.");
			return null;
		}

		private bool verifyLOD(int lod)
		{
			bool colorMaps = File.Exists(getColorMapPath(lod)) && File.Exists(getColorIndexPath(lod));
			if (!colorMaps)
			{
				SCANUtil.SCANlog($"[WARNING] SCANSAT_BODY_MIRAGE_CONFIG of {body.name} with LOD {lod} contains invalid color textures.");	
			}
			bool normalMaps = File.Exists(getNormalMapPath(lod)) && File.Exists(getNormalIndexPath(lod));
			if (!normalMaps)
			{
				SCANUtil.SCANlog($"[WARNING] SCANSAT_BODY_MIRAGE_CONFIG of {body.name} with LOD {lod} contains invalid normal textures.");
			}
			bool heightMaps = File.Exists(getHeightMapPath(lod)) && File.Exists(getHeightIndexPath(lod));
			if (!heightMaps)
			{
				SCANUtil.SCANlog($"[WARNING] SCANSAT_BODY_MIRAGE_CONFIG of {body.name} with LOD {lod} contains invalid height textures.");	
			}
			return colorMaps && normalMaps && heightMaps;
		}

		private string getColorMapPath(int lod)
		{
			return $"{baseMirageFolder}/Level_{lod}/canonical.color.L{lod}.bin";
		}

		private string getColorIndexPath(int lod)
		{
			return $"{baseMirageFolder}/Level_{lod}/canonical.color.L{lod}.idx";
		}

		private string getNormalMapPath(int lod)
		{
			return $"{baseMirageFolder}/Level_{lod}/canonical.normal.L{lod}.bin";
		}

		private string getNormalIndexPath(int lod)
		{
			return $"{baseMirageFolder}/Level_{lod}/canonical.normal.L{lod}.idx";
		}

		private string getHeightMapPath(int lod)
		{
			return $"{baseMirageFolder}/Level_{lod}/canonical.height.L{lod}.bin";
		}

		private string getHeightIndexPath(int lod)
		{
			return $"{baseMirageFolder}/Level_{lod}/canonical.height.L{lod}.idx";
		}
	}	
}
