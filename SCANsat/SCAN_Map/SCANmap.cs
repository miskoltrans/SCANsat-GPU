#region license
/*
 * [Scientific Committee on Advanced Navigation]
 * 			S.C.A.N. Satellite
 * 
 * SCANmap - makes maps from data
 *
 * Copyright (c)2013 damny;
 * Copyright (c)2014 technogeeky <technogeeky@gmail.com>;
 * Copyright (c)2014 (Your Name Here) <your email here>; see LICENSE.txt for licensing details.
*/
#endregion

using System;
using System.Linq;
using System.IO;
using UnityEngine;
using SCANsat.SCAN_Platform.Palettes;
using SCANsat.SCAN_Data;
using SCANsat.SCAN_UI.UI_Framework;
using SCANsat.SCAN_Unity;
using palette = SCANsat.SCAN_UI.UI_Framework.SCANcolorUtil;

namespace SCANsat.SCAN_Map
{
	public class SCANmap
	{
		internal SCANmap(CelestialBody Body, bool Cache, mapSource s)
		{
			body = Body;
			mSource = s;
			pqs = body.pqsController != null;
			biomeMap = body.BiomeMap != null;
			data = SCANUtil.getData(body);
			if (data == null)
			{
				data = new SCANdata(body);
				SCANcontroller.controller.addToBodyData(body, data);
			}
			cache = Cache;
		}

		internal SCANmap()
		{
		}

		#region Public Accessors

		public double MapScale
		{
			get { return mapscale; }
			internal set
			{
				if (!cache && mapscale != value)
					clearWindowCaches();   // pixel-space caches: a new zoom level is a new window
				mapscale = value;
				resourceMapScale = (mapwidth / resourceMapWidth) * mapscale;
			}
		}

		public double Lon_Offset
		{
			get { return lon_offset; }
		}

		public double Lat_Offset
		{
			get { return lat_offset; }
		}

		public double CenteredLong
		{
			get { return centeredLong; }
		}

		public double CenteredLat
		{
			get { return centeredLat; }
		}

		public int MapWidth
		{
			get { return mapwidth; }
		}

		public int MapHeight
		{
			get { return mapheight; }
		}

		public mapType MType
		{
			get { return mType; }
			set { mType = value; }
		}

		public bool ColorMap
		{
			get { return colorMap; }
			set { colorMap = value; }
		}

		public bool Terminator
		{
			get { return terminator; }
			set { terminator = value; }
		}

		public mapSource MSource
		{
			get { return mSource; }
		}

		// The texture to display. A GPU-rendered Visual map is a RenderTexture (accepted by
		// RawImage.texture / Graphics.Blit); otherwise the CPU-built Texture2D. Use this for
		// on-screen display; use Map (Texture2D) for CPU readback such as PNG export.
		public Texture DisplayTexture
		{
			get { return gpuRendered && visualRenderTex != null ? visualRenderTex : null; }
		}

		public bool GpuRendered
		{
			get { return gpuRendered; }
		}

		public RenderTexture VisualRenderTexture
		{
			get { return visualRenderTex; }
		}

		public CelestialBody Body
		{
			get { return body; }
		}

		public SCANresourceGlobal Resource
		{
			get { return resource; }
			set { resource = value; }
		}

		public bool ResourceActive
		{
			get { return resourceActive; }
			set { resourceActive = value; }
		}

		public SCANmapLegend MapLegend
		{
			get { return mapLegend; }
			internal set { mapLegend = value; }
		}

		public MapProjection Projection
		{
			get { return projection; }
			set { projection = value; }
		}

		internal float[,] Big_HeightMap
		{
			get { return big_heightmap; }
		}

		public bool UseCustomRange
		{
			get { return useCustomRange; }
		}

		public float CustomMin
		{
			get { return customMin; }
		}

		public float CustomMax
		{
			get { return customMax; }
		}

		public float CustomRange
		{
			get { return customRange; }
		}

		#endregion

		#region Big Map methods and fields

		/* MAP: Big Map height map caching */
		private float[,] big_heightmap;
		private bool cache;
		private double centeredLong, centeredLat;

		private void terrainHeightToArray(double lon, double lat, int ilon, int ilat)
		{
			passHeightSamples++;
			float alt = 0f;
			alt = (float)SCANUtil.getElevation(body, lon, lat);
			if (alt == 0f)
			{
				alt = -0.001f;
			}

			big_heightmap[ilon, ilat] = alt;
		}

		/* MAP: Projection methods for converting planet coordinates to the rectangular texture */
		private MapProjection projection = MapProjection.Rectangular;

		internal void setProjection(MapProjection p)
		{
			if (projection == p)
			{
				return;
			}

			projection = p;
			clearBiomeRowCache();   // the biome index cache is sampled in projected pixel space
			if (!cache)
				clearWindowCaches();   // and so is a window map's elevation cache
		}

		internal double projectLongitude(double lon, double lat)
		{
			lon = (lon + 3600 + 180) % 360 - 180;
			lat = (lat + 1800 + 90) % 180 - 90;
			switch (projection)
			{
				case MapProjection.KavrayskiyVII:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					lon = (3.0f * lon / 2.0f / Math.PI) * Math.Sqrt(Math.PI * Math.PI / 3.0f - lat * lat);
					return Mathf.Rad2Deg * lon;
				case MapProjection.Polar:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					if (lat < 0)
					{
						lon = 1.3 * Math.Cos(lat) * Math.Sin(lon) - Math.PI / 2;
					}
					else
					{
						lon = 1.3 * Math.Cos(lat) * Math.Sin(lon) + Math.PI / 2;
					}
					return Mathf.Rad2Deg * lon;
				case MapProjection.Orthographic:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double centerLon = Mathf.Deg2Rad * centeredLong;
					double centerLat = Mathf.Deg2Rad * centeredLat;

					if (Math.Sin(centerLat) * Math.Sin(lat) + Math.Cos(centerLat) * Math.Cos(lat) * Math.Cos(lon - centerLon) < 0)
					{
						return -200;
					}

					lon = 1.5 * Math.Cos(lat) * Math.Sin(lon - centerLon);

					return Mathf.Rad2Deg * lon;
				default:
					return lon;
			}
		}

		internal double projectLatitude(double lon, double lat)
		{
			lon = (lon + 3600 + 180) % 360 - 180;
			lat = (lat + 1800 + 90) % 180 - 90;
			switch (projection)
			{
				case MapProjection.Polar:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					if (lat < 0)
					{
						lat = 1.3 * Math.Cos(lat) * Math.Cos(lon);
					}
					else
					{
						lat = -1.3 * Math.Cos(lat) * Math.Cos(lon);
					}
					return Mathf.Rad2Deg * lat;
				case MapProjection.Orthographic:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double centerLon = Mathf.Deg2Rad * centeredLong;
					double centerLat = Mathf.Deg2Rad * centeredLat;

					if (Math.Sin(centerLat) * Math.Sin(lat) + Math.Cos(centerLat) * Math.Cos(lat) * Math.Cos(lon - centerLon) < 0)
					{
						return -200;
					}

					lat = 1.5 * (Math.Cos(centerLat) * Math.Sin(lat) - Math.Sin(centerLat) * Math.Cos(lat) * Math.Cos(lon - centerLon));

					return Mathf.Rad2Deg * lat;
				default:
					return lat;
			}
		}

		internal double unprojectLongitude(double lon, double lat)
		{
			if (lat > 90)
			{
				lat = 180 - lat;
				lon += 180;
			}
			else if (lat < -90)
			{
				lat = -180 - lat;
				lon += 180;
			}
			lon = (lon + 3600 + 180) % 360 - 180;
			lat = (lat + 1800 + 90) % 180 - 90;
			switch (projection)
			{
				case MapProjection.KavrayskiyVII:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					lon = lon / Math.Sqrt(Mathf.PI * Math.PI / 3.0f - lat * lat) * 2.0f * Math.PI / 3.0f;
					return Mathf.Rad2Deg * lon;
				case MapProjection.Polar:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double lat0 = Math.PI / 2;
					if (lon < 0)
					{
						lon += Math.PI / 2;
						lat0 = -Math.PI / 2;
					}
					else
					{
						lon -= Math.PI / 2;
					}
					lon /= 1.3;
					lat /= 1.3;
					double p = Math.Sqrt(lon * lon + lat * lat);
					double c = Math.Asin(p);
					lon = Math.Atan2((lon * Math.Sin(c)), (p * Math.Cos(lat0) * Math.Cos(c) - lat * Math.Sin(lat0) * Math.Sin(c)));
					lon = (Mathf.Rad2Deg * lon + 180) % 360 - 180;
					if (lon <= -180)
					{
						lon = -180;
					}

					return lon;
				case MapProjection.Orthographic:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double centerLon = Mathf.Deg2Rad * centeredLong;
					double centerLat = Mathf.Deg2Rad * centeredLat;

					double p2 = Math.Sqrt(lon * lon + lat * lat);
					double c2 = Math.Asin(p2 / 1.5);

					if (Math.Cos(c2) < 0)
					{
						return 300;
					}

					lon = centerLon + Math.Atan2(lon * Math.Sin(c2), p2 * Math.Cos(c2) * Math.Cos(centerLat) - lat * Math.Sin(c2) * Math.Sin(centerLat));

					lon = (Mathf.Rad2Deg * lon + 180) % 360 - 180;

					if (lon <= -180)
					{
						lon += 360;
					}

					return lon;
				default:
					return lon;
			}
		}

		internal double unprojectLatitude(double lon, double lat)
		{
			if (lat > 90)
			{
				lat = 180 - lat;
				lon += 180;
			}
			else if (lat < -90)
			{
				lat = -180 - lat;
				lon += 180;
			}
			lon = (lon + 3600 + 180) % 360 - 180;
			lat = (lat + 1800 + 90) % 180 - 90;
			switch (projection)
			{
				case MapProjection.Polar:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double lat0 = Math.PI / 2;
					if (lon < 0)
					{
						lon += Math.PI / 2;
						lat0 = -Math.PI / 2;
					}
					else
					{
						lon -= Math.PI / 2;
					}
					lon /= 1.3;
					lat /= 1.3;
					double p = Math.Sqrt(lon * lon + lat * lat);
					double c = Math.Asin(p);
					lat = Math.Asin(Math.Cos(c) * Math.Sin(lat0) + (lat * Math.Sin(c) * Math.Cos(lat0)) / (p));
					return Mathf.Rad2Deg * lat;
				case MapProjection.Orthographic:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double centerLat = Mathf.Deg2Rad * centeredLat;

					double p2 = Math.Sqrt(lon * lon + lat * lat);
					double c2 = Math.Asin(p2 / 1.5);

					if (Math.Cos(c2) < 0)
					{
						return 300;
					}

					lat = Math.Asin(Math.Cos(c2) * Math.Sin(centerLat) + (lat * Math.Sin(c2) * Math.Cos(centerLat)) / p2);

					return Mathf.Rad2Deg * lat;
				default:
					return lat;
			}
		}

		/* MAP: scaling, centering (setting origin), translating, etc */
		private double mapscale, lon_offset, lat_offset;
		private int mapwidth, mapheight;
		private bool resourceActive;
		private float[,] resourceCache;
		private int resourceInterpolation = 4;
		private int resourceMapWidth = 4;
		private int resourceMapHeight = 2;
		private double resourceMapScale = 1;
		private bool randomEdges = true;
		private double[] biomeIndex;
		private int startLine;
		private int stopLine;
		double sunLonCenter;
		double sunLatCenter;
		double gamma;

		internal void setSize(Vector2 size)
		{
			setSize((int)size.x, (int)size.y);
		}

		internal void setSize(int w, int h, int interpolation = 2, int start = 0, int stop = 0)
		{
			if (w == 0)
			{
				w = 360 * (Screen.width / 360);
			}

			if (w > 360 * 4)
			{
				w = 360 * 4;
			}

			mapwidth = w;
			biomeIndex = new double[mapwidth];
			mapscale = mapwidth / 360f;
			if (h <= 0)
			{
				h = (int)(180 * mapscale);
			}

			mapheight = h;
			startLine = start;
			stopLine = stop == 0 ? mapheight - 1 : stop;
			// Window-map caches, pixel space (see prepRow): the GPU data path fills them per rendered
			// pixel, so the zoom map and RPM render Altimetry/Slope/Biome the way the big map does.
			big_heightmap = new float[mapwidth, mapheight];
			biome_indexmap = new float[mapwidth, mapheight];
			biomeRowCached = new bool[mapheight];
			gpuDataBuf = null;   // ensureDataTex sizes it
			invalidateGpuDataCache();
			resourceMapWidth = mapwidth;
			resourceMapHeight = mapheight;
			resourceCache = new float[resourceMapWidth, resourceMapHeight];
			resourceInterpolation = interpolation;
			resourceMapScale = resourceMapWidth / 360;
			randomEdges = false;
		}

		internal void setWidth(int w)
		{
			if (w == 0)
			{
				w = 360 * (int)(Screen.width / 360);
				if (w > 360 * 4)
				{
					w = 360 * 4;
				}
			}
			if (w < 360)
			{
				w = 360;
			}

			if (mapwidth == w)
			{
				return;
			}

			mapwidth = w;
			biomeIndex = new double[w];
			resourceMapHeight = SCAN_Settings_Config.Instance.ResourceMapHeight;
			resourceMapWidth = resourceMapHeight * 2;
			resourceInterpolation = SCAN_Settings_Config.Instance.Interpolation;
			resourceMapScale = resourceMapWidth / 360f;
			resourceCache = new float[resourceMapWidth, resourceMapHeight];
			randomEdges = true;
			mapscale = mapwidth / 360f;
			mapheight = (int)(w / 2);
			startLine = 0;
			stopLine = mapheight - 1;
			/* big map caching */
			big_heightmap = new float[mapwidth, mapheight];
			biome_indexmap = new float[mapwidth, mapheight];
			biomeRowCached = new bool[mapheight];
			gpuDataBuf = new Color[mapwidth * mapheight];
			// Just wiped big_heightmap/biome_indexmap/gpuDataBuf. mapwidth is part of gpuConfigHash so
			// the stale claim can't match today, but the caches are empty either way - don't leave a
			// "data is complete" flag standing behind them.
			invalidateGpuDataCache();
			resetMap(resourceActive);
		}

		// Free the Unity objects this map owns. Texture2D/RenderTexture/Material are NOT GC-managed
		// and are NOT auto-destroyed on scene load, so an orphaned SCANmap (the bigmap/spotmap
		// wrappers are re-created per scene) leaks them - SCANmap is a plain class with no finalizer.
		// The owning window's OnDestroy calls this. Safe to call more than once.
		internal void Destroy()
		{
			if (coverageFlags != null) { UnityEngine.Object.Destroy(coverageFlags); coverageFlags = null; }
			if (compositeMaterial != null) { UnityEngine.Object.Destroy(compositeMaterial); compositeMaterial = null; }
			if (visualRenderTex != null) { visualRenderTex.Release(); UnityEngine.Object.Destroy(visualRenderTex); visualRenderTex = null; }
			if (exporter != null) { UnityEngine.Object.Destroy(exporter.gameObject); exporter = null; }
			if (elevationTex != null) { UnityEngine.Object.Destroy(elevationTex); elevationTex = null; }
			if (biomeIndexTex != null) { UnityEngine.Object.Destroy(biomeIndexTex); biomeIndexTex = null; }
			if (resourceTex != null) { UnityEngine.Object.Destroy(resourceTex); resourceTex = null; }
			if (paletteLUT != null) { UnityEngine.Object.Destroy(paletteLUT); paletteLUT = null; }
			if (paletteGreyLUT != null) { UnityEngine.Object.Destroy(paletteGreyLUT); paletteGreyLUT = null; }
			if (biomeLUT != null) { UnityEngine.Object.Destroy(biomeLUT); biomeLUT = null; }

			// The GPU data cache just went away with those textures, so its bookkeeping must go too.
			// bigmap/spotmap are STATIC: the SCANmap outlives this Destroy and gets reused next scene.
			// Leaving gpuDataComplete/gpuDataHash set means resetMap's instant-recolour path matches on
			// the next open with the same body+mode+projection+zoom, skips the whole sample sweep, and
			// re-Blits from biomeIndexTex/elevationTex that are now null - a blank map until you switch
			// body (which changes the hash) and back.
			invalidateGpuDataCache();
		}

		/// <summary>
		/// Drop every "the GPU already has valid data" claim. Anything that destroys or reallocates the
		/// data textures / CPU caches must call this, or resetMap will trust a cache that isn't there.
		/// </summary>
		private void invalidateGpuDataCache()
		{
			gpuDataComplete = false;
			gpuDataHash = 0;
			gpuRendered = false;
			gpuSweepDone = false;
			gpuRecolorSweep = false;
			resourceTexReady = false;
			resourceCacheReady = false;
			biomeLUTCount = 0;
			biomeLUTBody = null;
			paletteLUTHash = 0;
		}

		internal void centerAround(double lon, double lat)
		{
			double oldLonOffset = lon_offset, oldLatOffset = lat_offset;
			double oldCenteredLong = centeredLong, oldCenteredLat = centeredLat;

			centeredLong = lon;
			centeredLat = lat;

			if (projection == MapProjection.Orthographic)
			{
				double lo = projectLongitude(lon, lat);
				double la = projectLatitude(lon, lat);
				lon_offset = 180 + lo - (mapwidth / mapscale) / 2;
				lat_offset = 90 + la - (mapheight / mapscale) / 2;
			}
			else
			{
				lon_offset = 180 + lon - (mapwidth / mapscale) / 2;
				lat_offset = 90 + lat - (mapheight / mapscale) / 2;
			}

			// A window map's caches are pixel space, so a moved window invalidates them. Same window
			// (the zoom map re-centres on every reset) keeps them, which is what makes a refresh warm.
			if (!cache && (lon_offset != oldLonOffset || lat_offset != oldLatOffset || centeredLong != oldCenteredLong || centeredLat != oldCenteredLat))
				clearWindowCaches();
		}

		internal double scaleLatitude(double lat)
		{
			lat -= lat_offset;
			lat *= 180f / (mapheight / mapscale);
			return lat;
		}

		internal double scaleLongitude(double lon)
		{
			if (lon_offset < 0 && Math.Abs(lon_offset) < lon)
			{
				lon -= 360;
			}
			else if (lon_offset > 0 && Math.Abs(lon_offset) > lon)
			{
				lon += 360;
			}

			lon -= lon_offset;
			lon *= 360f / (mapwidth / mapscale);
			return lon;
		}

		private double unScaleLatitude(double lat)
		{
			lat -= lat_offset;
			lat += 90;
			lat *= mapscale;
			return lat;
		}

		private double unScaleLatitude(double lat, double scale)
		{
			lat -= lat_offset;
			lat += 90;
			lat *= scale;
			return lat;
		}

		private double unScaleLongitude(double lon)
		{
			lon -= lon_offset;
			lon += 180;
			lon *= mapscale;
			return lon;
		}

		private double unScaleLongitude(double lon, double scale)
		{
			lon -= lon_offset;
			lon = SCANUtil.fixLonShift(lon);
			lon += 180;
			lon *= scale;
			return lon;
		}

		private double fixUnscale(double value, int size)
		{
			if (value < 0)
			{
				value = 0;
			}
			else if (value >= (size - 0.5f))
			{
				value = size - 1;
			}

			return value;
		}

		/* MAP: internal state */
		private mapType mType;
		private mapSource mSource;
		private CelestialBody body = null; // all refs are below
		private SCANresourceGlobal resource;
		private SCANdata data;
		private SCANmapLegend mapLegend;
		private int mapstep; // all refs are below
		private bool pqs;
		private bool biomeMap;
		private float customMin;
		private float customMax;
		private float customRange;
		private float customResourceMin;
		private float customResourceMax;
		private bool useCustomRange;
		private bool colorMap;
		private bool terminator;

		/* GPU Visual-mode compositing: renders the Visual map on the GPU sampling the body's
		   original ScaledSpace textures, so no readable CPU copy is needed. Dormant until the
		   composite shader is present in the bundle; otherwise the CPU renderer below runs. */
		private RenderTexture visualRenderTex;
		private Material compositeMaterial;
		private Texture2D coverageFlags;
		private Color32[] coverageFlagsBuf;   // reused CPU buffer for coverageFlags (no per-frame alloc)
		private bool coverageFlagsDirty = true;   // set each resetMap; rebuild the coverage texture once per pass, not per sweep row
		private bool gpuRendered;
		private bool noRenderLogged;   // one "not rendered" log per pass when no renderer applies (shader unavailable)
			// GPU data textures for the non-Visual modes (all-modes port). Elevation/biome/resource
			// upload from the CPU caches (big_heightmap / biome_indexmap / resourceCache); a 1-D palette
			// LUT baked from heightToColor colourizes altimetry (legend parity). Shader branches _MapMode.
			private Texture2D elevationTex;
			private Texture2D biomeIndexTex;
			private Texture2D resourceTex;
			private Texture2D paletteLUT;
			private Texture2D paletteGreyLUT;   // LoRes-only altimetry grey ramp
			private Texture2D biomeLUT;         // stock biome mapColors
			private int biomeLUTCount;
			private CelestialBody biomeLUTBody;
			private float[,] biome_indexmap;
			private bool[] biomeRowCached;   // biome_indexmap row y is fully sampled for the current body / size / projection
			// Per-pass diagnostics, logged once when a GPU data pass completes.
			private int passHeightSamples;
			private int passBiomeSamples;
			private int passBuildFrames;
			private float passBuildStart = -1f;
			private float passBuildMs;
			private Color[] gpuDataBuf;
			private Color[] gpuRowBuf;
			private bool resourceTexReady;
			private bool resourceCacheReady;   // resourceCache is built this reset (by the prep loop or the lazy GPU build)
			private int paletteLUTHash;
			private bool gpuDataComplete;   // the non-Visual data cache is fully sampled for gpuDataHash's config
			private int gpuDataHash;        // config (body/mode/projection/size/offsets) the cached data is valid for
			private bool gpuRecolorSweep;   // cosmetic re-sweep in progress: re-Blit cached data with a new LUT (no re-sample)
		// The GPU compositor draws the whole Visual map in one Blit; the scanline is a purely cosmetic
		// reveal (in the CPU path's row order) so it matches the CPU modes' look. Visual and the
		// recolour re-sweep have their end state on the first Blit, so their line is paced by wall-clock
		// time (SweepDuration) rather than by pump calls: a pass takes the same second on any map size,
		// frame rate or MapGenerationSpeed. The data modes build row by row, so their line tracks
		// mapstep instead. gpuSweepDone gates isMapComplete so the pump keeps re-compositing until the
		// reveal finishes.
		private bool gpuSweepDone;
		private float sweepStart = -1f;   // realtimeSinceStartup at this pass's first composite; -1 until then
		private const float SweepDuration = 1f;
		private float noiseSeed;          // per-pass seed for the shader's no-data static (re-rolled in resetMap like the CPU's Random.value per pass)

		/* MAP: nearly trivial functions */
		public void setBody(CelestialBody b)
		{
			SCANcontroller.controller.unloadPQS(body, mSource);
			SCANcontroller.controller.unloadOnDemandScaledSpace(body, mSource);

			bool bodyChanged = body != b;

			if (bodyChanged)
			{
				SCANcontroller.controller.UnloadVisualMapTexture(body, mSource);
				body = b;
				SCANcontroller.controller.loadOnDemandScaledSpace(body, mSource);
			}
			else
			{
				SCANcontroller.controller.loadOnDemandScaledSpace(body, mSource);
			}

			// Visual textures are registered per body and loaded lazily at the needed mip; only for that mode.
			refreshVisualMapTexture();

			data = SCANUtil.getData(body);

			SCANcontroller.controller.loadPQS(body, mSource);
			pqs = body.pqsController != null;
			biomeMap = body.BiomeMap != null;

			// The height and biome-index caches are per body: terrain and biomes do not change at
			// runtime, and new coverage is picked up per pixel by the "unsampled" checks. So they
			// survive same-body calls (the big map calls setBody on every open) and clear only when the
			// body actually changes.
			if (cache && bodyChanged)
			{
				if (big_heightmap != null)
					System.Array.Clear(big_heightmap, 0, big_heightmap.Length);
				clearBiomeRowCache();
			}

			if (SCANconfigLoader.GlobalResource)
			{
				if (resource != null)
				{
					resource.CurrentBodyConfig(body.bodyName);
				}
			}
		}

		/// <summary>
		/// Visual mode's texture sources are per-body (SCANcontroller.mapTextureHandler): the
		/// SCANSAT_BODY_TEXTURES paths and the GPU textures loaded from them. Register the body while
		/// this map is in Visual mode and release it otherwise, so nothing is held for maps that never
		/// show Visual.
		/// </summary>
		private void refreshVisualMapTexture()
		{
			if (SCANcontroller.controller == null || body == null)
			{
				return;
			}

			if (mType == mapType.Visual)
			{
				SCANcontroller.controller.LoadVisualMapTexture_Renamed(body, mSource);
			}
			else
			{
				SCANcontroller.controller.UnloadVisualMapTexture(body, mSource);
			}
		}

		public void setCustomRange(float min, float max, float rMin, float rMax)
		{
			useCustomRange = true;
			customMin = min;
			customMax = max;
			customRange = max - min;
			customResourceMin = rMin;
			customResourceMax = rMax;
		}

		internal bool isMapComplete()
		{
			// Composited maps hold "incomplete" until the cosmetic sweep finishes so the pump keeps
			// re-compositing the advancing scanline. Before the first composite of a pass (or when
			// nothing renders this map) mapstep says whether the pass is over.
			return gpuRendered ? gpuSweepDone : mapstep >= mapheight;
		}

		public void resetMap(bool resourceOn, bool setRes = true)
		{
			mapstep = -2;
			gpuRendered = false;
			gpuSweepDone = false;
			sweepStart = -1f;
			noiseSeed = UnityEngine.Random.value;
			passHeightSamples = 0;
			passBiomeSamples = 0;
			passBuildFrames = 0;
			passBuildStart = -1f;
			passBuildMs = 0f;
			resourceTexReady = false;
			resourceCacheReady = false;
			coverageFlagsDirty = true;   // new pass: refresh the GPU coverage stencil once from live coverage
			noRenderLogged = false;
			resourceActive = resourceOn;
			if (SCANconfigLoader.GlobalResource && setRes)
			{ //Make sure that a resource is initialized if necessary
				if (resource != null && body != null)
				{
					resource.CurrentBodyConfig(body.bodyName);
				}

				resetResourceMap();
			}

			if (terminator)
			{
				double sunLon = body.GetLongitude(Planetarium.fetch.Sun.position, false);
				double sunLat = body.GetLatitude(Planetarium.fetch.Sun.position, false);

				sunLatCenter = SCANUtil.fixLatShift(sunLat);

				if (sunLatCenter >= 0)
				{
					sunLonCenter = SCANUtil.fixLonShift(sunLon + 90);
				}
				else
				{
					sunLonCenter = SCANUtil.fixLonShift(sunLon - 90);
				}

				gamma = Math.Abs(sunLatCenter) < 0.55 ? 100 : Math.Tan(Mathf.Deg2Rad * (90 - Math.Abs(sunLatCenter)));
			}

			// mType is set (via MType or the resetMap(mode,...) overload) before every call
			// here, so this covers map-type switches that don't go through setBody.
			refreshVisualMapTexture();

			// Instant re-colour: if the GPU data cache is already fully sampled for this exact config
			// (a colourisation-only change - palette/clamp/terminator - leaves the config hash the same),
			// skip the re-sweep. Jump to the last row so the next getPartialMap re-Blits once with the
			// rebuilt LUT over the cached data textures instead of re-sampling PQS across the whole map.
			if (gpuDataComplete && gpuDataHash == gpuConfigHash() && willRenderGPU(mType)
				&& !(resourceActive && SCANconfigLoader.GlobalResource && resource != null)   // resource maps need the prep to rebuild resourceCache (resetResourceMap clears it)
				&& (mType == mapType.Altimetry || mType == mapType.Slope || mType == mapType.Biome))
			{
				mapstep = 0;               // replay the sweep from the top...
				gpuRendered = true;
				gpuSweepDone = false;
				gpuRecolorSweep = true;    // ...re-Blitting the cached data with the rebuilt LUT (charm, no re-sample)
				resourceTexReady = false;  // resource colours may have changed too
			}
			else if (willRenderGPU(mType) && (mType == mapType.Altimetry || mType == mapType.Slope || mType == mapType.Biome))
			{
				// A full data build starts now and overwrites the data textures row by row. Until it
				// completes (buildGpuDataFrame sets the flag again) they hold a mix of passes, so a reset
				// in the meantime must not take the shortcut above.
				gpuDataComplete = false;
			}
		}

		public void resetMap(mapType mode, bool Cache, bool resourceOn, bool setRes = true)
		{
			mType = mode;
			cache = Cache;
			resetMap(resourceOn, setRes);
		}

		public void resetResourceMap()
		{
			if (mSource != mapSource.ZoomMap)
			{
				if (SCAN_Settings_Config.Instance.ResourceMapHeight != resourceMapHeight)
				{
					resourceMapHeight = SCAN_Settings_Config.Instance.ResourceMapHeight;
					resourceMapWidth = resourceMapHeight * 2;
					resourceMapScale = resourceMapWidth / 360f;
					resourceCache = new float[resourceMapWidth, resourceMapHeight];
				}

				if (SCAN_Settings_Config.Instance.Interpolation != resourceInterpolation)
				{
					resourceInterpolation = SCAN_Settings_Config.Instance.Interpolation;
				}
			}

			for (int i = 0; i < resourceMapWidth; i++)
			{
				for (int j = 0; j < resourceMapHeight; j++)
				{
					resourceCache[i, j] = 0;
				}
			}
		}

		/* MAP: export: PNG file */
		private SCANmapExporter exporter;

		internal void exportPNG()
		{
			if (exporter == null)
			{
				UnityEngine.GameObject obj = new GameObject();

				exporter = obj.gameObject.AddComponent<SCANmapExporter>();
			}

			if (exporter.Exporting)
			{
				return;
			}

			exporter.exportPNG(this, data);
		}

		#endregion

		#region Big Map Texture Generator

		// Pixels this map needs across 360 degrees of longitude at its current scale - what the Visual
		// source has to cover: the big map's width, or the zoom map's scale times 360.
		private int visualTargetWidth()
		{
			return Mathf.Max(1, Mathf.CeilToInt((float)(mapscale * 360.0)));
		}

		// True when the GPU compositor is expected to render this map, so the readable CPU copy
		// can be skipped. Mirrors tryRenderVisualGPU's eligibility: Visual mode, resource overlay
		// off, composite shader present, and the body's ScaledSpace source textures ready.
		private bool willRenderGPU(mapType m)
		{
			if (SCAN_UI_Loader.VisualCompositeShader == null || body == null || data == null || SCANcontroller.controller == null)
				return false;
			switch (m)
			{
				// The setting disables Visual maps outright (the CPU path only honoured it by accident). A
				// body without a source texture still renders on the GPU: the shader draws unscanned (_HasSource).
				case mapType.Visual: return SCAN_Settings_Config.Instance.VisibleMapsActive;
				// The data modes need the caches: geographic for the big map (setWidth), pixel space for a
				// window map (setSize). A body without PQS or without a biome map still renders on the GPU:
				// the shader draws the CPU renderers' black-white static there (_NoData).
				case mapType.Altimetry:
				case mapType.Slope: return big_heightmap != null;
				case mapType.Biome: return biome_indexmap != null;
				default: return false;
			}
		}

		// Renders the Visual map on the GPU from the body's Visual source (cfg-declared files at a
		// fitting mip, or its resident ScaledSpace textures) - no readable CPU copy anywhere. Returns false (CPU
		// fallback) when not eligible - see willRenderGPU.
		private bool tryRenderGPU()
		{
			if (!willRenderGPU(mType))
				return false;

			Shader shader = SCAN_UI_Loader.VisualCompositeShader;

			// (source material / useMaterial flag are for a later gas-giant/Parallax pass)
			SCANcontroller.controller.getVisualSource(body, visualTargetWidth(), out Texture colorTex, out Texture normalTex, out int normalYChannel, out _);

			if (compositeMaterial == null || compositeMaterial.shader != shader)
				compositeMaterial = new Material(shader);

			if (visualRenderTex == null || visualRenderTex.width != mapwidth || visualRenderTex.height != mapheight)
			{
				if (visualRenderTex != null)
				{
					// Release() frees the GPU surface but not the RT object; Destroy the wrapper too
					// or the old RenderTexture leaks until finalization on every resize.
					visualRenderTex.Release();
					UnityEngine.Object.Destroy(visualRenderTex);
				}

				visualRenderTex = new RenderTexture(mapwidth, mapheight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
				visualRenderTex.wrapMode = TextureWrapMode.Clamp;
				clearGpuRenderTex();   // rows ahead of the sweep show whatever the RT holds, so start it on background
			}

			updateCoverageFlags();

			compositeMaterial.SetTexture("_ScaledColor", colorTex);
			compositeMaterial.SetTexture("_ScaledNormal", normalTex);
			compositeMaterial.SetTexture("_CoverageFlags", coverageFlags);

			compositeMaterial.SetFloat("_MapWidth", mapwidth);
			compositeMaterial.SetFloat("_MapHeight", mapheight);
			compositeMaterial.SetFloat("_MapScale", (float)mapscale);
			compositeMaterial.SetFloat("_LonOffset", (float)lon_offset);
			compositeMaterial.SetFloat("_LatOffset", (float)lat_offset);
			compositeMaterial.SetFloat("_Projection", (float)(int)projection);
			compositeMaterial.SetFloat("_CenteredLon", (float)centeredLong);
			compositeMaterial.SetFloat("_CenteredLat", (float)centeredLat);
			compositeMaterial.SetFloat("_FlipY", 0f);
			compositeMaterial.SetFloat("_ColorMode", colorMap ? 1f : 0f);
			compositeMaterial.SetFloat("_HasNormal", normalTex != null ? 1f : 0f);
			compositeMaterial.SetFloat("_NormalYChannel", normalYChannel);
			compositeMaterial.SetFloat("_Terminator", terminator ? 1f : 0f);
			compositeMaterial.SetFloat("_SunLonCenter", (float)sunLonCenter);
			compositeMaterial.SetFloat("_SunLatCenter", (float)sunLatCenter);
			compositeMaterial.SetFloat("_Gamma", (float)gamma);

			// Data-texture addressing and the classic-renderer details the shader reproduces. cache=true
			// is the big map: its elevation cache is geographic over the globe. cache=false is a window
			// map (zoom, RPM, the small map's helper): pixel-space caches filled per rendered pixel. The
			// resource cache is pixel space whenever generateResourceCache ran over the map's raw window
			// (it unprojects for Orthographic, and a window map's raw window is not the globe).
			compositeMaterial.SetFloat("_ElevPixelSpace", cache ? 0f : 1f);
			compositeMaterial.SetFloat("_ResPixelSpace", (!cache || projection == MapProjection.Orthographic) ? 1f : 0f);
			compositeMaterial.SetFloat("_RowMin", startLine);
			compositeMaterial.SetFloat("_RowMax", stopLine);
			compositeMaterial.SetFloat("_Grid", 0f);
			compositeMaterial.SetFloat("_HasSource", colorTex != null ? 1f : 0f);
			compositeMaterial.SetFloat("_NoData", gpuNoData() ? 1f : 0f);
			compositeMaterial.SetFloat("_NoiseSeed", noiseSeed);
			compositeMaterial.SetFloat("_SweepBand", 2f);

			Color unscanned = SCAN_Settings_Config.Instance.UnscannedColor;
			unscanned.a *= SCAN_Settings_Config.Instance.UnscannedTransparency;
			compositeMaterial.SetColor("_UnscannedColor", unscanned);
			compositeMaterial.SetColor("_ClearColor", palette.Clear);
				Color greyCol = palette.Grey; greyCol.a = 1f;
				compositeMaterial.SetColor("_GreyColor", greyCol);

				// Mode select + the non-Visual data textures / LUTs / overlay uniforms.
				compositeMaterial.SetFloat("_MapMode", (float)(int)mType);
				setModeUniforms();

			// Cosmetic reveal fraction. Re-compositing each frame with a new fraction animates the
			// RawImage - already pointed at visualRenderTex - in place. Rows ahead of the line keep the
			// previous pass (shader discard); the RT was filled with the background only when created,
			// like the CPU path's fresh Texture2D. Redline = palette.Red.
			// The line is paced by time (SweepDuration) but never runs ahead of the rows built: Visual and
			// the recolour re-sweep have their end state at once, so they always get the full timed sweep;
			// a data pass (buildGpuDataFrame) is clamped to mapstep / mapheight, so a warm cache sweeps in
			// SweepDuration and a cold one shows the line at the real sampling pace.
			float reveal = 1f;
			if (mapheight > 0)
			{
				reveal = timedSweepFraction();
				if (mType != mapType.Visual && !gpuRecolorSweep)
					reveal = Mathf.Min(reveal, Mathf.Clamp01(mapstep / (float)mapheight));
			}

			Color background = SCAN_Settings_Config.Instance.MapBackgroundColor;
			background.a *= SCAN_Settings_Config.Instance.BackgroundTransparency;
			compositeMaterial.SetColor("_MapBackgroundColor", background);
			compositeMaterial.SetColor("_RedlineColor", palette.Red);
			compositeMaterial.SetFloat("_SweepY", reveal);

			Graphics.Blit(null, visualRenderTex, compositeMaterial);

			gpuRendered = true;                       // DisplayTexture returns the RT during the sweep
			if (reveal >= 1f)
			{
				gpuSweepDone = true;                  // that Blit was the fully revealed one (no redline)
				mapstep = mapheight;                  // mark complete for the legacy mapstep-based checks
			}
			return true;
		}

		// Wall-clock reveal fraction for a sweep whose end state is already rendered. The clock starts
		// at the pass's first composite, not at resetMap, so a map reset while hidden still plays its
		// sweep when shown. Real time: physics warp scales Time.deltaTime, and the UI runs while paused.
		private float timedSweepFraction()
		{
			float now = Time.realtimeSinceStartup;
			if (sweepStart < 0f)
				sweepStart = now;
			return Mathf.Clamp01((now - sweepStart) / SweepDuration);
		}

		/// <summary>
		/// Render this Visual map again at up to <paramref name="width"/> pixels wide into a fresh
		/// RenderTexture (caller releases it), for PNG export. Only Visual is texture-backed, so only
		/// Visual gains anything from a larger render; the source mip is raised to match. Returns null
		/// when that does not apply or the request is not larger than the on-screen map.
		/// </summary>
		internal RenderTexture renderVisualExport(int width)
		{
			if (mType != mapType.Visual || !gpuRendered || compositeMaterial == null || body == null || SCANcontroller.controller == null)
			{
				return null;
			}

			int w = Mathf.Min(width, 16384);

			if (w <= mapwidth || mapwidth <= 0)
			{
				return null;
			}

			double k = (double)w / mapwidth;
			int h = Mathf.Max(1, Mathf.RoundToInt((float)(mapheight * k)));
			int sourceWidth = Mathf.CeilToInt((float)(mapscale * k * 360.0));

			if (!SCANcontroller.controller.getVisualSource(body, sourceWidth, out Texture colorTex, out Texture normalTex, out int normalYChannel, out _))
			{
				return null;
			}

			RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			rt.wrapMode = TextureWrapMode.Clamp;
			rt.Create();

			// Same window, k times the pixels: scale the size and the pixels-per-degree together. Every
			// other uniform is still set from the last on-screen render; the next tryRenderGPU resets
			// all of them, so nothing here needs restoring.
			compositeMaterial.SetTexture("_ScaledColor", colorTex);
			compositeMaterial.SetTexture("_ScaledNormal", normalTex);
			compositeMaterial.SetFloat("_HasNormal", normalTex != null ? 1f : 0f);
			compositeMaterial.SetFloat("_NormalYChannel", normalYChannel);
			compositeMaterial.SetFloat("_MapWidth", w);
			compositeMaterial.SetFloat("_MapHeight", h);
			compositeMaterial.SetFloat("_MapScale", (float)(mapscale * k));
			compositeMaterial.SetFloat("_SweepY", 1f);

			Graphics.Blit(null, rt, compositeMaterial);

			SCANUtil.SCANlog("[{0}] Visual export rendered at {1}x{2} (on-screen {3}x{4})", body.bodyName, w, h, mapwidth, mapheight);
			return rt;
		}

		// Uploads the coverage bitmask as a 360x180 texture the composite shader samples as a
		// per-pixel stencil: R=VisualHiRes, G=VisualLoRes, B=ResourceHiRes, A=ResourceLoRes.
		// GPU mode data helpers:

		// Create the GPU RenderTexture (cleared to background) + set gpuRendered so DisplayTexture
		// returns it immediately - before tryRenderGPU has real data. Fixes the updateMap timing for
		// non-Visual modes: their tryRenderGPU runs in the getPartialMap branch (after the prep loop),
		// which is AFTER the BigMap pump consumes updateMap on the mode-switch frame, so without this
		// the RawImage stays pointed at the never-painted CPU map texture and shows blank. (Visual
		// primes via its own tryRenderGPU at the top of getPartialMap.)
		private void primeGpuRenderTex()
		{
			if (visualRenderTex == null || visualRenderTex.width != mapwidth || visualRenderTex.height != mapheight)
			{
				if (visualRenderTex != null) { visualRenderTex.Release(); UnityEngine.Object.Destroy(visualRenderTex); }
				visualRenderTex = new RenderTexture(mapwidth, mapheight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
				visualRenderTex.wrapMode = TextureWrapMode.Clamp;
				clearGpuRenderTex();
			}
			gpuRendered = true;
		}

		// Fill the RenderTexture with the map background, as the CPU path fills a freshly created
		// Texture2D. Creation and resize only: a pass overwrites rows under the scanline and leaves
		// the rest standing (the shader discards ahead of the sweep), which is how every map behaved
		// on the CPU - nothing is cleared before scanning.
		private void clearGpuRenderTex()
		{
			RenderTexture prev = RenderTexture.active;
			RenderTexture.active = visualRenderTex;
			Color bg = SCAN_Settings_Config.Instance.MapBackgroundColor;
			bg.a *= SCAN_Settings_Config.Instance.BackgroundTransparency;
			GL.Clear(false, true, bg);
			RenderTexture.active = prev;
		}

		// Per-mode data textures + uniforms for tryRenderGPU. Visual's ScaledSpace textures are set by
		// the caller; here we upload the CPU-cache data for Altimetry/Slope/Biome + the resource overlay.
		private void setModeUniforms()
		{
			if (mType == mapType.Altimetry || mType == mapType.Slope)
			{
				compositeMaterial.SetTexture("_ElevationTex", elevationTex);   // uploaded incrementally per row in getPartialMap

				float tMin, tRange;
				if (useCustomRange) { tMin = customMin; tRange = customRange; }
				else { SCANterrainConfig tc = SCANUtil.getTerrainConfig(data); tMin = tc.MinTerrain; tRange = tc.MaxTerrain - tc.MinTerrain; }
				if (tRange <= 0f) tRange = 1f;
				compositeMaterial.SetFloat("_TerrainMin", tMin);
				compositeMaterial.SetFloat("_TerrainRange", tRange);

				if (mType == mapType.Altimetry)
				{
					buildPaletteLUT(tMin, tRange);
					compositeMaterial.SetTexture("_PaletteLUT", paletteLUT);
					compositeMaterial.SetTexture("_PaletteGreyLUT", paletteGreyLUT);
				}
				else
				{
					compositeMaterial.SetFloat("_SlopeCutoff", SCAN_Settings_Config.Instance.SlopeCutoff);
					compositeMaterial.SetColor("_SlopeLoColorOne", SCANcontroller.controller.lowSlopeColorOne32);
					compositeMaterial.SetColor("_SlopeHiColorOne", SCANcontroller.controller.highSlopeColorOne32);
					compositeMaterial.SetColor("_SlopeLoColorTwo", SCANcontroller.controller.lowSlopeColorTwo32);
					compositeMaterial.SetColor("_SlopeHiColorTwo", SCANcontroller.controller.highSlopeColorTwo32);
				}
			}
			else if (mType == mapType.Biome)
			{
				compositeMaterial.SetTexture("_BiomeIndexTex", biomeIndexTex);   // uploaded incrementally per row in getPartialMap
				compositeMaterial.SetColor("_LowBiomeColor", SCANcontroller.controller.lowBiomeColor32);
				compositeMaterial.SetColor("_HighBiomeColor", SCANcontroller.controller.highBiomeColor32);
				compositeMaterial.SetFloat("_BiomeTransparency", SCAN_Settings_Config.Instance.BiomeTransparency);
				bool border = mSource == mapSource.BigMap ? SCAN_Settings_Config.Instance.BigMapBiomeBorder : SCAN_Settings_Config.Instance.ZoomMapBiomeBorder;
				compositeMaterial.SetFloat("_BiomeBorder", border ? 1f : 0f);
				buildBiomeLUT();
				compositeMaterial.SetTexture("_BiomeLUT", biomeLUT);
				compositeMaterial.SetFloat("_BiomeCount", biomeLUTCount);
				compositeMaterial.SetFloat("_StockBiomes", (SCAN_Settings_Config.Instance.BigMapStockBiomes && colorMap) ? 1f : 0f);
				// elevation underlay: biome blends its colour with grey elevation by BiomeTransparency
				compositeMaterial.SetTexture("_ElevationTex", elevationTex);
				SCANterrainConfig tc = SCANUtil.getTerrainConfig(data);
				float bRange = tc.MaxTerrain - tc.MinTerrain;
				compositeMaterial.SetFloat("_TerrainMin", tc.MinTerrain);
				compositeMaterial.SetFloat("_TerrainRange", bRange <= 0f ? 1f : bRange);
			}

			bool resOn = resourceActive && SCANconfigLoader.GlobalResource && resource != null;
			compositeMaterial.SetFloat("_ResourceActive", resOn ? 1f : 0f);
			if (resOn)
			{
				if (!resourceCacheReady) { buildResourceCache(); resourceCacheReady = true; }   // GPU paths skip the prep that builds it
				if (!resourceTexReady) { uploadResourceTexture(); resourceTexReady = true; }
				compositeMaterial.SetTexture("_ResourceTex", resourceTex);
				float minR = useCustomRange ? customResourceMin : resource.CurrentBody.MinValue;
				float maxR = useCustomRange ? customResourceMax : resource.CurrentBody.MaxValue;
				compositeMaterial.SetColor("_ResMinColor", resource.MinColor32);
				compositeMaterial.SetColor("_ResMaxColor", resource.MaxColor32);
				compositeMaterial.SetFloat("_ResMinRange", minR);
				compositeMaterial.SetFloat("_ResMaxRange", maxR);
				compositeMaterial.SetFloat("_ResTransparency", resource.Transparency / 100f);
			}
		}

		// Config key (body/mode/projection/size/offsets). If it matches the value cached when the data
		// finished sampling, the change was colourisation-only (palette/clamp/terminator) and we can
		// instant-recolour from the cached data textures instead of re-sweeping the whole map.
		private int gpuConfigHash()
		{
			int h = body != null ? body.flightGlobalsIndex : -1;
			h = h * 31 + (int)mType;
			h = h * 31 + (int)projection;
			h = h * 31 + mapwidth;
			h = h * 31 + mapheight;
			h = h * 31 + mapscale.GetHashCode();   // a window map's zoom level
			h = h * 31 + lon_offset.GetHashCode();
			h = h * 31 + lat_offset.GetHashCode();
			h = h * 31 + centeredLat.GetHashCode();
			h = h * 31 + centeredLong.GetHashCode();
			// Coverage growth (live scanning) must defeat the instant-recolour shortcut: the cached data
			// textures hold no samples for newly covered pixels - they upload as 0 m, which the LoRes
			// grey ramp draws nearly black. A real pass samples just the new pixels. 64,800 shorts, per reset.
			h = h * 31 + coverageChecksum();
			return h;
		}

		private int coverageChecksum()
		{
			if (data == null || data.Coverage == null)
			{
				return 0;
			}

			Int16[,] cov = data.Coverage;
			int sum = 0;

			for (int x = 0; x < 360; x++)
			{
				for (int y = 0; y < 180; y++)
				{
					sum = unchecked(sum + cov[x, y]);
				}
			}

			return sum;
		}

		// Ensure the mode's R-float data texture exists (cleared to 0). Rows are then staged by
		// stageDataRow as the prep fills the cache and uploaded once per frame (buildGpuDataFrame).
		private void ensureDataTex(ref Texture2D tex)
		{
			if (tex != null && tex.width == mapwidth && tex.height == mapheight) return;
			if (tex != null) UnityEngine.Object.Destroy(tex);
			tex = new Texture2D(mapwidth, mapheight, TextureFormat.RFloat, false);
			tex.wrapMode = TextureWrapMode.Clamp;
			if (gpuDataBuf == null || gpuDataBuf.Length != mapwidth * mapheight)
				gpuDataBuf = new Color[mapwidth * mapheight];
			System.Array.Clear(gpuDataBuf, 0, gpuDataBuf.Length);
			tex.SetPixels(gpuDataBuf);
			tex.Apply(false);
		}

		// Stage one geographic row (src column y=row) into the data texture's CPU mirror. No Apply
		// here: Apply re-uploads the whole texture, so the caller does it once per frame.
		private void stageDataRow(Texture2D tex, float[,] src, int row)
		{
			if (tex == null || src == null || row < 0 || row >= mapheight) return;
			if (gpuRowBuf == null || gpuRowBuf.Length != mapwidth)
				gpuRowBuf = new Color[mapwidth];
			for (int x = 0; x < mapwidth; x++)
				gpuRowBuf[x] = new Color(src[x, row], 0f, 0f, 0f);
			tex.SetPixels(0, row, mapwidth, 1, gpuRowBuf);
		}

		// Upload resourceCache (geographic resW x resH) as an R-float abundance texture (fraction 0..1).
		// Build resourceCache (stock abundance) - the GPU paths (Visual short-circuit, fake-sweep fast
		// path) skip the prep loop that normally builds it, and resetResourceMap clears it every reset.
		private void buildResourceCache()
		{
			SCANuiUtil.generateResourceCache(ref resourceCache, resourceMapHeight, resourceMapWidth, resourceInterpolation, resourceMapScale, this);
			System.Random rr = new System.Random(ResourceScenario.Instance.gameSettings.Seed);
			for (int i = resourceInterpolation / 2; i >= 1; i /= 2)
			{
				SCANuiUtil.interpolate(resourceCache, resourceMapHeight, resourceMapWidth, i, i, i, rr, randomEdges, mSource == mapSource.ZoomMap);
				SCANuiUtil.interpolate(resourceCache, resourceMapHeight, resourceMapWidth, 0, i, i, rr, randomEdges, mSource == mapSource.ZoomMap);
				SCANuiUtil.interpolate(resourceCache, resourceMapHeight, resourceMapWidth, i, 0, i, rr, randomEdges, mSource == mapSource.ZoomMap);
			}
		}

		private void uploadResourceTexture()
		{
			if (resourceCache == null) return;
			if (resourceTex == null || resourceTex.width != resourceMapWidth || resourceTex.height != resourceMapHeight)
			{
				if (resourceTex != null) UnityEngine.Object.Destroy(resourceTex);
				resourceTex = new Texture2D(resourceMapWidth, resourceMapHeight, TextureFormat.RFloat, false);
				resourceTex.wrapMode = TextureWrapMode.Clamp;
			}
			Color[] buf = new Color[resourceMapWidth * resourceMapHeight];
			for (int y = 0; y < resourceMapHeight; y++)
				for (int x = 0; x < resourceMapWidth; x++)
					buf[y * resourceMapWidth + x] = new Color(resourceCache[x, y] / 100f, 0f, 0f, 0f);
			resourceTex.SetPixels(buf);
			resourceTex.Apply(false);
		}

		// Bake heightToColor across [min, min+range] into a 1-D LUT so the shader is a plain fetch and
		// the map matches the legend (which calls the same heightToColor) by construction.
		// 1-D LUT of the body's stock biome mapColors, indexed by the biome fraction. Cached per body.
		private void buildBiomeLUT()
		{
			if (body.BiomeMap == null) return;
			int n = body.BiomeMap.Attributes.Length;
			if (biomeLUT != null && biomeLUTCount == n && biomeLUTBody == body) return;
			if (biomeLUT != null) UnityEngine.Object.Destroy(biomeLUT);
			int w = Mathf.Max(n, 1);
			biomeLUT = new Texture2D(w, 1, TextureFormat.RGBA32, false);
			biomeLUT.filterMode = FilterMode.Point;
			biomeLUT.wrapMode = TextureWrapMode.Clamp;
			Color[] c = new Color[w];
			for (int i = 0; i < n; i++) c[i] = SCANUtil.getBiomeDisplayColor(body, i);   // one colour source for map, legend and tooltips (grayscale-encoded biome maps get a generated palette)
			biomeLUT.SetPixels(c);
			biomeLUT.Apply(false);
			biomeLUTCount = n;
			biomeLUTBody = body;
		}

		private void buildPaletteLUT(float min, float range)
		{
			SCANterrainConfig tc = SCANUtil.getTerrainConfig(data);
			int hash = tc.ColorPal.Hash ^ (colorMap ? 1 : 0) ^ min.GetHashCode() ^ range.GetHashCode() ^ (useCustomRange ? 2 : 0);
			if (paletteLUT != null && paletteLUTHash == hash)
				return;
			if (paletteLUT == null)
			{
				paletteLUT = new Texture2D(1024, 1, TextureFormat.RGBA32, false);
				paletteLUT.wrapMode = TextureWrapMode.Clamp;
			}
			if (paletteGreyLUT == null)
			{
				paletteGreyLUT = new Texture2D(1024, 1, TextureFormat.RGBA32, false);
				paletteGreyLUT.wrapMode = TextureWrapMode.Clamp;
			}
			Color[] lut = new Color[1024];
			Color[] grey = new Color[1024];
			for (int x = 0; x < 1024; x++)
			{
				float val = min + (x / 1023f) * range;
				lut[x] = useCustomRange
					? (Color)palette.heightToColor(val, colorMap, tc, customMin, customMax, customRange, true)
					: (Color)palette.heightToColor(val, colorMap, tc);
				grey[x] = useCustomRange
					? (Color)palette.heightToColor(val, false, tc, customMin, customMax, customRange, true)
					: (Color)palette.heightToColor(val, false, tc);
			}
			paletteLUT.SetPixels(lut);
			paletteLUT.Apply(false);
			paletteGreyLUT.SetPixels(grey);
			paletteGreyLUT.Apply(false);
			paletteLUTHash = hash;
		}

		private void updateCoverageFlags()
		{
			bool created = false;
			if (coverageFlags == null)
			{
				coverageFlags = new Texture2D(360, 180, TextureFormat.RGBA32, false);
				coverageFlags.filterMode = FilterMode.Point;
				coverageFlags.wrapMode = TextureWrapMode.Clamp;
				created = true;
			}

			// Coverage only changes between render passes (resetMap sets coverageFlagsDirty), never
			// within the per-row sweep - so rebuild + GPU-upload once per pass instead of on every
			// tryRenderGPU call. The CPU buffer is a reused member, so the sweep allocates nothing.
			if (!created && !coverageFlagsDirty)
				return;

			if (coverageFlagsBuf == null)
				coverageFlagsBuf = new Color32[360 * 180];

			Int16[,] cov = data.Coverage;

			for (int x = 0; x < 360; x++)
			{
				for (int y = 0; y < 180; y++)
				{
					// Pack the raw 16-bit coverage: R = low byte, G = high byte. The shader decodes
					// cov = round(R*255) + 256*round(G*255) and tests any SCANtype bit (covHas). This
					// exposes every bit (Altimetry 0/1, VisualLoRes 2, Biome 3, VisualHiRes 6,
					// Resource 7/8) with one texture, vs the old 4-bit RGBA pack.
					int c = cov[x, y];
					coverageFlagsBuf[y * 360 + x] = new Color32((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), 0, 255);
				}
			}

			coverageFlags.SetPixels32(coverageFlagsBuf);
			coverageFlags.Apply(false);
			coverageFlagsDirty = false;
		}

		// Per-frame CPU budget for building a GPU data pass, by MapGenerationSpeed. Replaces the
		// one-row-per-call cadence for the GPU data modes; the CPU renderer still goes by rows per call.
		private static double gpuBuildBudgetMs()
		{
			switch (SCAN_Settings_Config.Instance.MapGenerationSpeed)
			{
				case 1: return 2.0;
				case 3: return 8.0;
				default: return 4.0;
			}
		}

		private void clearBiomeRowCache()
		{
			if (biomeRowCached != null)
				System.Array.Clear(biomeRowCached, 0, biomeRowCached.Length);
		}

		// A window map's (cache=false) elevation and biome caches are in pixel space, valid for one
		// window: body, size, projection, centre and zoom. Any of those changing drops them.
		private void clearWindowCaches()
		{
			if (big_heightmap != null)
				System.Array.Clear(big_heightmap, 0, big_heightmap.Length);
			clearBiomeRowCache();
			gpuDataComplete = false;
		}

		// Bodies the CPU renderers drew as black-white static: no PQS for Altimetry/Slope, no biome
		// map for Biome. The shader does the same (_NoData); there is nothing to build for them.
		private bool gpuNoData()
		{
			if (mType == mapType.Biome)
				return !biomeMap;
			return (mType == mapType.Altimetry || mType == mapType.Slope) && !pqs;
		}

		// One CPU budget per frame shared by every map that is building (the big map, the zoom map and
		// the small map can all be open at once): each takes what is left, and always at least one
		// step, so all of them progress and together they cost what the setting says.
		private static int budgetFrame = -1;
		private static long budgetUsedTicks;

		// The CPU sampling for row mapstep: the elevation look-ahead into big_heightmap (row mapstep+1)
		// and, in Biome mode, biomeIndex for the current row. buildGpuDataFrame stages the results into
		// the data textures; the shader does all the colourising.
		private void prepRow()
		{
			bool mapHidden = mapstep < startLine || mapstep > stopLine;

			// Biome rows are cached in biome_indexmap (per body / size / projection, like the height
			// cache): a row already sampled skips the per-pixel biome lookups entirely.
			bool biomeRowDone = biomeRowCached != null && mapstep >= 0 && mapstep < biomeRowCached.Length && biomeRowCached[mapstep];

			for (int i = 0; i < mapwidth; i++)
			{
				/* Introduce altimetry check here; Use unprojected lat/long coordinates
				 * All cached altimetry data stored in a single 2D array in rectangular format
				 * Pull altimetry data from cache after unprojection
				 */


				double cacheLat = ((mapstep + 1) * 1.0f / mapscale) - 90f + lat_offset;
				double lon = (i * 1.0f / mapscale) - 180f + lon_offset;

				if (mType != mapType.Visual)
				{
					int lookAhead = mapstep + 1;
					bool lookAheadHidden = lookAhead < startLine || lookAhead > stopLine;   // RPM reserved rows: the shader draws them clear

					if (body.pqsController != null && big_heightmap != null && lookAhead < mapheight && !lookAheadHidden)
					{
						if (big_heightmap[i, lookAhead] == 0f)
						{
							// The big map's cache (cache=true) is geographic: its raw grid is the globe, so the raw
							// coords are the sample coords and the shader reads it through the unprojected lon/lat.
							// A window map's cache is pixel space: sample at this pixel's unprojected coordinate,
							// which is where the shader (pixel uv) expects it.
							double sampleLon = lon, sampleLat = cacheLat;
							bool onMap = true;

							if (!cache)
							{
								sampleLat = unprojectLatitude(lon, cacheLat);
								sampleLon = unprojectLongitude(lon, cacheLat);
								onMap = !(double.IsNaN(sampleLat) || double.IsNaN(sampleLon) || sampleLat < -90 || sampleLat > 90 || sampleLon < -180 || sampleLon > 180);
							}

							if (onMap && SCANUtil.isCovered(sampleLon, sampleLat, data, SCANtype.Altimetry))
							{
								terrainHeightToArray(sampleLon, sampleLat, i, lookAhead);
							}
						}
					}
				}

				if (mapstep < 0)
				{
					continue;
				}

				if (mapHidden)
				{
					continue;
				}

				if (mType != mapType.Biome || !biomeMap || biomeRowDone)
				{
					continue;
				}

				double lat = (mapstep * 1.0f / mapscale) - 90f + lat_offset;
				double la = lat, lo = lon;
				lat = unprojectLatitude(lo, la);
				lon = unprojectLongitude(lo, la);

				if (double.IsNaN(lat) || double.IsNaN(lon) || lat < -90 || lat > 90 || lon < -180 || lon > 180)
				{
					biomeIndex[i] = 0;
					continue;
				}

				// One biome lookup per pixel: the shader colourises from _BiomeLUT[biomeIndex] (stock
				// colours or the low/high gradient) and finds the borders in the index texture itself.
				passBiomeSamples++;
				biomeIndex[i] = SCANUtil.getBiomeIndexFraction(body, lon, lat);
			}
		}

		// GPU data modes (Altimetry / Slope / Biome on the cache=true big map). Builds the pass under a
		// per-frame CPU budget instead of one row per pump call, stages the rows into the data textures
		// and uploads each texture once, then composites once. tryRenderGPU keeps the reveal at the
		// smaller of the timed sweep and the rows built, so a warm cache sweeps in SweepDuration and a
		// cold one shows the line at the real sampling pace. The resource cache is not built here: the
		// first composite builds it lazily (setModeUniforms -> buildResourceCache), as for Visual.
		private void buildGpuDataFrame()
		{
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			if (budgetFrame != Time.frameCount)
			{
				budgetFrame = Time.frameCount;
				budgetUsedTicks = 0;
			}
			long budget = (long)(gpuBuildBudgetMs() * System.Diagnostics.Stopwatch.Frequency / 1000.0) - budgetUsedTicks;
			bool elevDirty = false, biomeDirty = false, builtNow = false;

			if (mapstep < -1)
				mapstep = -1;   // the -2 step is the CPU path's resource cache build

			if (gpuNoData() && mapstep < mapheight)
				mapstep = mapheight;   // nothing to build; the shader draws static and the sweep runs on its own

			if (biomeRowCached == null || biomeRowCached.Length != mapheight)
				biomeRowCached = new bool[mapheight];

			if (mapstep < mapheight)
			{
				if (passBuildStart < 0f)
					passBuildStart = Time.realtimeSinceStartup;
				passBuildFrames++;
			}

			// At least one step per frame, however long it takes, then as many as fit the budget.
			while (mapstep < mapheight)
			{
				prepRow();   // at mapstep -1 this is the look-ahead that fills big_heightmap row 0

				if (mapstep >= 0)
				{
					// Altimetry/Slope: big_heightmap row mapstep+1 is the look-ahead just filled (row 0 came
					// at mapstep -1, so stage both at mapstep 0). Biome: biomeIndex is the current row - or,
					// for a cached row, biome_indexmap already holds it - plus the elevation rows for its underlay.
					if (mType == mapType.Biome)
					{
						if (!biomeRowCached[mapstep])
						{
							for (int bi = 0; bi < mapwidth; bi++)
								biome_indexmap[bi, mapstep] = (float)biomeIndex[bi];
							biomeRowCached[mapstep] = true;
						}
						ensureDataTex(ref biomeIndexTex);
						stageDataRow(biomeIndexTex, biome_indexmap, mapstep);
						biomeDirty = true;
					}
					ensureDataTex(ref elevationTex);
					if (mapstep == 0) stageDataRow(elevationTex, big_heightmap, 0);
					stageDataRow(elevationTex, big_heightmap, mapstep + 1);
					elevDirty = true;
				}

				mapstep++;
				if (mapstep >= mapheight)
				{
					builtNow = true;
					break;
				}
				if (System.Diagnostics.Stopwatch.GetTimestamp() - start >= budget)
					break;
			}

			if (elevDirty) elevationTex.Apply(false);
			if (biomeDirty) biomeIndexTex.Apply(false);

			budgetUsedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;

			if (builtNow)
			{
				gpuDataComplete = true;          // data cache fully sampled...
				gpuDataHash = gpuConfigHash();    // ...for this config (enables instant-recolour)
				passBuildMs = (Time.realtimeSinceStartup - passBuildStart) * 1000f;
			}

			tryRenderGPU();   // reveal = min(timed sweep, mapstep / mapheight); sets gpuSweepDone at 1

			if (gpuSweepDone && passBuildStart >= 0f)
			{
				SCANUtil.SCANlog("[{0}] {1} GPU pass {2}x{3}: build {4} frames / {5:F0} ms ({6} height samples, {7} biome lookups), pass total {8:F2} s",
					body.bodyName, mType, mapwidth, mapheight, passBuildFrames, passBuildMs, passHeightSamples, passBiomeSamples, Time.realtimeSinceStartup - passBuildStart);
				passBuildStart = -1f;   // one line per pass
			}
		}

		/* MAP: build: one pump call. The UI calls this 1-4 times per frame (MapGenerationSpeed), the last
		   with apply=true; the GPU paths act on that call only, the others are the old CPU row cadence. */
		internal void getPartialMap(bool apply = true)
		{
			if (data == null)
			{
				return;
			}

			if (mType == mapType.Visual && willRenderGPU(mapType.Visual))
			{
				// One composite per frame, on the pump's apply call; the sweep is paced by time in tryRenderGPU.
				if (apply)
					tryRenderGPU();
				return;
			}

			// Non-Visual GPU modes: point DisplayTexture at the RenderTexture up-front so the RawImage
			// tracks the GPU output on the same frame the UI consumes updateMap (the real data render
			// happens in buildGpuDataFrame, on the apply call, after the prep fills the caches).
			if (mType != mapType.Visual && !gpuRendered && willRenderGPU(mType))
				primeGpuRenderTex();

			// Cosmetic re-sweep after a colour-only change: the data textures are already uploaded, so
			// skip the whole prep/CPU loop and just re-Blit once per frame with the rebuilt LUT while the
			// timed reveal advances - keeps the sweep charm with no PQS re-sample and no re-processing.
			if (gpuRecolorSweep)
			{
				if (apply)
					tryRenderGPU();   // sets gpuSweepDone on the fully revealed Blit
				if (gpuSweepDone)
				{
					gpuRecolorSweep = false;
					gpuDataComplete = true;
					gpuDataHash = gpuConfigHash();
					SCANUtil.SCANlog("[{0}] {1} GPU recolour pass {2}x{3}: no re-sample, sweep {4:F2} s", body.bodyName, mType, mapwidth, mapheight, Time.realtimeSinceStartup - sweepStart);
				}
				return;
			}

			// GPU data modes (Altimetry / Slope / Biome): budgeted build plus one composite per frame, on
			// the pump's apply call.
			if (mType != mapType.Visual && willRenderGPU(mType))
			{
				if (apply)
					buildGpuDataFrame();
				return;
			}

			// Nothing renders this map: the composite shader is missing or unsupported on this graphics
			// device (SCAN_UI_Loader logged which), or Visual maps are disabled in the settings. There is
			// no CPU renderer any more, so mark the pass complete, say so once per pass, and leave
			// DisplayTexture null.
			if (mapstep < mapheight)
			{
				mapstep = mapheight;

				if (!noRenderLogged)
				{
					noRenderLogged = true;
					SCANUtil.SCANlog("[{0}] {1} map not rendered: composite shader unavailable{2}", body.bodyName, mType,
						mType == mapType.Visual && !SCAN_Settings_Config.Instance.VisibleMapsActive ? " (or Visual maps disabled)" : "");
				}
			}
		}

		/* Calculates the terrain elevation based on scanning coverage; fetches data from elevation cache if possible */
		private float terrainElevation(double Lon, double Lat, int w, int h, float[,] heightMap, bool c, SCANdata Data, out bool NowColor, bool exporting = false)
		{
			float elevation = 0f;
			NowColor = colorMap;
			if (SCANUtil.isCovered(Lon, Lat, Data, SCANtype.AltimetryHiRes))
			{
				if (c)
				{
					double lon = fixUnscale(unScaleLongitude(Lon), w);
					double lat = fixUnscale(unScaleLatitude(Lat), h);

					int ilon = Mathf.RoundToInt((float)lon);
					int ilat = Mathf.RoundToInt((float)lat);

					if (ilon >= w)
					{
						ilon = w - 1;
					}

					if (ilat >= h)
					{
						ilat = h - 1;
					}

					elevation = heightMap[ilon, ilat];
					if (elevation == 0f && !exporting)
					{
						elevation = (float)SCANUtil.getElevation(body, Lon, Lat);
					}
				}
				else
				{
					elevation = (float)SCANUtil.getElevation(body, Lon, Lat);
				}
			}
			else
			{
				if (c)
				{
					double lon = fixUnscale(unScaleLongitude(Lon), w);
					double lat = fixUnscale(unScaleLatitude(Lat), h);

					int ilon = ((int)(lon * 5)) / 5;
					int ilat = ((int)(lat * 5)) / 5;

					if (ilon >= w)
					{
						ilon = w - 1;
					}

					if (ilat >= h)
					{
						ilat = h - 1;
					}

					elevation = heightMap[ilon, ilat];
					if (elevation == 0f && !exporting)
					{
						elevation = (float)SCANUtil.getElevation(body, ((int)(Lon * 5)) / 5, ((int)(Lat * 5)) / 5);
					}
				}
				else
				{
					elevation = (float)SCANUtil.getElevation(body, ((int)(Lon * 5)) / 5, ((int)(Lat * 5)) / 5);
				}

				NowColor = false;
			}

			return elevation;
		}

		public float terrainElevation(double Lon, double Lat, int W, int H, float[,] heightMap, SCANdata Data, bool export = false)
		{
			bool c = true;

			return terrainElevation(Lon, Lat, W, H, heightMap, true, Data, out c, export);
		}


		#endregion

	}
}
